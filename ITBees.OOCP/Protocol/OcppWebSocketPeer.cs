using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ITBees.OOCP.Server;

namespace ITBees.OOCP.Protocol;

/// <summary>
/// An answer the receiving side of a CALL wants to send as a CALLERROR instead of a CALLRESULT.
/// </summary>
public class OcppCallErrorException : Exception
{
    public OcppCallErrorException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// One end of an OCPP-J connection (used by the central system and by the charge point client): reads frames,
/// matches answers to the calls it sent, and answers incoming calls one at a time in arrival order. Incoming calls
/// are processed apart from the read loop, so a handler may itself send a call to the other side and wait for it.
/// </summary>
internal sealed class OcppWebSocketPeer
{
    private readonly WebSocket _socket;
    private readonly int _maxMessageBytes;
    private readonly Func<OcppFrame, CancellationToken, Task<object>> _onCall;
    private readonly Action<OcppFrameDirection, string> _onFrame;
    private readonly Action<string, Exception?> _onError;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OcppFrame>> _pendingCalls = new();
    private readonly Channel<OcppFrame> _incomingCalls = Channel.CreateUnbounded<OcppFrame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closed = new();

    public OcppWebSocketPeer(WebSocket socket, int maxMessageBytes,
        Func<OcppFrame, CancellationToken, Task<object>> onCall,
        Action<OcppFrameDirection, string> onFrame,
        Action<string, Exception?> onError)
    {
        _socket = socket;
        _maxMessageBytes = maxMessageBytes;
        _onCall = onCall;
        _onFrame = onFrame;
        _onError = onError;
        LastReceivedUtc = DateTime.UtcNow;
    }

    public DateTime LastReceivedUtc { get; private set; }

    /// <summary>Why the connection ended; null while it is open.</summary>
    public string? CloseReason { get; private set; }

    public bool IsOpen => !_closed.IsCancellationRequested && _socket.State == WebSocketState.Open;

    public CancellationToken Closed => _closed.Token;

    /// <summary>Runs until the connection closes (by either side, an error or <paramref name="cancellationToken"/>).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var processor = Task.Run(() => ProcessIncomingCalls(linked.Token), CancellationToken.None);
        try
        {
            await ReceiveLoop(linked.Token);
        }
        catch (OperationCanceledException)
        {
            CloseReason ??= cancellationToken.IsCancellationRequested ? "Server shutting down" : "Closed";
        }
        catch (WebSocketException e)
        {
            CloseReason ??= $"Connection lost: {e.Message}";
        }
        catch (Exception e)
        {
            CloseReason ??= $"Connection error: {e.Message}";
            _onError($"Receive loop failed: {e.Message}", e);
        }
        finally
        {
            CloseReason ??= "Closed";
            _incomingCalls.Writer.TryComplete();
            if (!_closed.IsCancellationRequested)
                _closed.Cancel();
            FailPendingCalls();
            try
            {
                await processor;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ReceiveLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseReason ??= string.IsNullOrEmpty(result.CloseStatusDescription)
                    ? $"Closed by the other side ({result.CloseStatus})"
                    : $"Closed by the other side: {result.CloseStatusDescription}";
                await CloseOutput(WebSocketCloseStatus.NormalClosure, "Bye");
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > _maxMessageBytes)
            {
                CloseReason = $"Message larger than {_maxMessageBytes} bytes";
                await CloseOutput(WebSocketCloseStatus.MessageTooBig, "Message too big");
                return;
            }

            if (!result.EndOfMessage)
                continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            LastReceivedUtc = DateTime.UtcNow;

            // Binary frames are not part of OCPP-J; some firmware still sends the JSON that way.
            if (string.IsNullOrWhiteSpace(text))
                continue;
            SafeOnFrame(OcppFrameDirection.Received, text);
            await HandleText(text);
        }
    }

    private async Task HandleText(string text)
    {
        if (!OcppFrame.TryParse(text, out var frame, out var error) || frame == null)
        {
            _onError($"Unreadable frame ({error}): {Shorten(text)}", null);
            var uniqueId = OcppFrame.TryReadUniqueId(text);
            if (uniqueId != null)
                await SendText(OcppFrame.CallError(uniqueId, OcppErrorCodes.FormationViolation, error));
            return;
        }

        switch (frame.Type)
        {
            case OcppMessageType.Call:
                await _incomingCalls.Writer.WriteAsync(frame);
                break;
            default:
                if (_pendingCalls.TryRemove(frame.UniqueId, out var pending))
                    pending.TrySetResult(frame);
                else
                    _onError($"Answer to an unknown or expired call {frame.UniqueId} ignored", null);
                break;
        }
    }

    private async Task ProcessIncomingCalls(CancellationToken cancellationToken)
    {
        await foreach (var call in _incomingCalls.Reader.ReadAllAsync(cancellationToken))
        {
            string answer;
            try
            {
                var payload = await _onCall(call, cancellationToken);
                answer = OcppFrame.CallResult(call.UniqueId, payload);
            }
            catch (OcppCallErrorException e)
            {
                answer = OcppFrame.CallError(call.UniqueId, e.ErrorCode, e.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (JsonException e)
            {
                answer = OcppFrame.CallError(call.UniqueId, OcppErrorCodes.FormationViolation, e.Message);
            }
            catch (Exception e)
            {
                _onError($"{call.Action} {call.UniqueId} failed in the handler: {e.Message}", e);
                answer = OcppFrame.CallError(call.UniqueId, OcppErrorCodes.InternalError, e.Message);
            }

            try
            {
                await SendText(answer);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                _onError($"Answer to {call.Action} {call.UniqueId} not sent: {e.Message}", e);
                return;
            }
        }
    }

    /// <summary>Sends a CALL and waits for its CALLRESULT payload. A CALLERROR or no answer throws <see cref="OcppCallException"/>.</summary>
    public async Task<JsonElement> Call(string action, object payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsOpen)
            throw new OcppCallException(OcppCallFailure.ConnectionClosed, $"{action}: the connection is closed");

        var uniqueId = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<OcppFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalls[uniqueId] = pending;
        try
        {
            await SendText(OcppFrame.Call(uniqueId, action, payload));

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            timeoutSource.CancelAfter(timeout);
            OcppFrame answer;
            try
            {
                answer = await pending.Task.WaitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (_closed.IsCancellationRequested)
                    throw new OcppCallException(OcppCallFailure.ConnectionClosed, $"{action}: the connection closed before the answer");
                throw new OcppCallException(OcppCallFailure.Timeout, $"{action}: no answer within {timeout.TotalSeconds:0} s");
            }

            if (answer.Type == OcppMessageType.CallError)
                throw new OcppCallException(OcppCallFailure.CallError,
                    $"{action}: {answer.ErrorCode} {answer.ErrorDescription}".Trim(), answer.ErrorCode);
            return answer.Payload;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            throw new OcppCallException(OcppCallFailure.ConnectionClosed, $"{action}: {e.Message}", inner: e);
        }
        catch (OperationCanceledException e) when (_closed.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The connection closed while the call was being sent.
            throw new OcppCallException(OcppCallFailure.ConnectionClosed, $"{action}: the connection is closed", inner: e);
        }
        finally
        {
            _pendingCalls.TryRemove(uniqueId, out _);
        }
    }

    public async Task SendText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(_closed.Token);
        try
        {
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _closed.Token);
        }
        finally
        {
            _sendLock.Release();
        }

        SafeOnFrame(OcppFrameDirection.Sent, text);
    }

    public async Task CloseAsync(string reason)
    {
        CloseReason ??= reason;
        await CloseOutput(WebSocketCloseStatus.NormalClosure, reason);
        if (!_closed.IsCancellationRequested)
            _closed.Cancel();
    }

    private async Task CloseOutput(WebSocketCloseStatus status, string description)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // The close description is limited to 123 bytes by RFC 6455.
                var shortDescription = description.Length > 100 ? description[..100] : description;
                await _socket.CloseOutputAsync(status, shortDescription, timeout.Token);
            }
        }
        catch (Exception)
        {
            _socket.Abort();
        }
    }

    private void FailPendingCalls()
    {
        foreach (var uniqueId in _pendingCalls.Keys.ToList())
        {
            if (_pendingCalls.TryRemove(uniqueId, out var pending))
                pending.TrySetCanceled();
        }
    }

    private void SafeOnFrame(OcppFrameDirection direction, string text)
    {
        try
        {
            _onFrame(direction, text);
        }
        catch (Exception)
        {
            // A logging callback must never break the connection.
        }
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "..." : text;
}
