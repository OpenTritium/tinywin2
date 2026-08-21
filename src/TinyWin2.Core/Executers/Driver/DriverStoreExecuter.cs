using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Driver;

/// <summary>
/// Converges inbox driver-store packages. absent = delete FileRepository directories
/// named after the declared INF allowlist. Present direction is reserved for /Add-Driver.
/// Desired: <c>{ infNames:["mdm.inf"] }</c>.
/// </summary>
public sealed class DriverStoreExecuter(IProcessRunner runner) : IExecuter {
    private const string ResourceId = "driver.store";
    public string Resource => ResourceId;

    /// <summary>One structured difference carrying its resolved directory — apply never re-derives paths.</summary>
    private sealed record DriverChange(ChangeItem Change, string Directory);

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        ValidateSpec(spec);
        var changes = InspectCore(context, spec);
        return Task.FromResult(new ResourceDiff(changes.All(c => c.Change.Kind == ChangeKind.Skipped),
            [.. changes.Select(c => c.Change)]));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        ValidateSpec(spec);
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
                ImageFs.DeleteIfExists(directory);
            }
            catch (UnauthorizedAccessException) {
                await ImageFs.GrantDeleteAccessAsync(runner, directory, ct);
                ImageFs.DeleteIfExists(directory);
            }
        }

        return ExecResult.Applied([.. changes.Select(c => c.Change)]);
    }

    private static void ValidateSpec(ExecSpec spec) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException("driver.store present (driver integration) is not implemented yet.");
        }
    }

    private static List<DriverChange> InspectCore(ExecContext context, ExecSpec spec) {
        var options = DriverStoreOptions.FromDesired(spec.Desired);
        var repositoryRoot = RepositoryRoot(context.MountPath);
        if (!Directory.Exists(repositoryRoot)) {
            throw new ExecException($"Driver Store was not found at '{repositoryRoot}'.");
        }

        var changes = new List<DriverChange>();
        foreach (var infName in options.InfNames) {
            var prefix = $"{infName}_";
            var matches = Directory.EnumerateDirectories(repositoryRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(directory => Path.GetFileName(directory)
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) {
                changes.Add(new(new(ChangeKind.Skipped, infName, "not in FileRepository"), ""));
                continue;
            }

            changes.AddRange(matches.Select(directory => new DriverChange(
                new(ChangeKind.Removed,
                    Path.GetRelativePath(context.MountPath, directory), Before: infName),
                directory)));
        }

        return changes;
    }

    private static string RepositoryRoot(string mountPath) => Path.GetFullPath(Path.Combine(
        mountPath, "Windows", "System32", "DriverStore", "FileRepository"));
}
