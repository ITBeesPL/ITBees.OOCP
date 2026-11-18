namespace ITBees.OOCP.Server;

public sealed class OcppCentralSystemOptions
{
    /// <summary>Heartbeat interval given to chargers in BootNotification.conf, seconds.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>How long a command sent to a charger waits for its answer.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>WebSocket keep-alive frames, so NAT/firewalls do not drop a quiet connection.</summary>
    public TimeSpan WebSocketKeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After this long without any message the central system asks the charger for one configuration key
    /// (GetConfiguration is mandatory in the Core profile); no answer = the connection is dead and gets closed.
    /// Zero switches the probe off.
    /// </summary>
    public TimeSpan IdleProbeAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Largest accepted message (a full GetConfiguration or a long transactionData can be big).</summary>
    public int MaxMessageBytes { get; set; } = 1024 * 1024;

    /// <summary>Sub-protocols answered in the handshake, in order of preference.</summary>
    public IList<string> SupportedSubProtocols { get; set; } = new List<string> { "ocpp1.6" };

    /// <summary>
    /// When false (default) a charger that asks for no sub-protocol at all is accepted too - several cheap wallboxes
    /// do not send <c>Sec-WebSocket-Protocol</c>. A charger asking only for other versions (e.g. ocpp2.0.1) is always refused.
    /// </summary>
    public bool RequireSubProtocol { get; set; }
}
