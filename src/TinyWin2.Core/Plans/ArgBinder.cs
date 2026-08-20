using System.Text.Json.Nodes;

namespace TinyWin2.Core.Plans;

/// <summary>
/// Binds plan <c>arguments</c> into exec <c>with</c> payloads at resolution time:
/// <c>{"$arg":"name"}</c> references an argument value; <c>{"$map":{"arg":"n","cases":{...},"default":...}}</c>
/// translates an argument value into resource-specific data. Output is pure data.
/// </summary>
public static class ArgBinder
{
    public static JsonObject BindExec(PlanExec exec, IReadOnlyDictionary<string, JsonNode?> argValues)
    {
        var bound = Bind(exec.With, argValues, "$");
        return bound as JsonObject ?? throw new ArgBindException("$", "'with' must remain an object after binding.");
    }

    public static JsonNode? Bind(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> argValues, string path)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                if (obj.TryGetPropertyValue("$arg", out var argRef) && argRef is JsonValue argValue && argValue.TryGetValue<string>(out var argName))
                {
                    if (obj.Count != 1)
                    {
                        throw new ArgBindException(path, "$arg node must contain only the reference.");
                    }
                    return ResolveArg(argValues, argName, path)!.DeepClone();
                }
                if (obj.TryGetPropertyValue("$map", out var mapRef) && mapRef is JsonObject map)
                {
                    if (obj.Count != 1)
                    {
                        throw new ArgBindException(path, "$map node must contain only the transform.");
                    }
                    return ApplyMap(map, argValues, path)!.DeepClone();
                }
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    result[key] = Bind(value, argValues, $"{path}.{key}");
                }
                return result;
            case JsonArray array:
                var boundArray = new JsonArray();
                var index = 0;
                foreach (var item in array)
                {
                    boundArray.Add(Bind(item, argValues, $"{path}[{index++}]"));
                }
                return boundArray;
            default:
                return node.DeepClone();
        }
    }

    private static JsonNode? ResolveArg(IReadOnlyDictionary<string, JsonNode?> argValues, string argName, string path)
    {
        if (!argValues.TryGetValue(argName, out var value))
        {
            throw new ArgBindException(path, $"references unknown argument '{argName}'.");
        }
        return value;
    }

    private static JsonNode? ApplyMap(JsonObject map, IReadOnlyDictionary<string, JsonNode?> argValues, string path)
    {
        var argName = map["arg"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var n) ? n : null;
        if (argName is null)
        {
            throw new ArgBindException(path, "$map requires a string 'arg'.");
        }
        var source = ResolveArg(argValues, argName, path);
        var sourceKey = source is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : source?.ToJsonString();

        if (map["cases"] is JsonObject cases && sourceKey is not null && cases.TryGetPropertyValue(sourceKey, out var mapped))
        {
            return mapped;
        }
        if (map.TryGetPropertyValue("default", out var fallback) && fallback is not null)
        {
            return fallback;
        }
        throw new ArgBindException(path, $"$map has no case for argument '{argName}' value '{sourceKey}'.");
    }
}

public sealed class ArgBindException(string path, string message)
    : Exception($"Argument binding failed at '{path}': {message}")
{
    public string Path2 { get; } = path;
}
