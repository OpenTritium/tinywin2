using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Fs;

/// <summary>
/// Converges paths inside the image. absent = delete (path-traversal guarded);
/// present = copy files/directories from the plan's assets directory into the image.
/// Desired absent: <c>{ paths:[...] }</c>; present: <c>{ path, source }</c>.
/// </summary>
public sealed partial class FsPathExecuter(IProcessRunner runner) : IExecuter
{
    public const string ResourceId = "fs.path";
    public string Resource => ResourceId;

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct)
    {
        var differences = new List<ChangeItem>();
        if (spec.Ensure == Ensure.Absent)
        {
            foreach (var relative in ParseAbsentPaths(spec))
            {
                var target = ResolveInsideMount(context.MountPath, relative);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    differences.Add(new ChangeItem(ChangeKind.Removed, relative));
                }
            }
        }
        else
        {
            var (relative, source) = ParsePresent(spec);
            _ = ResolveAssetSource(context, source);
            var target = ResolveInsideMount(context.MountPath, relative);
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                differences.Add(new ChangeItem(ChangeKind.Created, relative, After: source));
            }
        }
        return Task.FromResult(new ResourceDiff(differences.Count == 0, differences));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct)
    {
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied)
        {
            return ExecResult.Skipped("paths already in the desired state");
        }

        if (spec.Ensure == Ensure.Absent)
        {
            var applied = new List<ChangeItem>();
            foreach (var change in diff.Differences)
            {
                var target = ResolveInsideMount(context.MountPath, change.Target);
                context.Log.Info($"removing image path: {change.Target}");
                try
                {
                    Delete(target);
                }
                catch (UnauthorizedAccessException)
                {
                    await runner.RunAsync("takeown.exe", ["/F", target, "/A", "/R", "/D", "Y"], cancellationToken: ct);
                    await runner.RunAsync("icacls.exe", [target, "/grant", "*S-1-5-32-544:F", "/T"], cancellationToken: ct);
                    Delete(target);
                }
                applied.Add(change);
            }
            return ExecResult.Applied(applied);
        }

        var (relative, source) = ParsePresent(spec);
        var assetPath = ResolveAssetSource(context, source);
        var destination = ResolveInsideMount(context.MountPath, relative);
        if (File.Exists(assetPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(assetPath, destination, overwrite: true);
        }
        else
        {
            foreach (var file in Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories))
            {
                var relativeToAsset = Path.GetRelativePath(assetPath, file);
                var targetFile = Path.Combine(destination, relativeToAsset);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(file, targetFile, overwrite: true);
            }
        }
        context.Log.Info($"copied asset '{source}' → image path '{relative}'");
        return ExecResult.Applied(diff.Differences);
    }

    private static void Delete(string target)
    {
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    /// <summary>Rejects rooted paths, .. traversal, and anything escaping the mount root (v1 rules).</summary>
    internal static string ResolveInsideMount(string mountPath, string relativePath)
    {
        var normalized = relativePath.Replace('/', '\\').TrimStart('\\');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(relativePath)
            || DotSegment().IsMatch(normalized))
        {
            throw new ExecException($"unsafe relative path '{relativePath}'.");
        }
        var mountRoot = Path.GetFullPath(mountPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(mountRoot, normalized));
        if (!target.StartsWith(mountRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExecException($"path '{relativePath}' resolved outside the mounted image.");
        }
        return target;
    }

    private static string ResolveAssetSource(ExecContext context, string source)
    {
        if (context.PlanAssetsRoot is null)
        {
            throw new ExecException("fs.path present requires a plan assets root, which this build did not provide.");
        }
        var assetRoot = Path.GetFullPath(context.PlanAssetsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(assetRoot, source.Replace('/', '\\')));
        if (!candidate.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExecException($"asset source '{source}' resolved outside the plan assets directory.");
        }
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw new ExecException($"asset source '{source}' was not found under '{context.PlanAssetsRoot}'.");
        }
        return candidate;
    }

    private static List<string> ParseAbsentPaths(ExecSpec spec)
    {
        var paths = spec.Desired["paths"]?.AsArray().OfType<JsonValue>().Select(v => v.GetValue<string>()).ToList()
                   ?? throw new ExecException("fs.path absent requires 'paths'.");
        if (paths.Count == 0)
        {
            throw new ExecException("fs.path absent requires at least one path.");
        }
        return paths;
    }

    private static (string Path, string Source) ParsePresent(ExecSpec spec)
    {
        var path = spec.Desired["path"]?.GetValue<string>()
                   ?? throw new ExecException("fs.path present requires 'path'.");
        var source = spec.Desired["source"]?.GetValue<string>()
                     ?? throw new ExecException("fs.path present requires 'source' (relative to the plan assets directory).");
        return (path, source);
    }

    [GeneratedRegex(@"(^|\\)\.\.?(\\|$)")]
    private static partial Regex DotSegment();
}
