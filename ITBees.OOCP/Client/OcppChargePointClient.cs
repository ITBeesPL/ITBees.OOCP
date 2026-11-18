using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ITBees.OOCP.Messages;
using ITBees.OOCP.Protocol;
using ITBees.OOCP.Server;

namespace ITBees.OOCP.Client;

/// <summary>
/// The charge point side of OCPP 1.6J - enough to simulate a wallbox: the tests and the simulator tool use it to
/// check a central system without real hardware. Commands from the central system go to <see cref="CommandHandler"/>
/// (default: every command accepted).
/// </summary>
public sealed class OcppChargePointClient : IAsyncDisposable
{
    private OcppWebSocketPeer? _peer;
    private Task? _runTask;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Answers a command of the central system: action and payload in, answer payload out.</summary>
    public Func<string, JsonElement, Task<object>> CommandHandler { get; set; } =
        (_, _) => Task.FromResult<object>(new StatusResponse { Status = "Accepted" });

    /// <summary>Every frame in both directions.</summary>
    public Action<OcppFrameDirection, string>? FrameLogger { get; set; }

    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsConnected => _peer?.IsOpen == true;

    public string? CloseReason => _peer?.CloseReason;

    /// <summary>Connects to the full URL including the identity, e.g. <c>ws://192.168.10.2:10001/ocpp/CP01</c>.</summary>
    public async Task ConnectAsync(Uri url, string? basicAuthPassword = null, CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("ocpp1.6");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        if (basicAuthPassword != null)
        {
            var identity = Uri.UnescapeDataString(url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{identity}:{basicAuthPassword}"));
            socket.Options.SetRequestHeader("Authorization", $"Basic {credentials}");
        }

        await socket.ConnectAsync(url, cancellationToken);
        Attach(socket);
    }

    /// <summary>Uses an already connected socket (e.g. from an ASP.NET Core TestServer).</summary>
    public void Attach(WebSocket socket)
    {
        _peer = new OcppWebSocketPeer(socket, 1024 * 1024,
            async (frame, _) => await CommandHandler(frame.Action ?? string.Empty, frame.Payload),
            (direction, text) => FrameLogger?.Invoke(direction, text),
            (_, _) => { });
        _runTask = Task.Run(() => _peer.RunAsync(_stop.Token));
    }

    public async Task<TResponse> Call<TResponse>(string action, object request) where TResponse : class, new()
    {
        if (_peer == null)
            throw new InvalidOperationException("Not connected");
        var payload = await _peer.Call(action, request, CallTimeout, CancellationToken.None);
        return OcppJson.Deserialize<TResponse>(payload) ?? new TResponse();
    }

    public Task<BootNotificationResponse> BootNotification(string vendor, string model, string? serialNumber = null,
        string? firmwareVersion = null) =>
        Call<BootNotificationResponse>(OcppActions.BootNotification, new BootNotificationRequest
        {
            ChargePointVendor = vendor,
            ChargePointModel = model,
            ChargePointSerialNumber = serialNumber,
            FirmwareVersion = firmwareVersion,
        });

    public Task<HeartbeatResponse> Heartbeat() => Call<HeartbeatResponse>(OcppActions.Heartbeat, new HeartbeatRequest());

    public Task<StatusNotificationResponse> StatusNotification(int connectorId, ChargePointStatus status,
        string errorCode = "NoError", string? info = null) =>
        Call<StatusNotificationResponse>(OcppActions.StatusNotification, new StatusNotificationRequest
        {
            ConnectorId = connectorId,
            Status = status.ToString(),
            ErrorCode = errorCode,
            Info = info,
            Timestamp = DateTime.UtcNow,
        });

    public Task<AuthorizeResponse> Authorize(string idTag) =>
        Call<AuthorizeResponse>(OcppActions.Authorize, new AuthorizeRequest { IdTag = idTag });

    public Task<StartTransactionResponse> StartTransaction(int connectorId, string idTag, long meterStartWh) =>
        Call<StartTransactionResponse>(OcppActions.StartTransaction, new StartTransactionRequest
        {
            ConnectorId = connectorId,
            IdTag = idTag,
            MeterStart = meterStartWh,
            Timestamp = DateTime.UtcNow,
        });

    public Task<StopTransactionResponse> StopTransaction(int transactionId, long meterStopWh, string reason = "Local",
        string? idTag = null) =>
        Call<StopTransactionResponse>(OcppActions.StopTransaction, new StopTransactionRequest
        {
            TransactionId = transactionId,
            MeterStop = meterStopWh,
            Timestamp = DateTime.UtcNow,
            Reason = reason,
            IdTag = idTag,
        });

    public Task<MeterValuesResponse> MeterValues(int connectorId, int? transactionId, double energyWh, double powerW) =>
        Call<MeterValuesResponse>(OcppActions.MeterValues, new MeterValuesRequest
        {
            ConnectorId = connectorId,
            TransactionId = transactionId,
            MeterValue = new List<MeterValue>
            {
                new()
                {
                    Timestamp = DateTime.UtcNow,
                    SampledValue = new List<SampledValue>
                    {
                        new() { Value = energyWh.ToString(System.Globalization.CultureInfo.InvariantCulture), Measurand = MeterReading.EnergyActiveImportRegister, Unit = "Wh" },
                        new() { Value = powerW.ToString(System.Globalization.CultureInfo.InvariantCulture), Measurand = MeterReading.PowerActiveImport, Unit = "W" },
                    },
                },
            },
        });

    public async Task CloseAsync()
    {
        if (_peer != null)
            await _peer.CloseAsync("Charge point closing");
        _stop.Cancel();
        if (_runTask != null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Closing anyway.
            }
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
