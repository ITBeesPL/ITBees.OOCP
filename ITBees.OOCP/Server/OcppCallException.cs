namespace ITBees.OOCP.Server;

/// <summary>A command sent to a charger failed: not connected, no answer in time, or a CALLERROR answer.</summary>
public class OcppCallException : Exception
{
    public OcppCallException(OcppCallFailure failure, string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        ErrorCode = errorCode;
    }

    public OcppCallFailure Failure { get; }

    /// <summary>The OCPP error code of a CALLERROR answer.</summary>
    public string? ErrorCode { get; }
}

public enum OcppCallFailure
{
    NotConnected,
    Timeout,
    CallError,
    ConnectionClosed,
    InvalidResponse,
}
