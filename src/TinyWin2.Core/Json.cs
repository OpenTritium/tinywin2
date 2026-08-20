using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TinyWin2.Core;

/// <summary>Shared JSON options. Serialize plain records with <see cref="Records"/> instead of
/// hand-writing ToJson; protocol DTOs (BuildEvent, manifest shapes) keep hand-written output
/// because their abbreviated keys (seq/ts) are fixed contracts.</summary>
public static class Json {
    private static readonly JsonSerializerOptions Pretty = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Record serialization: camelCase keys, nulls omitted, CJK-safe.</summary>
    private static readonly JsonSerializerOptions Records = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToPrettyString(this JsonObject obj) => obj.ToJsonString(Pretty);

    public static string ToCompactString(this JsonObject obj) => obj.ToJsonString(Compact);

    /// <summary>Serializes a record/collection into a JsonNode (object or array) using <see cref="Records"/>.</summary>
    public static JsonNode ToNode<T>(T value) => JsonNode.Parse(JsonSerializer.Serialize(value, Records))!;
}
