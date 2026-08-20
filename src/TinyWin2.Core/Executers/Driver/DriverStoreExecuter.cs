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

    /// <summary>One structured difference carrying its resolved directory — apply never re-derives paths.</summary>
    private sealed record DriverChange(ChangeItem Change, string Directory);

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException("driver.store present (driver integration) is not implemented yet.");
        }

        var changes = InspectCore(context, spec);
        return Task.FromResult(new ResourceDiff(changes.All(c => c.Change.Kind == ChangeKind.Skipped),
            [.. changes.Select(c => c.Change)]));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var changes = InspectCore(context, spec);
        if (changes.All(c => c.Change.Kind == ChangeKind.Skipped)) {
            return ExecResult.Skipped("no matching Driver Store packages",
                [.. changes.Select(c => c.Change).Where(c => c.Kind == ChangeKind.Skipped)]);
        }

        foreach (var (change, directory) in changes) {
            if (change.Kind == ChangeKind.Skipped) {
                continue;
            }

            context.Log.Warn($"removing Driver Store package directory: {change.Target}");
            try {
                Delete(directory);
            }
            catch (UnauthorizedAccessException) {
                await GrantDeleteAccessAsync(directory, ct);
                Delete(directory);
            }
        }

        return ExecResult.Applied([.. changes.Where(c => c.Change.Kind != ChangeKind.Skipped).Select(c => c.Change)]);
    }

    private static List<DriverChange> InspectCore(ExecContext context, ExecSpec spec) {
        var options = DriverStoreOptions.FromDesired(spec.Desired);
        var repositoryRoot = RepositoryRoot(context.MountPath);
        if (!Directory.Exists(repositoryRoot)) {
            throw new ExecException($"Driver Store was not found at '{repositoryRoot}'.");
        }

        var changes = new List<DriverChange>();
        foreach (var infName in options.InfNames) {
            var matches = Directory.EnumerateDirectories(repositoryRoot, $"{infName}_*", SearchOption.TopDirectoryOnly)
                .ToList();
            if (matches.Count == 0) {
                changes.Add(new(new ChangeItem(ChangeKind.Skipped, infName, "not in FileRepository"), ""));
                continue;
            }

            changes.AddRange(matches.Select(directory => new DriverChange(
                new ChangeItem(ChangeKind.Removed,
                    Path.GetRelativePath(context.MountPath, directory), Before: infName),
                directory)));
        }

        return changes;
    }

    private static string RepositoryRoot(string mountPath) => Path.GetFullPath(Path.Combine(
        mountPath, "Windows", "System32", "DriverStore", "FileRepository"));

    private static void Delete(string directory) {
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
