namespace TinyWin2.Core.Plans;

/// <summary>Locates a plan's bundled asset files (<c>plans/assets/&lt;planId&gt;</c>).</summary>
public static class PlanAssets {
    /// <summary>Root of the plan's asset files, or null when the plan ships no assets directory.</summary>
    public static string? ResolveRoot(string? plansDirectory, string planId) {
        if (plansDirectory is null) {
            return null;
        }

        var candidate = Path.Combine(plansDirectory, "assets", planId);
        return Directory.Exists(candidate) ? candidate : null;
    }
}
