using ITBees.OOCP.Messages;
using ITBees.OOCP.Protocol;

namespace ITBees.OOCP.Server;

/// <summary>Typed commands of the Core and Remote Trigger profiles.</summary>
public static class OcppCentralSystemCommands
{
    public static Task<StatusResponse> RemoteStartTransaction(this IOcppCentralSystem centralSystem, string chargePointId,
        string idTag, int? connectorId = null, CancellationToken cancellationToken = default) =>
        centralSystem.Call<StatusResponse>(chargePointId, OcppActions.RemoteStartTransaction,
            new RemoteStartTransactionRequest { IdTag = idTag, ConnectorId = connectorId }, cancellationToken: cancellationToken);

    public static Task<StatusResponse> RemoteStopTransaction(this IOcppCentralSystem centralSystem, string chargePointId,
        int transactionId, CancellationToken cancellationToken = default) =>
        centralSystem.Call<StatusResponse>(chargePointId, OcppActions.RemoteStopTransaction,
            new RemoteStopTransactionRequest { TransactionId = transactionId }, cancellationToken: cancellationToken);

    public static Task<StatusResponse> Reset(this IOcppCentralSystem centralSystem, string chargePointId,
        bool hard, CancellationToken cancellationToken = default) =>
        centralSystem.Call<StatusResponse>(chargePointId, OcppActions.Reset,
            new ResetRequest { Type = hard ? OcppValues.ResetHard : OcppValues.ResetSoft }, cancellationToken: cancellationToken);

    public static Task<StatusResponse> UnlockConnector(this IOcppCentralSystem centralSystem, string chargePointId,
        int connectorId, CancellationToken cancellationToken = default) =>
        centralSystem.Call<StatusResponse>(chargePointId, OcppActions.UnlockConnector,
            new UnlockConnectorRequest { ConnectorId = connectorId }, cancellationToken: cancellationToken);

    public static Task<StatusResponse> ChangeAvailability(this IOcppCentralSystem centralSystem, string chargePointId,
        int connectorId, bool operative, CancellationToken cancellationToken = default) =>
        centralSystem.Call<StatusResponse>(chargePointId, OcppActions.ChangeAvailability,
            new ChangeAvailabilityRequest
            {
                ConnectorId = connectorId,
                Type = operative ? OcppValues.AvailabilityOperative : OcppValues.AvailabilityInoperative,
            }, cancellationToken: cancellationToken);

    public static Task<StatusResponse> TriggerMessage(this IOcppCentralSystem centralSystem, string chargePointId,
        string requestedMessage, int? connectorId = null, CancellationToken cancellationToken = default) =>
        centralSystem.Call<StatusResponse>(chargePointId, OcppActions.TriggerMessage,
            new TriggerMessageRequest { RequestedMessage = requestedMessage, ConnectorId = connectorId },
            cancellationToken: cancellationToken);

    public static Task<GetConfigurationResponse> GetConfiguration(this IOcppCentralSystem centralSystem,
        string chargePointId, IEnumerable<string>? keys = null, CancellationToken cancellationToken = default) =>
        centralSystem.Call<GetConfigurationResponse>(chargePointId, OcppActions.GetConfiguration,
            new GetConfigurationRequest { Key = keys?.ToList() }, cancellationToken: cancellationToken);

    public static Task<StatusResponse> ChangeConfiguration(this IOcppCentralSystem centralSystem, string chargePointId,
        string key, string value, CancellationToken cancellationToken = default) =>
        centralSystem.Call<StatusResponse>(chargePointId, OcppActions.ChangeConfiguration,
            new ChangeConfigurationRequest { Key = key, Value = value }, cancellationToken: cancellationToken);

    public static Task<StatusResponse> ClearCache(this IOcppCentralSystem centralSystem, string chargePointId,
        CancellationToken cancellationToken = default) =>
        centralSystem.Call<StatusResponse>(chargePointId, OcppActions.ClearCache, new ClearCacheRequest(),
            cancellationToken: cancellationToken);
}
