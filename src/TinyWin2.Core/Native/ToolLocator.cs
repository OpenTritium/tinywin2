namespace TinyWin2.Core.Native;

/// <summary>Locates a native tool on PATH. Callers pass full file names ("dism.exe").</summary>
public static class ToolLocator {
    public static string? Locate(string fileName) {
        if (Path.IsPathRooted(fileName)) {
            return File.Exists(fileName) ? fileName : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) {
            return null;
        }
        // File.Exists never throws (malformed PATH entries simply miss), so no try/catch.
        var found = path
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
        return found is null ? null : Path.GetFullPath(found);
    }
}
