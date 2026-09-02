using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Strongly-typed bound payloads for the dism-family resources.</summary>
public sealed record FeatureOptions {
    public required IReadOnlyList<string> Features { get; init; }
    public bool RemovePayload { get; private init; } = true;
    public bool ForceExplicit { get; private init; }

    public static FeatureOptions FromDesired(JsonObject desired) {
        var features = Desired.RequiredStringArray(desired, "features", "dism.feature");
        if (features.Count == 0) {
            throw new ExecException("dism.feature requires at least one feature name.");
        }

        return new() {
            Features = features,
            RemovePayload = Desired.OptionalBool(desired, "removePayload", true),
            ForceExplicit = Desired.OptionalBool(desired, "forceExplicit", false)
        };
    }
}

public sealed record CapabilityOptions {
    public required IReadOnlyList<string> Capabilities { get; init; }

    public static CapabilityOptions FromDesired(JsonObject desired) {
        var capabilities = Desired.RequiredStringArray(desired, "capabilities", "dism.capability");
        if (capabilities.Count == 0) {
            throw new ExecException("dism.capability requires at least one capability name.");
        }

        return new CapabilityOptions { Capabilities = capabilities };
    }
}

public sealed record PackageOptions {
    public required IReadOnlyList<Regex> Patterns { get; init; }

    public static PackageOptions FromDesired(JsonObject desired) {
        var raw = Desired.RequiredStringArray(desired, "patterns", "dism.package");
        if (raw.Count == 0) {
            throw new ExecException("dism.package requires at least one pattern.");
        }

        var compiled = new List<Regex>();
        foreach (var pattern in raw) {
            try {
                compiled.Add(new(pattern, RegexOptions.CultureInvariant));
            }
            catch (ArgumentException ex) {
                throw new ExecException($"invalid package pattern '{pattern}': {ex.Message}");
            }
        }

        return new() { Patterns = compiled };
    }
}

public sealed record ComponentStoreOptions {
    public bool ResetBase { get; private init; }

    public static ComponentStoreOptions FromDesired(JsonObject desired) =>
        new() { ResetBase = Desired.OptionalBool(desired, "resetBase", false) };
}

public sealed record AppxOptions {
    public required IReadOnlyList<string> Patterns { get; init; }

    public static AppxOptions FromDesired(JsonObject desired) {
        var raw = Desired.RequiredStringArray(desired, "patterns", "appx.provisioned");
        return raw.Count == 0
            ? throw new ExecException("appx.provisioned requires at least one pattern.")
            : new AppxOptions { Patterns = raw };
    }
}
