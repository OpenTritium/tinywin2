using System.Text.Json.Nodes;

namespace TinyWin2.Core.Executers.Driver;

public sealed record DriverStoreOptions {
    public required IReadOnlyList<string> InfNames { get; init; }
    public bool ForceUnusedInbox { get; init; }

    public static DriverStoreOptions FromDesired(JsonObject desired) {
        var infNames = Desired.RequiredStringArray(desired, "infNames", "driver.store");
        if (infNames.Count == 0) {
            throw new ExecException("driver.store requires at least one INF name.");
        }

        foreach (var infName in infNames) {
            var normalized = Path.GetFileName(infName);
            if (!string.Equals(normalized, infName, StringComparison.Ordinal)
                || !infName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
                || infName.Contains('*')
                || infName.Contains('?')
                || infName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
                throw new ExecException($"invalid driver INF name '{infName}'.");
            }
        }

        return new() {
            InfNames = infNames,
            ForceUnusedInbox = Desired.OptionalBool(desired, "forceUnusedInbox", false)
        };
    }
}
