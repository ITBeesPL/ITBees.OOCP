using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ITBees.OOCP.Protocol;

/// <summary>
/// JSON settings of OCPP-J payloads: camelCase names, nulls left out, enums as their names, numbers accepted as
/// strings (some firmware sends "meterStart":"1234") and timestamps written without fractions of a second.
/// </summary>
public static class OcppJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new OcppDateTimeConverter());
        options.Converters.Add(new OcppNullableDateTimeConverter());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(Options);
}

/// <summary>
/// Writes "2026-11-18T10:15:00Z" (UTC, whole seconds) - several wallbox firmwares cannot parse fractions or offsets.
/// Reads anything <see cref="OcppTimestamps.TryParse"/> accepts; an unreadable value becomes <see cref="DateTime.MinValue"/>
/// instead of failing the whole message, so the host falls back to the time of receipt.
/// </summary>
public sealed class OcppDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && OcppTimestamps.TryParse(reader.GetString(), out var value))
            return value;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var unixSeconds))
            return OcppTimestamps.FromUnix(unixSeconds);
        return DateTime.MinValue;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(OcppTimestamps.Format(value));
}

public sealed class OcppNullableDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType == JsonTokenType.String && OcppTimestamps.TryParse(reader.GetString(), out var value))
            return value;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var unixSeconds))
            return OcppTimestamps.FromUnix(unixSeconds);
        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(OcppTimestamps.Format(value.Value));
        else
            writer.WriteNullValue();
    }
}

/// <summary>Timestamp handling for chargers whose clock may be wrong (no internet = no NTP).</summary>
public static class OcppTimestamps
{
    /// <summary>Anything before this is a clock that was never set (1970, 2000, ...).</summary>
    public static readonly DateTime EarliestPlausibleUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string Format(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>ISO 8601 with or without "Z"/offset; a value without a zone is taken as UTC (as OCPP requires).</summary>
    public static bool TryParse(string? text, out DateTime utc)
    {
        utc = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return false;
        utc = parsed.UtcDateTime;
        return true;
    }

    public static DateTime FromUnix(long seconds)
    {
        try
        {
            // Milliseconds sent by mistake: 13 digits.
            return seconds > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(seconds).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// The charger's time when it is believable (not before 2020 and not more than <paramref name="maxSkew"/> in the
    /// future), otherwise <paramref name="receivedUtc"/>. A charger without a network time source often boots in 1970.
    /// </summary>
    public static DateTime OrReceived(DateTime? reportedUtc, DateTime receivedUtc, TimeSpan? maxSkew = null)
    {
        if (reportedUtc is not { } reported || reported < EarliestPlausibleUtc)
            return receivedUtc;
        var reportedAsUtc = reported.Kind == DateTimeKind.Utc ? reported : DateTime.SpecifyKind(reported, DateTimeKind.Utc);
        return reportedAsUtc > receivedUtc + (maxSkew ?? TimeSpan.FromMinutes(10)) ? receivedUtc : reportedAsUtc;
    }
}
