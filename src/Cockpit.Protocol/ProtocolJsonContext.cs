using System.Text.Json.Serialization;

namespace Cockpit.Protocol;

/// <summary>Source-generated metadata so the bridge stays trimming- and Native AOT-safe.</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;
