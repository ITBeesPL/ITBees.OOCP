namespace ITBees.OOCP.Messages;

// Messages a charge point sends to the central system (Core profile + the two status notifications of the
// Firmware Management profile, which chargers often send unasked) and the central system's answers.

public class BootNotificationRequest
{
    public string? ChargePointVendor { get; set; }
    public string? ChargePointModel { get; set; }
    public string? ChargePointSerialNumber { get; set; }
    public string? ChargeBoxSerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? Iccid { get; set; }
    public string? Imsi { get; set; }
    public string? MeterType { get; set; }
    public string? MeterSerialNumber { get; set; }
}

public class BootNotificationResponse
{
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Accepted;

    /// <summary>The charger sets its clock from it - with no internet this is its only time source.</summary>
    public DateTime CurrentTime { get; set; } = DateTime.UtcNow;

    /// <summary>Heartbeat interval in seconds (or the retry interval when not accepted).</summary>
    public int Interval { get; set; } = 60;
}

public class HeartbeatRequest
{
}

public class HeartbeatResponse
{
    public DateTime CurrentTime { get; set; } = DateTime.UtcNow;
}

public class StatusNotificationRequest
{
    /// <summary>0 = the whole charge point, 1.. = connectors.</summary>
    public int ConnectorId { get; set; }

    public string? ErrorCode { get; set; }
    public string? Info { get; set; }
    public string? Status { get; set; }
    public DateTime? Timestamp { get; set; }
    public string? VendorId { get; set; }
    public string? VendorErrorCode { get; set; }

    public ChargePointStatus ParsedStatus => ChargePointStatuses.Parse(Status);

    /// <summary>"NoError" (or nothing) means no fault.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorCode) &&
                            !string.Equals(ErrorCode, "NoError", StringComparison.OrdinalIgnoreCase);
}

public class StatusNotificationResponse
{
}

public class AuthorizeRequest
{
    public string IdTag { get; set; } = string.Empty;
}

public class AuthorizeResponse
{
    public IdTagInfo IdTagInfo { get; set; } = IdTagInfo.Accepted();
}

public class StartTransactionRequest
{
    public int ConnectorId { get; set; }
    public string IdTag { get; set; } = string.Empty;

    /// <summary>Energy register at the start, in Wh (OCPP integer).</summary>
    public long MeterStart { get; set; }

    public int? ReservationId { get; set; }
    public DateTime Timestamp { get; set; }
}

public class StartTransactionResponse
{
    public IdTagInfo IdTagInfo { get; set; } = IdTagInfo.Accepted();
    public int TransactionId { get; set; }
}

public class StopTransactionRequest
{
    public string? IdTag { get; set; }

    /// <summary>Energy register at the end, in Wh.</summary>
    public long MeterStop { get; set; }

    public DateTime Timestamp { get; set; }
    public int TransactionId { get; set; }

    /// <summary>EmergencyStop, EVDisconnected, HardReset, Local, Other, PowerLoss, Reboot, Remote, SoftReset, UnlockCommand, DeAuthorized.</summary>
    public string? Reason { get; set; }

    public List<MeterValue>? TransactionData { get; set; }
}

public class StopTransactionResponse
{
    public IdTagInfo? IdTagInfo { get; set; }
}

public class MeterValuesRequest
{
    public int ConnectorId { get; set; }
    public int? TransactionId { get; set; }
    public List<MeterValue> MeterValue { get; set; } = new();
}

public class MeterValuesResponse
{
}

public class DataTransferRequest
{
    public string VendorId { get; set; } = string.Empty;
    public string? MessageId { get; set; }

    /// <summary>Free text (often JSON inside a string).</summary>
    public string? Data { get; set; }
}

public class DataTransferResponse
{
    /// <summary>Accepted, Rejected, UnknownMessageId or UnknownVendorId.</summary>
    public string Status { get; set; } = "Accepted";

    public string? Data { get; set; }
}

public class DiagnosticsStatusNotificationRequest
{
    public string? Status { get; set; }
}

public class DiagnosticsStatusNotificationResponse
{
}

public class FirmwareStatusNotificationRequest
{
    public string? Status { get; set; }
}

public class FirmwareStatusNotificationResponse
{
}
