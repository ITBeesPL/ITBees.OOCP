using ITBees.OOCP.Messages;

namespace ITBees.OOCP.Server;

/// <summary>
/// The host application's side of the central system: decides who may connect and answers what chargers send.
/// Calls of one charger come one at a time and in order (OCPP requires the charger to wait for each answer);
/// different chargers are handled in parallel. A handler that throws answers the charger with a CALLERROR
/// (InternalError) - for StartTransaction that may stop the charging, so the host should not throw there.
/// Derive from <see cref="OcppCentralSystemHandlerBase"/> to get working defaults for everything.
/// </summary>
public interface IOcppCentralSystemHandler
{
    Task<OcppConnectionDecision> OnConnecting(OcppConnectionRequest request);
    Task OnConnected(OcppChargePointContext chargePoint);
    Task OnDisconnected(OcppChargePointContext chargePoint, string reason);

    Task<BootNotificationResponse> OnBootNotification(OcppChargePointContext chargePoint, BootNotificationRequest request);
    Task<HeartbeatResponse> OnHeartbeat(OcppChargePointContext chargePoint, HeartbeatRequest request);
    Task<StatusNotificationResponse> OnStatusNotification(OcppChargePointContext chargePoint, StatusNotificationRequest request);
    Task<AuthorizeResponse> OnAuthorize(OcppChargePointContext chargePoint, AuthorizeRequest request);
    Task<StartTransactionResponse> OnStartTransaction(OcppChargePointContext chargePoint, StartTransactionRequest request);
    Task<StopTransactionResponse> OnStopTransaction(OcppChargePointContext chargePoint, StopTransactionRequest request);
    Task<MeterValuesResponse> OnMeterValues(OcppChargePointContext chargePoint, MeterValuesRequest request);
    Task<DataTransferResponse> OnDataTransfer(OcppChargePointContext chargePoint, DataTransferRequest request);

    Task<DiagnosticsStatusNotificationResponse> OnDiagnosticsStatusNotification(OcppChargePointContext chargePoint,
        DiagnosticsStatusNotificationRequest request);

    Task<FirmwareStatusNotificationResponse> OnFirmwareStatusNotification(OcppChargePointContext chargePoint,
        FirmwareStatusNotificationRequest request);

    /// <summary>Every frame in both directions (raw text) - for a protocol log. Must be cheap and must not throw.</summary>
    void OnFrame(OcppChargePointContext chargePoint, OcppFrameDirection direction, string text);

    /// <summary>Problems the host may want to log (bad frames, handler exceptions, dropped connections).</summary>
    void OnError(OcppChargePointContext? chargePoint, string message, Exception? exception);
}
