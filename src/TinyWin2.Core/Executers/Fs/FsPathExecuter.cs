using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Fs;

/// <summary>
/// Converges paths inside the image. absent = delete (path-traversal guarded);
/// present = copy files/directories from the plan's assets directory into the image.
/// Desired absent: <c>{ paths:[...] }</c>; present: <c>{ path, source }</c>.
/// </summary>
public sealed partial class FsPathExecuter(IProcessRunner runner) : IExecuter {
    public const string ResourceId = "fs.path";
    public string Resource => ResourceId;

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var differences = new List<ChangeItem>();
        var options = FsPathOptions.FromDesired(spec.Desired, spec.Ensure);
        if (spec.Ensure == Ensure.Absent) {
            differences.AddRange(from relative in options.Paths
                                 let target = ResolveInsideMount(context.MountPath, relative)
                                 where File.Exists(target) || Directory.Exists(target)
                                 select new ChangeItem(ChangeKind.Removed, relative));
        }
        else {
            _ = ResolveAssetSource(context, options.Source!);
            var target = ResolveInsideMount(context.MountPath, options.Path!);
            if (!File.Exists(target) && !Directory.Exists(target)) {
                differences.Add(new ChangeItem(ChangeKind.Created, options.Path!, After: options.Source));
            }
        }

        return Task.FromResult(new ResourceDiff(differences.Count == 0, differences));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped("paths already in the desired state");
        }

        var options = FsPathOptions.FromDesired(spec.Desired, spec.Ensure);
        if (spec.Ensure == Ensure.Absent) {
            var applied = new List<ChangeItem>();
            foreach (var change in diff.Differences) {
                var target = ResolveInsideMount(context.MountPath, change.Target);
                context.Log.Info($"removing image path: {change.Target}");
                try {
                    Delete(target);
                }
                catch (UnauthorizedAccessException) {
                    await runner.RunAsync("takeown.exe", ["/F", target, "/A", "/R", "/D", "Y"], cancellationToken: ct);
                    await runner.RunAsync("icacls.exe", [target, "/grant", "*S-1-5-32-544:F", "/T"],
                        cancellationToken: ct);
                    Delete(target);
                }

                applied.Add(change);
            }

            return ExecResult.Applied(applied);
        }

        var assetPath = ResolveAssetSource(context, options.Source!);
        var destination = ResolveInsideMount(context.MountPath, options.Path!);
        if (File.Exists(assetPath)) {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(assetPath, destination, overwrite: true);
        }
        else {
            foreach (var file in Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories)) {
                var relativeToAsset = Path.GetRelativePath(assetPath, file);
                var targetFile = Path.Combine(destination, relativeToAsset);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(file, targetFile, overwrite: true);
            }
        }

        context.Log.Info($"copied asset '{options.Source}' → image path '{options.Path}'");
        return ExecResult.Applied(diff.Differences);
    }

    private static void Delete(string target) {
        if (Directory.Exists(target)) {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target)) {
            File.Delete(target);
        }
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
