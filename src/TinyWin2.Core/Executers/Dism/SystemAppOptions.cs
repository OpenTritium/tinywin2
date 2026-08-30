using System.Text.Json.Nodes;

namespace TinyWin2.Core.Executers.Dism;

internal sealed record SystemAppOptions {
    public required IReadOnlyList<string> Patterns { get; init; }

    public static SystemAppOptions FromDesired(JsonObject desired) {
        var patterns = Desired.RequiredStringArray(desired, "patterns", "appx.system");
        return patterns.Count == 0
            ? throw new ExecException("appx.system requires at least one pattern.")
            : new() { Patterns = patterns };
    }
}
