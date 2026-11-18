namespace ITBees.OOCP.Messages;

// Messages the central system sends to a charge point and the charge point's answers. Answer statuses stay strings:
// they come from firmware, and an unexpected spelling must not turn a delivered command into a "failure".

public class RemoteStartTransactionRequest
{
    /// <summary>Null lets the charger pick the connector (a single-socket wallbox ignores it anyway).</summary>
    public int? ConnectorId { get; set; }

    public string IdTag { get; set; } = string.Empty;
}

public class RemoteStopTransactionRequest
{
    public int TransactionId { get; set; }
}

public class ResetRequest
{
    /// <summary><see cref="OcppValues.ResetSoft"/> or <see cref="OcppValues.ResetHard"/>.</summary>
    public string Type { get; set; } = OcppValues.ResetSoft;
}

public class UnlockConnectorRequest
{
    public int ConnectorId { get; set; } = 1;
}

public class ChangeAvailabilityRequest
{
    /// <summary>0 = the whole charge point.</summary>
    public int ConnectorId { get; set; }

    /// <summary><see cref="OcppValues.AvailabilityOperative"/> or <see cref="OcppValues.AvailabilityInoperative"/>.</summary>
    public string Type { get; set; } = OcppValues.AvailabilityOperative;
}

public class TriggerMessageRequest
{
    /// <summary>One of the <c>OcppValues.Trigger*</c> constants.</summary>
    public string RequestedMessage { get; set; } = OcppValues.TriggerStatusNotification;

    public int? ConnectorId { get; set; }
}

public class GetConfigurationRequest
{
    /// <summary>Null or empty = all keys.</summary>
    public List<string>? Key { get; set; }
}

public class GetConfigurationResponse
{
    public List<ConfigurationKeyValue>? ConfigurationKey { get; set; }
    public List<string>? UnknownKey { get; set; }
}

public class ConfigurationKeyValue
{
    public string Key { get; set; } = string.Empty;
    public bool Readonly { get; set; }
    public string? Value { get; set; }
}

public class ChangeConfigurationRequest
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class ClearCacheRequest
{
}

/// <summary>The answer of every command whose response is just a status.</summary>
public class StatusResponse
{
    /// <summary>
    /// Accepted / Rejected (most commands), Unlocked / UnlockFailed / NotSupported (UnlockConnector), Scheduled
    /// (ChangeAvailability), RebootRequired (ChangeConfiguration), NotImplemented (TriggerMessage).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    public bool IsAccepted => Status.Equals("Accepted", StringComparison.OrdinalIgnoreCase)
                              || Status.Equals("Unlocked", StringComparison.OrdinalIgnoreCase)
                              || Status.Equals("Scheduled", StringComparison.OrdinalIgnoreCase)
                              || Status.Equals("RebootRequired", StringComparison.OrdinalIgnoreCase);
}
