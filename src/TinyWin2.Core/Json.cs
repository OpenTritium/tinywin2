using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyWin2.Core;

/// <summary>
///     Shared JSON options for hand-built protocol and manifest objects. Protocol DTOs (BuildEvent,
///     manifest shapes) keep hand-written output because their abbreviated keys (seq/ts) are fixed
///     contracts and this keeps the pipeline AOT-safe.
/// </summary>
public static class Json {
    private static readonly JsonSerializerOptions Pretty = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions Compact = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    extension(JsonObject obj) {
        public string ToPrettyString() => obj.ToJsonString(Pretty);
        public string ToCompactString() => obj.ToJsonString(Compact);
    }
}
