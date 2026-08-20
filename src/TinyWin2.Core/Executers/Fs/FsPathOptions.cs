using System.Text.Json.Nodes;

namespace TinyWin2.Core.Executers.Fs;

/// <summary>Strongly-typed bound payload for <c>fs.path</c>: absent = delete paths, present = copy asset.</summary>
public sealed record FsPathOptions {
    public IReadOnlyList<string> Paths { get; init; } = [];
    public string? Path { get; init; }
    public string? Source { get; init; }

    public static FsPathOptions FromDesired(JsonObject desired, Ensure ensure) {
        const string context = "fs.path";
        if (ensure == Ensure.Absent) {
            var paths = Desired.RequiredStringArray(desired, "paths", context);
            return paths.Count == 0
                ? throw new ExecException("fs.path absent requires at least one path.")
                : new FsPathOptions { Paths = paths };
        }

        var path = Desired.RequiredString(desired, "path", context);
        var source = Desired.RequiredString(desired, "source", context);
        return new() { Path = path, Source = source };
    }
}
