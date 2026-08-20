using System.Text.Json.Nodes;

namespace TinyWin2.Core.Executers.Driver;

/// <summary>Strongly-typed bound payload for <c>driver.store</c>.</summary>
public sealed record DriverStoreOptions {
    public required IReadOnlyList<string> InfNames { get; init; }

    public static DriverStoreOptions FromDesired(JsonObject desired) {
        var infNames = Desired.RequiredStringArray(desired, "infNames", "driver.store");
        if (infNames.Count == 0) {
            throw new ExecException("driver.store requires at least one INF name.");
        }

        foreach (var infName in from infName in infNames
                                let normalized = Path.GetFileName(infName)
                                where !string.Equals(normalized, infName, StringComparison.Ordinal)
                                      || !infName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
                                select infName) {
            throw new ExecException($"invalid driver INF name '{infName}'.");
        }

        return new() { InfNames = infNames };
    }
}
