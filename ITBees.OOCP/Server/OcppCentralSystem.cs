using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ITBees.OOCP.Messages;
using ITBees.OOCP.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ITBees.OOCP.Server;

public interface IOcppCentralSystem
{
    /// <summary>Charge points connected right now.</summary>
    IReadOnlyCollection<OcppChargePointContext> ConnectedChargePoints { get; }

    /// <summary>Case-insensitive; null when the charge point is not connected.</summary>
    OcppChargePointContext? Find(string chargePointId);

    bool IsConnected(string chargePointId);

    /// <summary>
    /// Sends a command and returns the charger's answer. Throws <see cref="OcppCallException"/> when the charger
    /// is not connected, does not answer in time or answers with a CALLERROR.
    /// </summary>
    Task<TResponse> Call<TResponse>(string chargePointId, string action, object request, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) where TResponse : class, new();

    /// <summary>Closes the connection of a charge point (it will normally connect again by itself).</summary>
    Task Disconnect(string chargePointId, string reason);

    /// <summary>Serves one WebSocket connection of <paramref name="chargePointId"/> until it closes.</summary>
    Task HandleWebSocketRequest(HttpContext context, string chargePointId);
}

/// <summary>
/// OCPP 1.6J central system: one WebSocket per charge point (the charge point is the client), calls of the charger
/// answered by <see cref="IOcppCentralSystemHandler"/>, commands sent through <see cref="Call{TResponse}"/>.
/// Register as a singleton (<c>AddOcppCentralSystem</c>) and map its endpoint (<c>UseOcppCentralSystem</c>).
/// </summary>
public sealed class OcppCentralSystem : IOcppCentralSystem
{
    private const int MaxChargePointIdLength = 100;

    private readonly IOcppCentralSystemHandler _handler;
    private readonly OcppCentralSystemOptions _options;
    private readonly ILogger<OcppCentralSystem> _logger;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.OrdinalIgnoreCase);

    public OcppCentralSystem(IOcppCentralSystemHandler handler, OcppCentralSystemOptions options, ILogger<OcppCentralSystem> logger)
    {
        _handler = handler;
        _options = options;
        _logger = logger;
    }

    public IReadOnlyCollection<OcppChargePointContext> ConnectedChargePoints =>
        _connections.Values.Where(x => x.Peer.IsOpen).Select(x => x.Context).ToList();

    public OcppChargePointContext? Find(string chargePointId) =>
        _connections.TryGetValue(chargePointId, out var connection) && connection.Peer.IsOpen ? connection.Context : null;

    public bool IsConnected(string chargePointId) => Find(chargePointId) != null;

    public async Task<TResponse> Call<TResponse>(string chargePointId, string action, object request,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default) where TResponse : class, new()
    {
        if (!_connections.TryGetValue(chargePointId, out var connection) || !connection.Peer.IsOpen)
            throw new OcppCallException(OcppCallFailure.NotConnected, $"Charge point {chargePointId} is not connected");

        var payload = await connection.Peer.Call(action, request, timeout ?? _options.CallTimeout, cancellationToken);
        try
        {
            return OcppJson.Deserialize<TResponse>(payload) ?? new TResponse();
        }
        catch (JsonException e)
        {
            throw new OcppCallException(OcppCallFailure.InvalidResponse, $"{action}: unreadable answer {payload.GetRawText()}", inner: e);
        }
    }

    public async Task Disconnect(string chargePointId, string reason)
    {
        if (_connections.TryGetValue(chargePointId, out var connection))
            await connection.Peer.CloseAsync(reason);
    }

    public async Task HandleWebSocketRequest(HttpContext context, string chargePointId)
    {
        chargePointId = (chargePointId ?? string.Empty).Trim();
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("OCPP-J central system: connect with a WebSocket (ws://host:port/path/{chargePointId}).");
            return;
        }

        if (chargePointId.Length == 0 || chargePointId.Length > MaxChargePointIdLength)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("The charge point identity is missing from the URL (ws://host:port/path/{chargePointId}).");
            return;
        }

        var requested = context.WebSockets.WebSocketRequestedProtocols;
        var subProtocol = ChooseSubProtocol(requested);
        if (subProtocol == null && (requested.Count > 0 || _options.RequireSubProtocol))
        {
            if (_options.RequireSubProtocol)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync($"Sub-protocol {string.Join(", ", _options.SupportedSubProtocols)} is required.");
                return;
            }

            // Lenient on purpose: talk OCPP 1.6 anyway, a charger that insists on another version closes by itself.
            _handler.OnError(null, $"{chargePointId}: asked for sub-protocols [{string.Join(", ", requested)}], none supported - connecting without one", null);
        }

        var connectionRequest = new OcppConnectionRequest
        {
            ChargePointId = chargePointId,
            RemoteIpAddress = context.Connection.RemoteIpAddress,
            BasicAuthPassword = ReadBasicAuthPassword(context.Request.Headers.Authorization.ToString()),
            RequestedSubProtocols = requested.ToList(),
        };

        OcppConnectionDecision decision;
        try
        {
            decision = await _handler.OnConnecting(connectionRequest);
        }
        catch (Exception e)
        {
            _handler.OnError(null, $"{chargePointId}: OnConnecting failed: {e.Message}", e);
            decision = OcppConnectionDecision.Rejected(StatusCodes.Status500InternalServerError, "Internal error");
        }

        if (!decision.Accept)
        {
            context.Response.StatusCode = decision.RejectStatusCode;
            await context.Response.WriteAsync(decision.Reason ?? "Connection refused");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = subProtocol,
            KeepAliveInterval = _options.WebSocketKeepAlive,
        });

        var chargePoint = new OcppChargePointContext(chargePointId, context.Connection.RemoteIpAddress, subProtocol);
        var connection = new Connection(chargePoint);
        connection.Peer = new OcppWebSocketPeer(socket, _options.MaxMessageBytes,
            (frame, cancellationToken) => Dispatch(chargePoint, frame),
            (direction, text) =>
            {
                chargePoint.LastMessageUtc = direction == OcppFrameDirection.Received ? DateTime.UtcNow : chargePoint.LastMessageUtc;
                _handler.OnFrame(chargePoint, direction, text);
            },
            (message, exception) => _handler.OnError(chargePoint, message, exception));

        // A charger that reconnects (power cut, network change) replaces its stale connection.
        Connection? replaced = null;
        _connections.AddOrUpdate(chargePointId, connection, (_, existing) =>
        {
            replaced = existing;
            return connection;
        });
        if (replaced != null)
            await replaced.Peer.CloseAsync("Replaced by a new connection of the same charge point");

        _logger.LogInformation("OCPP charge point {ChargePointId} connected from {RemoteIp} ({SubProtocol})",
            chargePointId, chargePoint.RemoteIpAddress, subProtocol ?? "no sub-protocol");
        await SafeHandler(chargePoint, "OnConnected", () => _handler.OnConnected(chargePoint));

        using var probeStop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var probe = Task.Run(() => IdleProbe(connection, probeStop.Token), CancellationToken.None);
        try
        {
            await connection.Peer.RunAsync(context.RequestAborted);
        }
        finally
        {
            probeStop.Cancel();
            try
            {
                await probe;
            }
            catch (OperationCanceledException)
            {
            }

            // Only remove the entry when it is still this connection (a reconnect may have replaced it already).
            _connections.TryRemove(new KeyValuePair<string, Connection>(chargePointId, connection));
            var reason = connection.Peer.CloseReason ?? "Closed";
            _logger.LogInformation("OCPP charge point {ChargePointId} disconnected: {Reason}", chargePointId, reason);
            await SafeHandler(chargePoint, "OnDisconnected", () => _handler.OnDisconnected(chargePoint, reason));
        }
    }

    private async Task<object> Dispatch(OcppChargePointContext chargePoint, OcppFrame call)
    {
        var action = call.Action ?? string.Empty;
        switch (action.ToLowerInvariant())
        {
            case "bootnotification":
                var boot = Read<BootNotificationRequest>(call);
                chargePoint.BootNotification = boot;
                return await _handler.OnBootNotification(chargePoint, boot);
            case "heartbeat":
                return await _handler.OnHeartbeat(chargePoint, Read<HeartbeatRequest>(call));
            case "statusnotification":
                return await _handler.OnStatusNotification(chargePoint, Read<StatusNotificationRequest>(call));
            case "authorize":
                return await _handler.OnAuthorize(chargePoint, Read<AuthorizeRequest>(call));
            case "starttransaction":
                return await _handler.OnStartTransaction(chargePoint, Read<StartTransactionRequest>(call));
            case "stoptransaction":
                return await _handler.OnStopTransaction(chargePoint, Read<StopTransactionRequest>(call));
            case "metervalues":
                return await _handler.OnMeterValues(chargePoint, Read<MeterValuesRequest>(call));
            case "datatransfer":
                return await _handler.OnDataTransfer(chargePoint, Read<DataTransferRequest>(call));
            case "diagnosticsstatusnotification":
                return await _handler.OnDiagnosticsStatusNotification(chargePoint, Read<DiagnosticsStatusNotificationRequest>(call));
            case "firmwarestatusnotification":
                return await _handler.OnFirmwareStatusNotification(chargePoint, Read<FirmwareStatusNotificationRequest>(call));
            // Security extension (OCPP 1.6 Security Whitepaper) - answered empty so the charger does not retry forever.
            case "securityeventnotification":
            case "logstatusnotification":
            case "signedfirmwarestatusnotification":
                return new Dictionary<string, object>();
            default:
                throw new OcppCallErrorException(OcppErrorCodes.NotImplemented, $"Action {action} is not supported");
        }
    }

    private static T Read<T>(OcppFrame call) where T : new()
    {
        try
        {
            return OcppJson.Deserialize<T>(call.Payload) ?? new T();
        }
        catch (JsonException e)
        {
            throw new OcppCallErrorException(OcppErrorCodes.FormationViolation, $"{call.Action}: {e.Message}");
        }
    }

    /// <summary>
    /// A quiet connection is asked for one configuration key; a charger that does not answer is gone (power cut,
    /// cable pulled) and its connection is closed, so the host does not show a dead charger as connected.
    /// </summary>
    private async Task IdleProbe(Connection connection, CancellationToken cancellationToken)
    {
        if (_options.IdleProbeAfter <= TimeSpan.Zero)
            return;
        var checkEvery = TimeSpan.FromSeconds(Math.Clamp(_options.IdleProbeAfter.TotalSeconds / 4, 1, 30));
        while (!cancellationToken.IsCancellationRequested && connection.Peer.IsOpen)
        {
            await Task.Delay(checkEvery, cancellationToken);
            if (DateTime.UtcNow - connection.Peer.LastReceivedUtc < _options.IdleProbeAfter)
                continue;
            try
            {
                await connection.Peer.Call(OcppActions.GetConfiguration,
                    new GetConfigurationRequest { Key = new List<string> { "HeartbeatInterval" } },
                    _options.CallTimeout, cancellationToken);
            }
            catch (OcppCallException e) when (e.Failure == OcppCallFailure.Timeout)
            {
                _handler.OnError(connection.Context, $"No answer to the keep-alive probe after {_options.IdleProbeAfter.TotalMinutes:0} min of silence - closing", null);
                await connection.Peer.CloseAsync("No answer to the keep-alive probe");
                return;
            }
            catch (OcppCallException)
            {
                // A CALLERROR is an answer too - the charger is alive.
            }
        }
    }

    private string? ChooseSubProtocol(IList<string> requested)
    {
        foreach (var supported in _options.SupportedSubProtocols)
        {
            var match = requested.FirstOrDefault(x => x.Trim().Equals(supported, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match.Trim();
        }

        // "ocpp1.6j", "OCPP1.6J" and similar spellings.
        return requested.FirstOrDefault(x => x.Trim().StartsWith("ocpp1.6", StringComparison.OrdinalIgnoreCase))?.Trim();
    }

    internal static string? ReadBasicAuthPassword(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var separator = decoded.IndexOf(':');
            return separator < 0 ? null : decoded[(separator + 1)..];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task SafeHandler(OcppChargePointContext chargePoint, string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            _handler.OnError(chargePoint, $"{name} failed: {e.Message}", e);
        }
    }

    private sealed class Connection
    {
        public Connection(OcppChargePointContext context)
        {
            Context = context;
        }

        public OcppChargePointContext Context { get; }
        public OcppWebSocketPeer Peer { get; set; } = null!;
    }
}
