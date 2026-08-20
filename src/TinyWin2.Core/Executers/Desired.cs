using System.Text.Json.Nodes;

namespace TinyWin2.Core.Executers;

/// <summary>
/// Boundary readers: the ONLY place in the engine that pulls values out of a
/// bound <c>with</c> JsonObject. Everything past this point is strongly typed.
/// </summary>
internal static class Desired {
    internal static string RequiredString(JsonObject desired, string key, string context) {
        var value = OptionalString(desired, key);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ExecException($"{context} requires '{key}'.");
        }
        return value;
    }

    internal static string? OptionalString(JsonObject desired, string key) {
        if (!desired.TryGetPropertyValue(key, out var node) || node is null) {
            return null;
        }
        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new ExecException($"'{key}' must be a string.");
    }

    internal static List<string> RequiredStringArray(JsonObject desired, string key, string context) =>
        OptionalStringArray(desired, key)
        ?? throw new ExecException($"{context} requires '{key}'.");

    internal static List<string>? OptionalStringArray(JsonObject desired, string key) {
        if (!desired.TryGetPropertyValue(key, out var node) || node is null) {
            return null;
        }
        if (node is not JsonArray array) {
            throw new ExecException($"'{key}' must be an array of strings.");
        }
        var result = new List<string>();
        foreach (var item in array) {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) {
                result.Add(text);
            }
            else {
                throw new ExecException($"'{key}' must contain non-empty strings.");
            }
        }
        return result;
    }

    internal static bool OptionalBool(JsonObject desired, string key, bool fallback) {
        if (!desired.TryGetPropertyValue(key, out var node) || node is null) {
            return fallback;
        }
        return node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : throw new ExecException($"'{key}' must be a boolean.");
    }

    internal static List<JsonObject> ObjectArray(JsonObject desired, string key) {
        var result = new List<JsonObject>();
        if (desired.TryGetPropertyValue(key, out var node) && node is JsonArray array) {
            result.AddRange(array.OfType<JsonObject>());
        }
        return result;
    }

    internal static JsonObject? OptionalObject(JsonObject desired, string key) =>
        desired.TryGetPropertyValue(key, out var node) && node is JsonObject obj ? obj : null;
}
