using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Driver;

/// <summary>
/// Converges inbox driver-store packages. absent = delete FileRepository directories
/// named after the declared INF allowlist. Present direction is reserved for /Add-Driver.
/// Desired: <c>{ infNames:["mdm.inf"] }</c>.
/// </summary>
public sealed class DriverStoreExecuter(IProcessRunner runner) : IExecuter {
    public const string ResourceId = "driver.store";
    public string Resource => ResourceId;

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException("driver.store present (driver integration) is not implemented yet.");
        }
        var options = DriverStoreOptions.FromDesired(spec.Desired);
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            context.MountPath, "Windows", "System32", "DriverStore", "FileRepository"));
        if (!Directory.Exists(repositoryRoot)) {
            throw new ExecException($"Driver Store was not found at '{repositoryRoot}'.");
        }
        var differences = new List<ChangeItem>();
        foreach (var infName in options.InfNames) {
            var matches = Directory.EnumerateDirectories(repositoryRoot, $"{infName}_*", SearchOption.TopDirectoryOnly)
                .ToList();
            if (matches.Count == 0) {
                context.Log.Info($"skipping unavailable Driver Store package: {infName}");
                differences.Add(new ChangeItem(ChangeKind.Skipped, infName, "not in FileRepository"));
                continue;
            }
            foreach (var directory in matches) {
                var relative = Path.GetRelativePath(context.MountPath, directory);
                differences.Add(new ChangeItem(ChangeKind.Removed, relative, Before: infName));
            }
        }
        return Task.FromResult(new ResourceDiff(differences.All(d => d.Kind == ChangeKind.Skipped), differences));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped("no matching Driver Store packages",
                diff.Differences.Where(d => d.Kind == ChangeKind.Skipped).ToArray());
        }
        var applied = new List<ChangeItem>();
        foreach (var change in diff.Differences.Where(d => d.Kind != ChangeKind.Skipped)) {
            var directory = Path.GetFullPath(Path.Combine(context.MountPath, change.Target));
            var repositoryRoot = Path.GetFullPath(Path.Combine(
                context.MountPath, "Windows", "System32", "DriverStore", "FileRepository"));
            if (!directory.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                throw new ExecException($"Driver Store package '{change.Target}' resolved outside FileRepository.");
            }
            context.Log.Warn($"removing Driver Store package directory: {change.Target}");
            try {
                TryDelete(directory);
            }
            catch (UnauthorizedAccessException) {
                await GrantDeleteAccessAsync(directory, ct);
                TryDelete(directory);
            }
            applied.Add(change);
        }
        return ExecResult.Applied(applied);
    }

    private static void TryDelete(string directory) {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>v1 fallback: protected packages need ownership + Administrators full control first.</summary>
    private async Task GrantDeleteAccessAsync(string directory, CancellationToken ct) {
        await runner.RunAsync("takeown.exe", ["/F", directory, "/A", "/R", "/D", "Y"], cancellationToken: ct);
        await runner.RunAsync("icacls.exe", [directory, "/grant", "*S-1-5-32-544:F", "/T"], cancellationToken: ct);
    }

}
