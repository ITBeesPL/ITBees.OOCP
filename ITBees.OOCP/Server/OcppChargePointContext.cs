using System.Collections.Concurrent;
using System.Net;
using ITBees.OOCP.Messages;

namespace ITBees.OOCP.Server;

/// <summary>A connected charge point, as the host handler sees it.</summary>
public sealed class OcppChargePointContext
{
    internal OcppChargePointContext(string chargePointId, IPAddress? remoteIpAddress, string? subProtocol)
    {
        ChargePointId = chargePointId;
        RemoteIpAddress = remoteIpAddress;
        SubProtocol = subProtocol;
        ConnectedUtc = DateTime.UtcNow;
        LastMessageUtc = ConnectedUtc;
    }

    /// <summary>The identity from the URL (<c>.../ocpp/{chargePointId}</c>), as the charger sent it.</summary>
    public string ChargePointId { get; }

    public IPAddress? RemoteIpAddress { get; }
    public string? SubProtocol { get; }
    public DateTime ConnectedUtc { get; }

    /// <summary>Last frame received from the charger (any kind).</summary>
    public DateTime LastMessageUtc { get; internal set; }

    /// <summary>The last BootNotification of this connection; null until the charger sends one.</summary>
    public BootNotificationRequest? BootNotification { get; internal set; }

    /// <summary>Free storage for the host (e.g. the id of the device record the charger was matched to).</summary>
    public ConcurrentDictionary<string, object?> Items { get; } = new();

    public override string ToString() => $"{ChargePointId} ({RemoteIpAddress})";
}

/// <summary>What the host knows about a connection before the WebSocket handshake is answered.</summary>
public sealed class OcppConnectionRequest
{
    public string ChargePointId { get; init; } = string.Empty;
    public IPAddress? RemoteIpAddress { get; init; }

    /// <summary>The password of HTTP Basic authentication (OCPP security profile 1), if the charger sent one.</summary>
    public string? BasicAuthPassword { get; init; }

    public IReadOnlyList<string> RequestedSubProtocols { get; init; } = Array.Empty<string>();
}

public sealed class OcppConnectionDecision
{
    public bool Accept { get; init; }

    /// <summary>HTTP status of a refusal (401 for a wrong password, 404 for an unknown charge point).</summary>
    public int RejectStatusCode { get; init; } = 404;

    public string? Reason { get; init; }

    public static OcppConnectionDecision Accepted() => new() { Accept = true };

    public static OcppConnectionDecision Rejected(int statusCode, string reason) =>
        new() { Accept = false, RejectStatusCode = statusCode, Reason = reason };
}

public enum OcppFrameDirection
{
    Received,
    Sent,
}
