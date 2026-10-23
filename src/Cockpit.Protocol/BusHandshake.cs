using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Protocol;

/// <summary>
/// First frame a bridge sends after connecting. The job ID and token come from the bridge's
/// command line (written into the generated MCP config by the Core), never from model output.
/// The token is not secret from the agent, which can read its own config; what makes it safe is
/// that the Core binds it to exactly one job's grant and revokes it when the job ends.
/// </summary>
public sealed record BusHello(int ProtocolVersion, string JobId, string Token, int BridgePid, string Transport)
{
    public const int CurrentProtocolVersion = 1;

    public string ToJson() => new JsonObject
    {
        ["type"] = "hello",
        ["protocolVersion"] = ProtocolVersion,
        ["jobId"] = JobId,
        ["token"] = Token,
        ["bridgePid"] = BridgePid,
        ["transport"] = Transport,
    }.ToJsonString();

    public static BusHello Parse(ReadOnlySpan<byte> utf8)
    {
        var node = JsonNode.Parse(utf8) as JsonObject ?? throw new InvalidDataException("Hello is not a JSON object.");
        if ((string?)node["type"] != "hello") throw new InvalidDataException("First frame must be a hello.");
        return new BusHello(
            (int?)node["protocolVersion"] ?? 0,
            (string?)node["jobId"] ?? throw new InvalidDataException("Hello is missing jobId."),
            (string?)node["token"] ?? throw new InvalidDataException("Hello is missing token."),
            (int?)node["bridgePid"] ?? 0,
            (string?)node["transport"] ?? "unknown");
    }
}

/// <summary>The Core's reply to a hello. On failure the Core closes the connection after sending it.</summary>
public sealed record BusWelcome(bool Ok, string? Error, string ServerVersion)
{
    public string ToJson() => new JsonObject
    {
        ["type"] = "welcome",
        ["ok"] = Ok,
        ["error"] = Error,
        ["serverVersion"] = ServerVersion,
    }.ToJsonString();

    public static BusWelcome Parse(ReadOnlySpan<byte> utf8)
    {
        var node = JsonNode.Parse(utf8) as JsonObject ?? throw new InvalidDataException("Welcome is not a JSON object.");
        if ((string?)node["type"] != "welcome") throw new InvalidDataException("Expected a welcome frame.");
        return new BusWelcome((bool?)node["ok"] ?? false, (string?)node["error"], (string?)node["serverVersion"] ?? "?");
    }
}

/// <summary>Where the Core listens: <c>pipe:&lt;name&gt;</c> on Windows, or <c>tcp:127.0.0.1:&lt;port&gt;</c> for WSL clients.</summary>
public sealed record BusEndpoint(string Kind, string PipeName, string Host, int Port)
{
    public const string DefaultPipeName = "ai-cockpit-bus";

    public static BusEndpoint Default { get; } = new("pipe", DefaultPipeName, "", 0);

    public static BusEndpoint Parse(string text)
    {
        if (text.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase))
        {
            string name = text[5..];
            if (name.Length == 0 || name.Contains('\\') || name.Contains('/'))
                throw new FormatException($"Invalid pipe name in '{text}'.");
            return new BusEndpoint("pipe", name, "", 0);
        }
        if (text.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            int colon = text.LastIndexOf(':');
            string host = text[4..colon];
            if (!int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
                throw new FormatException($"Invalid port in '{text}'.");
            if (host is not ("127.0.0.1" or "localhost" or "::1"))
                throw new FormatException("The bus only listens on loopback; use 127.0.0.1.");
            return new BusEndpoint("tcp", "", host, port);
        }
        throw new FormatException($"Unknown endpoint '{text}'. Use pipe:<name> or tcp:127.0.0.1:<port>.");
    }

    public override string ToString() => Kind == "pipe" ? $"pipe:{PipeName}" : $"tcp:{Host}:{Port}";
}

/// <summary>Small JSON-RPC 2.0 helpers shared by the bridge and the Core.</summary>
public static class JsonRpc
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    /// <summary>Implementation-defined: the bridge gave up waiting for the Core.</summary>
    public const int CockpitTimeout = -32001;
    /// <summary>Implementation-defined: the Core is unreachable.</summary>
    public const int CockpitUnavailable = -32002;

    /// <summary>
    /// Canonical key for a request ID. Compares by value, not by raw text: a peer that re-serializes the ID
    /// may escape it differently ("é" vs "\u00E9"). String "1" and number 1 stay distinct.
    /// </summary>
    public static string IdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => "s:" + id.GetString(),
        JsonValueKind.Number => "n:" + (id.TryGetInt64(out long n) ? n.ToString(CultureInfo.InvariantCulture) : id.GetRawText()),
        _ => "x:" + id.GetRawText(),
    };

    public static string ErrorResponse(JsonElement id, int code, string message) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"error\":{{\"code\":{code.ToString(CultureInfo.InvariantCulture)},\"message\":{JsonSerializer.Serialize(message, ProtocolJsonContext.Default.String)}}}}}";

    public static string CancelledNotification(JsonElement id, string reason) =>
        $"{{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{{\"requestId\":{id.GetRawText()},\"reason\":{JsonSerializer.Serialize(reason, ProtocolJsonContext.Default.String)}}}}}";

    /// <summary>Classifies a raw message without allocating a full DOM beyond what is needed.</summary>
    public static MessageShape Inspect(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return MessageShape.Invalid;
        bool hasMethod = root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
        bool hasId = root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        if (hasMethod) return hasId ? MessageShape.Request : MessageShape.Notification;
        return hasId ? MessageShape.Response : MessageShape.Invalid;
    }
}

public enum MessageShape { Invalid, Request, Notification, Response }
