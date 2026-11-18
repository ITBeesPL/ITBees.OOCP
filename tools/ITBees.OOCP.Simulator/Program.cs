using System.Globalization;
using System.Text.Json;
using ITBees.OOCP.Client;
using ITBees.OOCP.Messages;
using ITBees.OOCP.Protocol;

// Single-connector AC wallbox simulator (like a Lenergizee Business 22 kW) for testing a central system without hardware.
// Usage: ITBees.OOCP.Simulator [ws://host:port/ocpp/IDENTITY] [idTag]
var url = new Uri(args.Length > 0 ? args[0] : "ws://localhost:10001/ocpp/SIM-01");
var idTag = args.Length > 1 ? args[1] : "SIM-CARD-01";
var simulator = new Simulator(url, idTag);
await simulator.Run();

internal sealed class Simulator
{
    private const int ConnectorId = 1;
    private const double PowerW = 11_000;

    private readonly Uri _url;
    private readonly string _idTag;
    private readonly object _lock = new();
    private OcppChargePointClient _client = new();
    private int? _transactionId;
    private double _energyWh = 125_000;
    private int _heartbeatSeconds = 60;

    public Simulator(Uri url, string idTag)
    {
        _url = url;
        _idTag = idTag;
    }

    public async Task Run()
    {
        await Connect();
        Console.WriteLine("Keys: [s] start  [t] stop  [f] fault  [a] available  [u] unavailable  [m] meter now  [r] reconnect  [q] quit");
        using var timers = new CancellationTokenSource();
        _ = Task.Run(() => HeartbeatLoop(timers.Token));
        _ = Task.Run(() => MeterLoop(timers.Token));

        while (true)
        {
            var key = Console.ReadKey(intercept: true).KeyChar;
            try
            {
                switch (char.ToLowerInvariant(key))
                {
                    case 's': await StartCharging(_idTag); break;
                    case 't': await StopCharging("Local"); break;
                    case 'f': await _client.StatusNotification(ConnectorId, ChargePointStatus.Faulted, "GroundFailure", "Simulated fault"); break;
                    case 'a': await _client.StatusNotification(ConnectorId, ChargePointStatus.Available); break;
                    case 'u': await _client.StatusNotification(ConnectorId, ChargePointStatus.Unavailable); break;
                    case 'm': await SendMeter(); break;
                    case 'r': await Reconnect(); break;
                    case 'q':
                        timers.Cancel();
                        await _client.CloseAsync();
                        return;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"! {e.Message}");
            }
        }
    }

    private async Task Connect()
    {
        _client = new OcppChargePointClient
        {
            FrameLogger = (direction, text) => Console.WriteLine($"{(direction == ITBees.OOCP.Server.OcppFrameDirection.Sent ? ">>" : "<<")} {text}"),
            CommandHandler = HandleCommand,
        };
        await _client.ConnectAsync(_url);
        var boot = await _client.BootNotification("ITBees", "Simulator 22kW", "SIM-SN-0001", "sim-1.0");
        _heartbeatSeconds = Math.Max(10, boot.Interval);
        await _client.StatusNotification(0, ChargePointStatus.Available);
        await _client.StatusNotification(ConnectorId, _transactionId == null ? ChargePointStatus.Available : ChargePointStatus.Charging);
    }

    private async Task Reconnect()
    {
        await _client.CloseAsync();
        await Task.Delay(1000);
        await Connect();
    }

    private async Task StartCharging(string tag)
    {
        if (_transactionId != null)
        {
            Console.WriteLine("! Already charging");
            return;
        }

        await _client.StatusNotification(ConnectorId, ChargePointStatus.Preparing);
        var authorize = await _client.Authorize(tag);
        if (authorize.IdTagInfo.Status != AuthorizationStatus.Accepted)
        {
            Console.WriteLine($"! Id tag {tag} refused: {authorize.IdTagInfo.Status}");
            await _client.StatusNotification(ConnectorId, ChargePointStatus.Available);
            return;
        }

        var start = await _client.StartTransaction(ConnectorId, tag, (long)_energyWh);
        lock (_lock) _transactionId = start.TransactionId;
        await _client.StatusNotification(ConnectorId, ChargePointStatus.Charging);
    }

    private async Task StopCharging(string reason)
    {
        int transactionId;
        lock (_lock)
        {
            if (_transactionId == null)
            {
                Console.WriteLine("! Not charging");
                return;
            }

            transactionId = _transactionId.Value;
            _transactionId = null;
        }

        await _client.StatusNotification(ConnectorId, ChargePointStatus.Finishing);
        await _client.StopTransaction(transactionId, (long)_energyWh, reason, _idTag);
        await _client.StatusNotification(ConnectorId, ChargePointStatus.Available);
    }

    private async Task SendMeter()
    {
        int? transactionId;
        lock (_lock) transactionId = _transactionId;
        await _client.MeterValues(ConnectorId, transactionId, _energyWh, transactionId == null ? 0 : PowerW);
    }

    private async Task HeartbeatLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_heartbeatSeconds), cancellationToken);
            try { await _client.Heartbeat(); } catch (Exception e) { Console.WriteLine($"! Heartbeat: {e.Message}"); }
        }
    }

    private async Task MeterLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            bool charging;
            lock (_lock) charging = _transactionId != null;
            if (!charging)
                continue;
            _energyWh += PowerW * 10 / 3600;
            try { await SendMeter(); } catch (Exception e) { Console.WriteLine($"! MeterValues: {e.Message}"); }
        }
    }

    private async Task<object> HandleCommand(string action, JsonElement payload)
    {
        await Task.Yield();
        Console.WriteLine($"## Command {action}");
        switch (action)
        {
            case OcppActions.RemoteStartTransaction:
                var tag = payload.TryGetProperty("idTag", out var idTagElement) ? idTagElement.GetString() ?? _idTag : _idTag;
                _ = Task.Run(async () => { await Task.Delay(500); await StartCharging(tag); });
                return new StatusResponse { Status = _transactionId == null ? "Accepted" : "Rejected" };
            case OcppActions.RemoteStopTransaction:
                _ = Task.Run(async () => { await Task.Delay(500); await StopCharging("Remote"); });
                return new StatusResponse { Status = _transactionId == null ? "Rejected" : "Accepted" };
            case OcppActions.Reset:
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    if (_transactionId != null) await StopCharging("SoftReset");
                    await Reconnect();
                });
                return new StatusResponse { Status = "Accepted" };
            case OcppActions.UnlockConnector:
                return new StatusResponse { Status = "Unlocked" };
            case OcppActions.ChangeAvailability:
                var operative = payload.TryGetProperty("type", out var type) && type.GetString() == "Operative";
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    await _client.StatusNotification(ConnectorId, operative ? ChargePointStatus.Available : ChargePointStatus.Unavailable);
                });
                return new StatusResponse { Status = "Accepted" };
            case OcppActions.TriggerMessage:
                var requested = payload.TryGetProperty("requestedMessage", out var message) ? message.GetString() : null;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    if (requested == "StatusNotification")
                        await _client.StatusNotification(ConnectorId, _transactionId == null ? ChargePointStatus.Available : ChargePointStatus.Charging);
                    else if (requested == "MeterValues")
                        await SendMeter();
                    else if (requested == "Heartbeat")
                        await _client.Heartbeat();
                });
                return new StatusResponse { Status = "Accepted" };
            case OcppActions.GetConfiguration:
                return new GetConfigurationResponse
                {
                    ConfigurationKey = new List<ConfigurationKeyValue>
                    {
                        new() { Key = "HeartbeatInterval", Value = _heartbeatSeconds.ToString(CultureInfo.InvariantCulture) },
                        new() { Key = "NumberOfConnectors", Value = "1", Readonly = true },
                        new() { Key = "MeterValueSampleInterval", Value = "10" },
                    },
                };
            default:
                return new StatusResponse { Status = "Accepted" };
        }
    }
}
