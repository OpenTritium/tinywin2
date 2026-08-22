using System.Text.Json.Nodes;

namespace TinyWin2.Core.Plans;

/// <summary>
///     Binds plan <c>parameters</c> into operation <c>spec</c> payloads at resolution time:
///     <c>{"$parameter":"name"}</c> references a parameter value;
///     <c>{"$map":{"parameter":"n","cases":{...},"default":...}}</c>
///     translates a parameter value into resource-specific data. Output is pure data.
/// </summary>
public static class ParameterBinder {
    public static JsonObject BindOperation(JsonObject spec, IReadOnlyDictionary<string, JsonNode?> values) {
        var bound = Bind(spec, values, "$");
        return bound as JsonObject ??
               throw new ParameterBindingException("$", "'spec' must remain an object after binding.");
    }

    private static JsonNode? Bind(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> parameterValues, string path) {
        switch (node) {
            case null:
                return null;
            case JsonObject obj:
                if (obj.ContainsKey("$parameter")) {
                    if (obj["$parameter"] is not JsonValue parameterValue
                        || !parameterValue.TryGetValue<string>(out var parameterName)
                        || string.IsNullOrWhiteSpace(parameterName)) {
                        throw new ParameterBindingException(path, "$parameter requires a non-empty string.");
                    }

                    return obj.Count != 1
                        ? throw new ParameterBindingException(path, "$parameter node must contain only the reference.")
                        : ResolveParameter(parameterValues, parameterName, path)?.DeepClone();
                }

                if (obj.ContainsKey("$map")) {
                    if (obj["$map"] is not JsonObject map) {
                        throw new ParameterBindingException(path, "$map requires an object.");
                    }

                    return obj.Count != 1
                        ? throw new ParameterBindingException(path, "$map node must contain only the transform.")
                        : ApplyMap(map, parameterValues, path)?.DeepClone();
                }

                var result = new JsonObject();
                foreach (var (key, value) in obj) {
                    result[key] = Bind(value, parameterValues, $"{path}.{key}");
                }

                return result;
            case JsonArray array:
                var boundArray = new JsonArray();
                var index = 0;
                foreach (var item in array) {
                    boundArray.Add(Bind(item, parameterValues, $"{path}[{index++}]"));
                }

                return boundArray;
            default:
                return node.DeepClone();
        }
    }

    private static JsonNode? ResolveParameter(IReadOnlyDictionary<string, JsonNode?> parameterValues,
        string parameterName, string path) {
        if (!parameterValues.TryGetValue(parameterName, out var value)) {
            throw new ParameterBindingException(path, $"references unknown parameter '{parameterName}'.");
        }

        return value;
    }

    private static JsonNode? ApplyMap(JsonObject map, IReadOnlyDictionary<string, JsonNode?> parameterValues,
        string path) {
        var unknown = map.Select(property => property.Key)
            .FirstOrDefault(pname => pname is not "parameter" and not "cases" and not "default");
        if (unknown is not null) {
            throw new ParameterBindingException(path, $"$map contains unknown field '{unknown}'.");
        }

        var parameterName = map["parameter"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name)
            ? name
            : null;
        if (string.IsNullOrWhiteSpace(parameterName)) {
            throw new ParameterBindingException(path, "$map requires a non-empty string 'parameter'.");
        }

        var source = ResolveParameter(parameterValues, parameterName, path);
        var sourceKey = source is JsonValue sourceValue && sourceValue.TryGetValue<string>(out var text)
            ? text
            : source?.ToJsonString();
        JsonObject? cases = null;
        if (map.TryGetPropertyValue("cases", out var casesNode)) {
            cases = casesNode as JsonObject
                    ?? throw new ParameterBindingException(path, "$map 'cases' must be an object.");
        }

        if (cases is not null && sourceKey is not null && cases.TryGetPropertyValue(sourceKey, out var mapped)) {
            return mapped;
        }

        if (map.TryGetPropertyValue("default", out var fallback)) {
            return fallback;
        }

        throw new ParameterBindingException(path,
            $"$map has no case for parameter '{parameterName}' value '{sourceKey}'.");
    }
}

public sealed class ParameterBindingException(string path, string message)
    : Exception($"Parameter binding failed at '{path}': {message}");
