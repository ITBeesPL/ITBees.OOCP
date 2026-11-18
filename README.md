# ITBees.OOCP

OCPP 1.6J (JSON over WebSocket) **central system** for EV chargers, hosted inside any ASP.NET Core application.
Written for wallboxes on a parking LAN without internet (Octopark: the column/local server is the central system),
but nothing in it is parking-specific and it has no dependency on FAS.

- accepts charge point connections (`ws://host:port/ocpp/{chargePointId}`), one WebSocket per charger,
- answers the **Core profile** through your handler: BootNotification, Heartbeat, StatusNotification, Authorize,
  StartTransaction, StopTransaction, MeterValues, DataTransfer, plus Diagnostics/FirmwareStatusNotification and the
  security-extension notifications (answered empty),
- sends **commands**: RemoteStartTransaction, RemoteStopTransaction, Reset, UnlockConnector, ChangeAvailability,
  TriggerMessage, GetConfiguration, ChangeConfiguration, ClearCache,
- is **lenient** towards real firmware: numbers as strings, message type / unique id as string or number, missing
  payloads, timestamps with or without zone or fractions, energy in Wh or kWh, power in W or kW, per-phase values,
  the `ocpp1.6` sub-protocol missing or spelled differently,
- writes timestamps as `yyyy-MM-ddTHH:mm:ssZ` (several wallboxes cannot parse fractions) and gives the current time in
  every BootNotification/Heartbeat answer - with no internet that is the charger's only clock,
- detects dead connections: after `IdleProbeAfter` of silence it sends `GetConfiguration(HeartbeatInterval)` (mandatory
  in Core) and closes the connection when nothing answers,
- includes a **charge point client** (`OcppChargePointClient`) used by the tests and usable as a simulator.

## Quick start

```csharp
builder.Services.AddOcppCentralSystem<MyOcppHandler>(options =>
{
    options.HeartbeatIntervalSeconds = 60;
    options.CallTimeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();
app.UseOcppCentralSystem("/ocpp");   // before static files / fallbacks; enables WebSockets for this branch only
```

```csharp
public class MyOcppHandler : OcppCentralSystemHandlerBase
{
    public MyOcppHandler(OcppCentralSystemOptions options) : base(options) { }

    public override Task<StatusNotificationResponse> OnStatusNotification(OcppChargePointContext chargePoint,
        StatusNotificationRequest request)
    {
        // request.ParsedStatus: Available, Preparing, Charging, SuspendedEV(SE), Finishing, Reserved, Unavailable, Faulted
        return base.OnStatusNotification(chargePoint, request);
    }

    public override async Task<StartTransactionResponse> OnStartTransaction(OcppChargePointContext chargePoint,
        StartTransactionRequest request)
    {
        var answer = await base.OnStartTransaction(chargePoint, request); // Accepted + transaction id
        // store request.IdTag, request.MeterStart (Wh), answer.TransactionId ...
        return answer;
    }
}
```

The handler is a singleton: take scoped services through `IServiceScopeFactory`. Calls of one charger arrive one at a
time and in order; different chargers run in parallel. A handler may send a command to the charger while answering it
(incoming calls are processed apart from the read loop). Do not throw from `OnStartTransaction` - the charger gets a
CALLERROR and may refuse to charge.

Commands:

```csharp
var answer = await centralSystem.RemoteStopTransaction("CP01", transactionId);
if (!answer.IsAccepted) ...
// OcppCallException.Failure: NotConnected, Timeout, CallError (ErrorCode), ConnectionClosed, InvalidResponse
```

`MeterReading.From(request.MeterValue)` gives the newest energy register (Wh), power (W), current (A), voltage (V) and
SoC (%) out of MeterValues or StopTransaction.transactionData.

`OcppTimestamps.OrReceived(reported, receivedUtc)` replaces a charger time that is implausible (before 2020, or more
than 10 minutes in the future) with the time of receipt.

`OcppTransactionIdGenerator` hands out transaction ids without storage (seconds since 2024, strictly increasing).

## Charger configuration

Set the OCPP backend / central system URL in the charger's app or web panel to

```
ws://<IP of the host>:<port>/ocpp
```

Most chargers append their identity (charge point id / "charge box id") themselves; if a charger does not, put the
identity in the URL: `ws://<IP>:<port>/ocpp/<id>`. The identity is the last path segment. No TLS (`wss://`) in the
host - on a parking LAN it is not needed; terminate TLS in a reverse proxy when the charger connects over the internet.

## Simulator

`OcppChargePointClient` connects to any central system:

```csharp
await using var charger = new OcppChargePointClient();
await charger.ConnectAsync(new Uri("ws://192.168.10.2:10001/ocpp/SIM-01"));
await charger.BootNotification("ITBees", "Simulator");
await charger.StatusNotification(1, ChargePointStatus.Available);
var start = await charger.StartTransaction(1, "04A1B2C3", meterStartWh: 0);
await charger.MeterValues(1, start.TransactionId, energyWh: 1500, powerW: 11000);
await charger.StopTransaction(start.TransactionId, meterStopWh: 3200, reason: "EVDisconnected");
```

The console tool `tools/ITBees.OOCP.Simulator` wraps it with keyboard commands.

## Tests

`ITBees.OOCP.Tests` runs a real ASP.NET Core pipeline (TestServer) with the client on the other end of the socket:
a full charging session, commands and their answers, timeouts, CALLERRORs, reconnects, a refused connection and a
handler that calls the charger while answering it.
