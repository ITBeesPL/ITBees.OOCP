using System.Text;
using System.Text.Json;

namespace ITBees.OOCP.Protocol;

public enum OcppMessageType
{
    Call = 2,
    CallResult = 3,
    CallError = 4,
}

/// <summary>
/// One OCPP-J message: <c>[2, id, action, payload]</c>, <c>[3, id, payload]</c> or
/// <c>[4, id, errorCode, errorDescription, errorDetails]</c>.
/// </summary>
public sealed class OcppFrame
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public OcppMessageType Type { get; private init; }
    public string UniqueId { get; private init; } = string.Empty;

    /// <summary>Set for a CALL.</summary>
    public string? Action { get; private init; }

    /// <summary>CALL / CALLRESULT payload, or the error details of a CALLERROR.</summary>
    public JsonElement Payload { get; private init; } = EmptyObject;

    public string? ErrorCode { get; private init; }
    public string? ErrorDescription { get; private init; }

    /// <summary>
    /// Reads a message. Lenient where firmware is known to deviate: the message type or the unique id as a string or
    /// a number, a missing payload (taken as <c>{}</c>), a null payload.
    /// </summary>
    public static bool TryParse(string text, out OcppFrame? frame, out string? error)
    {
        frame = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3)
            {
                error = "A message must be a JSON array with at least 3 elements";
                return false;
            }

            if (!TryReadMessageType(root[0], out var type))
            {
                error = $"Unknown message type {root[0]}";
                return false;
            }

            var uniqueId = ReadText(root[1]);
            if (string.IsNullOrEmpty(uniqueId))
            {
                error = "Missing unique id";
                return false;
            }

            switch (type)
            {
                case OcppMessageType.Call:
                    var action = ReadText(root[2]);
                    if (string.IsNullOrWhiteSpace(action))
                    {
                        error = "Missing action";
                        return false;
                    }

                    frame = new OcppFrame
                    {
                        Type = type,
                        UniqueId = uniqueId,
                        Action = action.Trim(),
                        Payload = root.GetArrayLength() > 3 ? PayloadOf(root[3]) : EmptyObject,
                    };
                    return true;

                case OcppMessageType.CallResult:
                    frame = new OcppFrame { Type = type, UniqueId = uniqueId, Payload = PayloadOf(root[2]) };
                    return true;

                default:
                    frame = new OcppFrame
                    {
                        Type = type,
                        UniqueId = uniqueId,
                        ErrorCode = ReadText(root[2]) ?? OcppErrorCodes.GenericError,
                        ErrorDescription = root.GetArrayLength() > 3 ? ReadText(root[3]) : null,
                        Payload = root.GetArrayLength() > 4 ? PayloadOf(root[4]) : EmptyObject,
                    };
                    return true;
            }
        }
        catch (JsonException e)
        {
            error = $"Invalid JSON: {e.Message}";
            return false;
        }
    }

    /// <summary>Best-effort unique id of a message that failed to parse, so the CALLERROR can still refer to it.</summary>
    public static string? TryReadUniqueId(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 1 ? ReadText(root[1]) : null;
        }
        catch (JsonException)
        {
            // Broken JSON: the id is still readable from the start of the array.
            var match = System.Text.RegularExpressions.Regex.Match(text, @"^\s*\[\s*""?\d""?\s*,\s*""([^""]{1,64})""");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    public static string Call(string uniqueId, string action, object payload) =>
        $"[2,{JsonSerializer.Serialize(uniqueId)},{JsonSerializer.Serialize(action)},{SerializePayload(payload)}]";

    public static string CallResult(string uniqueId, object payload) =>
        $"[3,{JsonSerializer.Serialize(uniqueId)},{SerializePayload(payload)}]";

    public static string CallError(string uniqueId, string errorCode, string? errorDescription) =>
        $"[4,{JsonSerializer.Serialize(uniqueId)},{JsonSerializer.Serialize(errorCode)},{JsonSerializer.Serialize(Truncate(errorDescription ?? string.Empty, 255))},{{}}]";

    private static string SerializePayload(object payload) =>
        payload is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(payload, payload.GetType(), OcppJson.Options);

    private static bool TryReadMessageType(JsonElement element, out OcppMessageType type)
    {
        type = default;
        int number;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out number)
            || element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number))
        {
            if (number is >= 2 and <= 4)
            {
                type = (OcppMessageType)number;
                return true;
            }
        }

        return false;
    }

    private static string? ReadText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null,
    };

    private static JsonElement PayloadOf(JsonElement element) =>
        element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? EmptyObject : element.Clone();

    private static string Truncate(string text, int length) => text.Length > length ? text[..length] : text;

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(Type).Append(' ').Append(UniqueId);
        if (Action != null) builder.Append(' ').Append(Action);
        if (ErrorCode != null) builder.Append(' ').Append(ErrorCode);
        return builder.ToString();
    }
}

/// <summary>Error codes of a CALLERROR (OCPP-J 1.6, 4.2.3). "Occurence" is spelled as in the specification.</summary>
public static class OcppErrorCodes
{
    public const string NotImplemented = "NotImplemented";
    public const string NotSupported = "NotSupported";
    public const string InternalError = "InternalError";
    public const string ProtocolError = "ProtocolError";
    public const string SecurityError = "SecurityError";
    public const string FormationViolation = "FormationViolation";
    public const string PropertyConstraintViolation = "PropertyConstraintViolation";
    public const string OccurenceConstraintViolation = "OccurenceConstraintViolation";
    public const string TypeConstraintViolation = "TypeConstraintViolation";
    public const string GenericError = "GenericError";
}

/// <summary>Action names of OCPP 1.6.</summary>
public static class OcppActions
{
    // Charge point -> central system
    public const string Authorize = "Authorize";
    public const string BootNotification = "BootNotification";
    public const string DataTransfer = "DataTransfer";
    public const string DiagnosticsStatusNotification = "DiagnosticsStatusNotification";
    public const string FirmwareStatusNotification = "FirmwareStatusNotification";
    public const string Heartbeat = "Heartbeat";
    public const string MeterValues = "MeterValues";
    public const string StartTransaction = "StartTransaction";
    public const string StatusNotification = "StatusNotification";
    public const string StopTransaction = "StopTransaction";

    // Central system -> charge point
    public const string ChangeAvailability = "ChangeAvailability";
    public const string ChangeConfiguration = "ChangeConfiguration";
    public const string ClearCache = "ClearCache";
    public const string GetConfiguration = "GetConfiguration";
    public const string RemoteStartTransaction = "RemoteStartTransaction";
    public const string RemoteStopTransaction = "RemoteStopTransaction";
    public const string Reset = "Reset";
    public const string TriggerMessage = "TriggerMessage";
    public const string UnlockConnector = "UnlockConnector";
}
