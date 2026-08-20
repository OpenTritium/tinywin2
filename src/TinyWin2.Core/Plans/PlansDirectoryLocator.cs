namespace TinyWin2.Core.Plans;

/// <summary>Locates the repository's plans/ directory by walking up from a start directory.</summary>
public static class PlansDirectoryLocator {
    /// <summary>
    /// Probes <paramref name="startAt"/> and its ancestors (then the current directory) for a
    /// plans/ child. Shared by the CLI's --plans discovery and the GUI's repository locator
    /// so both find the same directory.
    /// </summary>
    public static string? TryLocate(string? startAt = null, int maxHops = 8) {
        var probes = new List<string>();
        var current = startAt ?? AppContext.BaseDirectory;
        for (var i = 0; i <= maxHops && current is not null; i++) {
            probes.Add(current);
            current = Directory.GetParent(current)?.FullName;
        }
        probes.Add(Environment.CurrentDirectory);
        return probes
            .Select(probe => Path.Combine(probe, "plans"))
            .FirstOrDefault(Directory.Exists);
    }
}
