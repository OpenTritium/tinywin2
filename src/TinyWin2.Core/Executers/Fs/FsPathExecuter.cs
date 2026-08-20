using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Fs;

/// <summary>
/// Converges image paths. absent = delete paths inside the mount; present = copy a plan
/// asset (file or directory) into the image.
/// </summary>
public sealed partial class FsPathExecuter(IProcessRunner runner) : IExecuter {
    private const string ResourceId = "fs.path";
    public string Resource => ResourceId;

    /// <summary>
    /// One structured difference carrying its resolved targets — apply never re-parses
    /// options, re-validates paths, or re-resolves the asset source.
    /// </summary>
    private sealed record PathChange(ChangeItem Change, string AbsoluteTarget, string? AssetSource = null);

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var changes = InspectCore(context, spec);
        return Task.FromResult(new ResourceDiff(changes.Count == 0, [.. changes.Select(c => c.Change)]));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var changes = InspectCore(context, spec);
        if (changes.Count == 0) {
            return ExecResult.Skipped("paths already in the desired state");
        }

        if (spec.Ensure == Ensure.Absent) {
            foreach (var change in changes) {
                context.Log.Info($"removing image path: {change.Change.Target}");
                try {
                    ImageFs.DeleteIfExists(change.AbsoluteTarget);
                }
                catch (UnauthorizedAccessException) {
                    await ImageFs.GrantDeleteAccessAsync(runner, change.AbsoluteTarget, ct);
                    ImageFs.DeleteIfExists(change.AbsoluteTarget);
                }
            }

            return ExecResult.Applied([.. changes.Select(c => c.Change)]);
        }

        var (_, destination, assetSource) = changes[0];
        var assetPath = assetSource!;
        if (File.Exists(assetPath)) {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(assetPath, destination, overwrite: true);
        }
        else {
            foreach (var file in Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories)) {
                var targetFile = Path.Combine(destination, Path.GetRelativePath(assetPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(file, targetFile, overwrite: true);
            }
        }

        context.Log.Info($"copied asset '{changes[0].Change.After}' → image path '{changes[0].Change.Target}'");
        return ExecResult.Applied([.. changes.Select(c => c.Change)]);
    }

    private static List<PathChange> InspectCore(ExecContext context, ExecSpec spec) {
        var options = FsPathOptions.FromDesired(spec.Desired, spec.Ensure);
        if (spec.Ensure == Ensure.Absent) {
            return
            [
                .. from relative in options.Paths
                   let target = ResolveInsideMount(context.MountPath, relative)
                   where File.Exists(target) || Directory.Exists(target)
                   select new PathChange(new ChangeItem(ChangeKind.Removed, relative), target),
            ];
        }

        var assetSource = ResolveAssetSource(context, options.Source!);
        var destination = ResolveInsideMount(context.MountPath, options.Path!);
        return File.Exists(destination) || Directory.Exists(destination)
            ? []
            : [new PathChange(new ChangeItem(ChangeKind.Created, options.Path!, After: options.Source),
                destination, assetSource)];
    }

    /// <summary>Rejects rooted paths, .. traversal, and anything escaping the mount root (v1 rules).</summary>
    internal static string ResolveInsideMount(string mountPath, string relativePath) {
        var normalized = relativePath.Replace('/', '\\').TrimStart('\\');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(relativePath)
            || DotSegment().IsMatch(normalized)) {
            throw new ExecException($"unsafe relative path '{relativePath}'.");
        }

        var mountRoot = Path.GetFullPath(mountPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(mountRoot, normalized));
        return !target.StartsWith(mountRoot, StringComparison.OrdinalIgnoreCase)
            ? throw new ExecException($"path '{relativePath}' resolved outside the mounted image.")
            : target;
    }

    private static string ResolveAssetSource(ExecContext context, string source) {
        if (context.PlanAssetsRoot is null) {
            throw new ExecException("fs.path present requires a plan assets root, which this build did not provide.");
        }

        var assetRoot = Path.GetFullPath(context.PlanAssetsRoot).TrimEnd(Path.DirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(assetRoot, source.Replace('/', '\\')));
        if (!candidate.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase)) {
            throw new ExecException($"asset source '{source}' resolved outside the plan assets directory.");
        }

        if (!File.Exists(candidate) && !Directory.Exists(candidate)) {
            throw new ExecException($"asset source '{source}' was not found under '{context.PlanAssetsRoot}'.");
        }

        return candidate;
    }

    [GeneratedRegex(@"(^|\\)\.\.?(\\|$)")]
    private static partial Regex DotSegment();
}
