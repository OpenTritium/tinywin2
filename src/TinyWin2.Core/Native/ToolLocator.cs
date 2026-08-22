namespace TinyWin2.Core.Native;

/// <summary>Locates a native tool on PATH. Callers pass full file names ("dism.exe").
/// No PATHEXT probing by design: every caller in this repo knows its exact tool name.</summary>
public static class ToolLocator {
    public static string? Locate(string fileName) {
        if (string.IsNullOrWhiteSpace(fileName)) {
            return null;
        }

        if (Path.IsPathRooted(fileName)) {
            var fullPath = Path.GetFullPath(fileName);
            return File.Exists(fullPath) ? fullPath : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        // File.Exists never throws; malformed PATH entries simply miss.
        var found = path?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"')) // legacy quoted PATH entries
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
        return found is null ? null : Path.GetFullPath(found);
    }
}
