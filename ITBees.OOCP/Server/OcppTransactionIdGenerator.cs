namespace ITBees.OOCP.Server;

/// <summary>
/// Transaction ids without storage: seconds since 2024-01-01 UTC, kept strictly increasing within the process.
/// A restart never repeats an id that was given out more than a second earlier, the values fit in int32 until
/// 2092, and they stay unique across several central systems far better than a counter starting at 1.
/// </summary>
public sealed class OcppTransactionIdGenerator
{
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly object _lock = new();
    private readonly Func<DateTime> _utcNow;
    private int _last;

    public OcppTransactionIdGenerator() : this(() => DateTime.UtcNow)
    {
    }

    internal OcppTransactionIdGenerator(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    public int Next()
    {
        var fromClock = (int)Math.Clamp((_utcNow() - Epoch).TotalSeconds, 1, int.MaxValue);
        lock (_lock)
        {
            _last = Math.Max(fromClock, _last + 1);
            return _last;
        }
    }
}
