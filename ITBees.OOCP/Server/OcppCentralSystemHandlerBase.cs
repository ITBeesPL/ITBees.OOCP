using ITBees.OOCP.Messages;

namespace ITBees.OOCP.Server;

/// <summary>
/// Defaults that let any charger charge: every connection, id tag and transaction is accepted, the clock is given
/// in every BootNotification / Heartbeat answer and transaction ids come from <see cref="OcppTransactionIdGenerator"/>.
/// Override what the host needs.
/// </summary>
public abstract class OcppCentralSystemHandlerBase : IOcppCentralSystemHandler
{
    protected OcppCentralSystemHandlerBase(OcppCentralSystemOptions options)
    {
        Options = options;
    }

    protected OcppCentralSystemOptions Options { get; }

    protected OcppTransactionIdGenerator TransactionIds { get; } = new();

    public virtual Task<OcppConnectionDecision> OnConnecting(OcppConnectionRequest request) =>
        Task.FromResult(OcppConnectionDecision.Accepted());

    public virtual Task OnConnected(OcppChargePointContext chargePoint) => Task.CompletedTask;

    public virtual Task OnDisconnected(OcppChargePointContext chargePoint, string reason) => Task.CompletedTask;

    public virtual Task<BootNotificationResponse> OnBootNotification(OcppChargePointContext chargePoint,
        BootNotificationRequest request) =>
        Task.FromResult(new BootNotificationResponse
        {
            Status = RegistrationStatus.Accepted,
            CurrentTime = DateTime.UtcNow,
            Interval = Options.HeartbeatIntervalSeconds,
        });

    public virtual Task<HeartbeatResponse> OnHeartbeat(OcppChargePointContext chargePoint, HeartbeatRequest request) =>
        Task.FromResult(new HeartbeatResponse { CurrentTime = DateTime.UtcNow });

    public virtual Task<StatusNotificationResponse> OnStatusNotification(OcppChargePointContext chargePoint,
        StatusNotificationRequest request) =>
        Task.FromResult(new StatusNotificationResponse());

    public virtual Task<AuthorizeResponse> OnAuthorize(OcppChargePointContext chargePoint, AuthorizeRequest request) =>
        Task.FromResult(new AuthorizeResponse { IdTagInfo = IdTagInfo.Accepted() });

    public virtual Task<StartTransactionResponse> OnStartTransaction(OcppChargePointContext chargePoint,
        StartTransactionRequest request) =>
        Task.FromResult(new StartTransactionResponse
        {
            IdTagInfo = IdTagInfo.Accepted(),
            TransactionId = TransactionIds.Next(),
        });

    public virtual Task<StopTransactionResponse> OnStopTransaction(OcppChargePointContext chargePoint,
        StopTransactionRequest request) =>
        Task.FromResult(new StopTransactionResponse
        {
            IdTagInfo = string.IsNullOrEmpty(request.IdTag) ? null : IdTagInfo.Accepted(),
        });

    public virtual Task<MeterValuesResponse> OnMeterValues(OcppChargePointContext chargePoint, MeterValuesRequest request) =>
        Task.FromResult(new MeterValuesResponse());

    public virtual Task<DataTransferResponse> OnDataTransfer(OcppChargePointContext chargePoint, DataTransferRequest request) =>
        Task.FromResult(new DataTransferResponse { Status = "UnknownVendorId" });

    public virtual Task<DiagnosticsStatusNotificationResponse> OnDiagnosticsStatusNotification(
        OcppChargePointContext chargePoint, DiagnosticsStatusNotificationRequest request) =>
        Task.FromResult(new DiagnosticsStatusNotificationResponse());

    public virtual Task<FirmwareStatusNotificationResponse> OnFirmwareStatusNotification(
        OcppChargePointContext chargePoint, FirmwareStatusNotificationRequest request) =>
        Task.FromResult(new FirmwareStatusNotificationResponse());

    public virtual void OnFrame(OcppChargePointContext chargePoint, OcppFrameDirection direction, string text)
    {
    }

    public virtual void OnError(OcppChargePointContext? chargePoint, string message, Exception? exception)
    {
    }
}
