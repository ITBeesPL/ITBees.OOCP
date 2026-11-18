namespace ITBees.OOCP.Messages;

public enum AuthorizationStatus
{
    Accepted,
    Blocked,
    Expired,
    Invalid,
    ConcurrentTx,
}

public enum RegistrationStatus
{
    Accepted,
    Pending,
    Rejected,
}

public class IdTagInfo
{
    public AuthorizationStatus Status { get; set; } = AuthorizationStatus.Accepted;
    public DateTime? ExpiryDate { get; set; }
    public string? ParentIdTag { get; set; }

    public static IdTagInfo Accepted() => new() { Status = AuthorizationStatus.Accepted };
    public static IdTagInfo Invalid() => new() { Status = AuthorizationStatus.Invalid };
}

/// <summary>Values of one moment (OCPP MeterValue). Field values stay strings, read them through <see cref="MeterReading"/>.</summary>
public class MeterValue
{
    public DateTime Timestamp { get; set; }
    public List<SampledValue> SampledValue { get; set; } = new();
}

/// <summary>
/// One sampled value as the charger sent it. Everything except <see cref="Value"/> is optional; the defaults of OCPP
/// are: measurand "Energy.Active.Import.Register", unit "Wh", context "Sample.Periodic", location "Outlet".
/// </summary>
public class SampledValue
{
    public string Value { get; set; } = string.Empty;
    public string? Context { get; set; }
    public string? Format { get; set; }
    public string? Measurand { get; set; }
    public string? Phase { get; set; }
    public string? Location { get; set; }
    public string? Unit { get; set; }
}

/// <summary>Connector states of StatusNotification (OCPP 1.6, 7.7).</summary>
public enum ChargePointStatus
{
    Unknown = 0,
    Available,
    Preparing,
    Charging,
    SuspendedEVSE,
    SuspendedEV,
    Finishing,
    Reserved,
    Unavailable,
    Faulted,
}

public static class ChargePointStatuses
{
    /// <summary>Case-insensitive; anything unknown (or OCPP 1.5 "Occupied") is mapped as well as it can be.</summary>
    public static ChargePointStatus Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ChargePointStatus.Unknown;
        if (Enum.TryParse<ChargePointStatus>(text.Trim(), true, out var status))
            return status;
        return text.Trim().ToLowerInvariant() switch
        {
            "occupied" => ChargePointStatus.Charging,
            "suspended_evse" or "suspended-evse" => ChargePointStatus.SuspendedEVSE,
            "suspended_ev" or "suspended-ev" => ChargePointStatus.SuspendedEV,
            "fault" or "error" => ChargePointStatus.Faulted,
            _ => ChargePointStatus.Unknown,
        };
    }

    /// <summary>A vehicle is plugged in or the connector is held for one.</summary>
    public static bool IsOccupied(ChargePointStatus status) => status is ChargePointStatus.Preparing
        or ChargePointStatus.Charging or ChargePointStatus.SuspendedEV or ChargePointStatus.SuspendedEVSE
        or ChargePointStatus.Finishing or ChargePointStatus.Reserved;
}

/// <summary>Constants of ResetType, AvailabilityType and MessageTrigger.</summary>
public static class OcppValues
{
    public const string ResetSoft = "Soft";
    public const string ResetHard = "Hard";

    public const string AvailabilityOperative = "Operative";
    public const string AvailabilityInoperative = "Inoperative";

    public const string TriggerBootNotification = "BootNotification";
    public const string TriggerDiagnosticsStatusNotification = "DiagnosticsStatusNotification";
    public const string TriggerFirmwareStatusNotification = "FirmwareStatusNotification";
    public const string TriggerHeartbeat = "Heartbeat";
    public const string TriggerMeterValues = "MeterValues";
    public const string TriggerStatusNotification = "StatusNotification";
}
