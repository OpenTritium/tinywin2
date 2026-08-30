using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>Strongly-typed bound payload for <c>registry.value</c>.</summary>
public sealed record RegistryValueOptions {
    public required string Hive { get; init; }
    public required IReadOnlyList<RegistryValueTarget> Values { get; init; }
    public IReadOnlyList<string> DeleteKeys { get; private init; } = [];

    public static RegistryValueOptions FromDesired(JsonObject desired, OperationAction action) {
        const string context = "registry.value";
        var hive = Desired.RequiredString(desired, "hive", context);
        if (!RegistryHiveCache.HiveFiles.ContainsKey(hive)) {
            throw new ExecException(
                $"{context} uses unsupported registry hive '{hive}' (expected one of: {string.Join(", ", RegistryHiveCache.HiveFiles.Keys)}).");
        }

        var targets = new List<RegistryValueTarget>();
        var declared = false;
        foreach (var raw in Desired.ObjectArray(desired, "values")) {
            targets.Add(RegistryValueTarget.FromDesired(raw, action));
            declared = true;
        }

        if (desired.ContainsKey("key")) {
            // Single-value shorthand at the top level.
            targets.Add(RegistryValueTarget.FromDesired(desired, action));
            declared = true;
        }

        var deleteKeys = Desired.OptionalStringArray(desired, "deleteKeys") ?? [];
        if (action == OperationAction.Set && deleteKeys.Count > 0) {
            throw new ExecException("'deleteKeys' is only valid with action: remove.");
        }

        if (!declared && deleteKeys.Count == 0) {
            throw new ExecException($"{context} requires 'values', a single value form, or 'deleteKeys'.");
        }

        return new() { Hive = hive, Values = targets, DeleteKeys = deleteKeys };
    }
}

public sealed record RegistryValueTarget {
    public required string Key { get; init; }
    public string Name { get; private init; } = "";

    /// <summary>Friendly type (dword/qword/string/expand/multi/binary); null for absent targets.</summary>
    private string? Type { get; init; }

    public JsonNode? Data { get; private init; }

    /// <summary>REG_* form for reg.exe.</summary>
    public string RegType => RegistryValueTypes.ToRegType(Type!);

    public static RegistryValueTarget FromDesired(JsonObject raw, OperationAction action) {
        var key = Desired.RequiredString(raw, "key", "registry value").Trim('\\');
        var name = Desired.OptionalString(raw, "name") ?? "";
        if (action == OperationAction.Remove) {
            return new() { Key = key, Name = name };
        }

        var type = Desired.OptionalString(raw, "type");
        if (type is null || !RegistryValueTypes.IsSupported(type)) {
            throw new ExecException(
                $"registry value '{key}\\{name}' requires a valid 'type' (dword|qword|string|expand|multi|binary).");
        }

        return !raw.TryGetPropertyValue("data", out var data) || data is null
            ? throw new ExecException($"registry value '{key}\\{name}' requires 'data'.")
            : new() { Key = key, Name = name, Type = type, Data = data.DeepClone() };
    }
}

public static class RegistryValueTypes {
    private static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["dword"] = "REG_DWORD",
            ["qword"] = "REG_QWORD",
            ["string"] = "REG_SZ",
            ["expand"] = "REG_EXPAND_SZ",
            ["multi"] = "REG_MULTI_SZ",
            ["binary"] = "REG_BINARY"
        };

    public static bool IsSupported(string type) => Map.ContainsKey(type);

    public static string ToRegType(string friendlyType) =>
        Map.TryGetValue(friendlyType, out var regType)
            ? regType
            : throw new ExecException($"unsupported registry value type '{friendlyType}'.");
}

/// <summary>Strongly-typed bound payload for <c>registry.service</c>.</summary>
public sealed partial record RegistryServiceOptions {
    private static readonly IReadOnlyDictionary<string, int> StartValues =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
            ["disabled"] = 4,
            ["manual"] = 3,
            ["auto"] = 2,
            ["delayedAuto"] = 2,
            ["trigger"] = 3 // manual + TriggerInfo: the SCM pulls the service when the event fires
        };

    /// <summary>
    ///     Named SCM trigger events (offline TriggerInfo writes; Action is always SERVICE_START).
    ///     Presets carry the documented subtype GUID; device classes take a raw interface-class GUID.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Type, Guid SubType)> TriggerKinds =
        new Dictionary<string, (int, Guid)>(StringComparer.OrdinalIgnoreCase) {
            ["domain-join"] = (3, Guid.Parse("1ce20aba-9851-4421-9430-1ddeb766e809")),
            ["ip-arrival"] = (2, Guid.Parse("4f27f2de-14e2-430b-a549-7cd48cbc8245")),
            ["gpo-change"] = (5, Guid.Parse("659fcae6-5bdb-4da9-b1ff-ca2a178d46e0"))
        };

    public required IReadOnlyList<string> Services { get; init; }
    public IReadOnlyList<string> ServicePatterns { get; private init; } = [];
    public required string Start { get; init; }

    /// <summary>Parsed trigger descriptors: preset name or 'device:{interface-class-guid}'.</summary>
    public IReadOnlyList<string> Triggers { get; private init; } = [];

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
            throw new ExecException($"{context} requires 'start' (auto|delayedAuto|manual|disabled|trigger).");
        }

        var triggers = Desired.OptionalStringArray(desired, "triggers") ?? [];
        if (triggers.Count > 0 && !string.Equals(start, "trigger", StringComparison.OrdinalIgnoreCase)) {
            throw new ExecException($"{context}: 'triggers' requires 'start': 'trigger' (manual + TriggerInfo).");
        }

        if (string.Equals(start, "trigger", StringComparison.OrdinalIgnoreCase) && triggers.Count == 0) {
            throw new ExecException($"{context}: 'start': 'trigger' requires at least one trigger.");
        }

        foreach (var trigger in triggers) {
            if (!TriggerKinds.ContainsKey(trigger)
                && !(trigger.StartsWith("device:", StringComparison.OrdinalIgnoreCase)
                     && Guid.TryParse(trigger["device:".Length..], out _))) {
                throw new ExecException(
                    $"{context}: unknown trigger '{trigger}' (expected domain-join|ip-arrival|gpo-change|device:{{guid}}).");
            }
        }

        return new() { Services = services, ServicePatterns = patterns, Start = start, Triggers = triggers };
    }

    internal IReadOnlyList<ServiceTrigger> ResolveTriggers() =>
        [.. Triggers.Select(ResolveTrigger).OfType<ServiceTrigger>()];

    /// <summary>Resolves a trigger descriptor to its SCM (Type, SubType) pair; null when unknown.</summary>
    internal static ServiceTrigger? ResolveTrigger(string trigger) {
        if (TriggerKinds.TryGetValue(trigger, out var preset)) {
            return new(preset.Type, preset.SubType);
        }

        if (trigger.StartsWith("device:", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(trigger["device:".Length..], out var guid)) {
            return new(1, guid);
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_.?*-]+$")]
    private static partial Regex ServiceNameChars();

    internal sealed record ServiceTrigger(int Type, Guid SubType);
}
