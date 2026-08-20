using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyWin2.Core;

/// <summary>Shared JSON writer options: indented + CJK-friendly escaping.</summary>
public static class Json {
    public static readonly JsonSerializerOptions Pretty = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Compact = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToPrettyString(this JsonObject obj) => obj.ToJsonString(Pretty);

    public static string ToCompactString(this JsonObject obj) => obj.ToJsonString(Compact);
}
