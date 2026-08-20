using System.Text.Json.Nodes;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>Strongly-typed bound payload for <c>registry.value</c>.</summary>
public sealed record RegistryValueOptions {
    public required string Hive { get; init; }
    public required IReadOnlyList<RegistryValueTarget> Values { get; init; }
    public IReadOnlyList<string> DeleteKeys { get; init; } = [];

    public static RegistryValueOptions FromDesired(JsonObject desired, Ensure ensure) {
        const string context = "registry.value";
        var hive = Desired.RequiredString(desired, "hive", context);
        if (!RegistryHiveCache.HiveFiles.ContainsKey(hive)) {
            throw new ExecException($"{context} uses unsupported registry hive '{hive}' (expected one of: {string.Join(", ", RegistryHiveCache.HiveFiles.Keys)}).");
        }
        var targets = new List<RegistryValueTarget>();
        var declared = false;
        foreach (var raw in Desired.ObjectArray(desired, "values")) {
            targets.Add(RegistryValueTarget.FromDesired(raw, ensure));
            declared = true;
        }
        if (desired.ContainsKey("key")) {
            // Single-value shorthand at the top level.
            targets.Add(RegistryValueTarget.FromDesired(desired, ensure));
            declared = true;
        }
        var deleteKeys = Desired.OptionalStringArray(desired, "deleteKeys") ?? [];
        if (ensure == Ensure.Present && deleteKeys.Count > 0) {
            throw new ExecException("'deleteKeys' is only valid with ensure: absent.");
        }
        if (!declared && deleteKeys.Count == 0) {
            throw new ExecException($"{context} requires 'values', a single value form, or 'deleteKeys'.");
        }
        return new RegistryValueOptions { Hive = hive, Values = targets, DeleteKeys = deleteKeys };
    }
}

public sealed record RegistryValueTarget {
    public required string Key { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Friendly type (dword/qword/string/expand/multi); null for absent targets.</summary>
    public string? Type { get; init; }
    public JsonNode? Data { get; init; }

    /// <summary>REG_* form for reg.exe.</summary>
    public string RegType => RegistryValueTypes.ToRegType(Type!);

    public static RegistryValueTarget FromDesired(JsonObject raw, Ensure ensure) {
        var key = Desired.RequiredString(raw, "key", "registry value").Trim('\\');
        var name = Desired.OptionalString(raw, "name") ?? "";
        if (ensure == Ensure.Absent) {
            return new RegistryValueTarget { Key = key, Name = name };
        }
        var type = Desired.OptionalString(raw, "type");
        if (type is null || !RegistryValueTypes.IsSupported(type)) {
            throw new ExecException($"registry value '{key}\\{name}' requires a valid 'type' (dword|qword|string|expand|multi).");
        }
        if (!raw.ContainsKey("data")) {
            throw new ExecException($"registry value '{key}\\{name}' requires 'data'.");
        }
        return new RegistryValueTarget { Key = key, Name = name, Type = type, Data = raw["data"]!.DeepClone() };
    }
}

public static class RegistryValueTypes {
    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["dword"] = "REG_DWORD",
        ["qword"] = "REG_QWORD",
        ["string"] = "REG_SZ",
        ["expand"] = "REG_EXPAND_SZ",
        ["multi"] = "REG_MULTI_SZ",
    };

    public static bool IsSupported(string type) => Map.ContainsKey(type);

    public static string ToRegType(string friendlyType) =>
        Map.TryGetValue(friendlyType, out var regType)
            ? regType
            : throw new ExecException($"unsupported registry value type '{friendlyType}'.");
}

/// <summary>Strongly-typed bound payload for <c>registry.service</c>.</summary>
public sealed partial record RegistryServiceOptions {
    public required IReadOnlyList<string> Services { get; init; }
    public IReadOnlyList<string> ServicePatterns { get; init; } = [];
    public required string Start { get; init; }

    public static readonly IReadOnlyDictionary<string, int> StartValues = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
        ["disabled"] = 4,
        ["manual"] = 3,
        ["auto"] = 2,
        ["delayedAuto"] = 2,
    };

    public bool IsDelayed => string.Equals(Start, "delayedAuto", StringComparison.OrdinalIgnoreCase);
    public int StartDword => StartValues[Start];

    public static RegistryServiceOptions FromDesired(JsonObject desired) {
        const string context = "registry.service";
        var services = Desired.OptionalStringArray(desired, "services") ?? [];
        var patterns = Desired.OptionalStringArray(desired, "servicePatterns") ?? [];
        if (services.Count == 0 && patterns.Count == 0) {
            throw new ExecException($"{context} requires 'services' or 'servicePatterns'.");
        }
        foreach (var name in services.Concat(patterns)) {
            if (string.IsNullOrWhiteSpace(name) || !ServiceNameChars().IsMatch(name)) {
                throw new ExecException($"invalid service name or pattern '{name}'.");
            }
        }
        var start = Desired.RequiredString(desired, "start", context);
        if (!StartValues.ContainsKey(start)) {
            throw new ExecException($"{context} requires 'start' (auto|delayedAuto|manual|disabled).");
        }
        return new RegistryServiceOptions { Services = services, ServicePatterns = patterns, Start = start };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_.?*-]+$")]
    private static partial System.Text.RegularExpressions.Regex ServiceNameChars();
}
