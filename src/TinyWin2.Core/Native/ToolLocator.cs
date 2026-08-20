namespace TinyWin2.Core.Native;

/// <summary>Locates native tools on PATH or in well-known directories.</summary>
public static class ToolLocator {
    public static string? Locate(string fileName, params string[] extraDirectories) {
        if (Path.IsPathRooted(fileName)) {
            return File.Exists(fileName) ? fileName : null;
        }

        foreach (var directory in extraDirectories) {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) {
                return Path.GetFullPath(candidate);
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVariable)) {
            foreach (var directory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                try {
                    var candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate)) {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) {
                    // Malformed PATH entries are skipped, not fatal.
                }
            }
        }
        return null;
    }
}
