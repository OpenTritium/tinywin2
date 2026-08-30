using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>
///     Removes inbox SystemApps that are registered in the offline AppX store but are
///     not exposed as provisioned packages by DISM.
/// </summary>
public sealed class AppxSystemExecuter(IProcessRunner runner) : IExecuter {
    private const string ResourceId = "appx.system";
    private const string SystemAppsRelativePath = @"Windows\SystemApps";

    private const string AppxStoreRelativePath =
        @"Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore";

    public string Resource => ResourceId;

    public void Validate(OperationSpec spec) {
        if (spec.Action != OperationAction.Remove) {
            throw new ExecException($"{ResourceId} supports only action 'remove'.");
        }

        _ = SystemAppOptions.FromDesired(spec.Spec);
    }

    public async Task<ResourceDiff> InspectAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = await InspectCoreAsync(context, spec, ct);
        return new(changes.Count == 0, [.. changes.Select(c => c.Change)]);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = await InspectCoreAsync(context, spec, ct);
        if (changes.Count == 0) {
            return ExecResult.Skipped("no matching inbox SystemApps or AppX registrations");
        }

        // Remove registrations first so the package cannot be resolved while its files
        // are being removed. The build engine rolls back the layer if file deletion fails.
        foreach (var change in changes.Where(c => c.RegistryKey is not null)) {
            await OfflineReg.DeleteKeyAsync(runner, change.RegistryKey!, change.RegistryKey!, ct);
            context.Log.Info($"deleted inbox AppX registration '{change.Change.Target}'");
        }

        foreach (var change in changes.Where(c => c.FilePath is not null)) {
            await ImageFs.DeleteWithRescueAsync(runner, change.FilePath!, ct);
            context.Log.Info($"deleted SystemApps directory '{change.Change.Target}'");
        }

        return ExecResult.Applied([.. changes.Select(c => c.Change)]);
    }

    private async Task<List<SystemAppChange>> InspectCoreAsync(
        ExecContext context, OperationSpec spec, CancellationToken ct) {
        var options = SystemAppOptions.FromDesired(spec.Spec);
        var changes = new List<SystemAppChange>();
        var systemAppsRoot = Path.GetFullPath(Path.Combine(context.MountPath, SystemAppsRelativePath));
        if (Directory.Exists(systemAppsRoot)) {
            foreach (var directory in Directory.EnumerateDirectories(systemAppsRoot)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if (!options.Patterns.Any(pattern => LikePattern.IsMatch(pattern, name))) {
                    continue;
                }

                EnsureSystemAppPath(systemAppsRoot, directory);
                changes.Add(new(
                    new(ChangeKind.Removed,
                        Path.Combine(SystemAppsRelativePath, name).Replace(Path.DirectorySeparatorChar, '\\'),
                        "directory present"),
                    FilePath: directory));
            }
        }

        var hive = await context.Hives.GetAsync("software", context.Log, ct);
        var appxRoot = $"{hive.HiveKey}\\{AppxStoreRelativePath}";
        var result = await OfflineReg.QueryAsync(runner, appxRoot, ct, "/s");

        var acceptedRoots = new[] {
            $"{appxRoot}\\Config",
            $"{appxRoot}\\InboxApplications"
        };
        changes.AddRange(from key in RegQuery.KeysUnder(result.Output, hive.HiveKey)
                         where acceptedRoots.Any(root => IsDirectChild(root, key))
                         let name = key[(key.LastIndexOf('\\') + 1)..]
                         where options.Patterns.Any(pattern => LikePattern.IsMatch(pattern, name))
                         select new SystemAppChange(
                             new(ChangeKind.Removed, $"software:{key[hive.HiveKey.Length..].TrimStart('\\')}",
                                 "registry key present"), RegistryKey: key));

        return [
            .. changes
                .GroupBy(change => change.Change.Target, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
        ];
    }

    private static bool IsDirectChild(string root, string candidate) =>
        candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
        && !candidate[(root.Length + 1)..].Contains('\\');

    private static void EnsureSystemAppPath(string root, string candidate) {
        var inside = SafePath.TryResolveInside(root, candidate);
        if (inside is null
            || File.GetAttributes(inside).HasFlag(FileAttributes.ReparsePoint)) {
            throw new ExecException($"unsafe SystemApps removal path '{candidate}'.");
        }
    }

    private sealed record SystemAppChange(
        ChangeItem Change,
        string? FilePath = null,
        string? RegistryKey = null);
}
