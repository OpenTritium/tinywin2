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
    private enum EntryKind {
        Missing,
        File,
        Directory,
    }

    private sealed record PathChange(
        ChangeItem Change,
        string AbsoluteTarget,
        string? AssetSource = null,
        EntryKind AssetKind = EntryKind.Missing);

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

        var (_, destination, assetSource, assetKind) = changes[0];
        var assetPath = assetSource!;
        if (GetEntryKind(destination) != assetKind) {
            if (GetEntryKind(destination) != EntryKind.Missing) {
                EnsureTreeHasNoReparsePoints(destination);
            }

            try {
                ImageFs.DeleteIfExists(destination);
            }
            catch (UnauthorizedAccessException) {
                await ImageFs.GrantDeleteAccessAsync(runner, destination, ct);
                ImageFs.DeleteIfExists(destination);
            }
        }

        if (assetKind == EntryKind.File) {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(assetPath, destination, overwrite: true);
        }
        else {
            var result = await runner.RunAsync("robocopy.exe",
                [
                    assetPath, destination, "/E", "/COPY:DAT", "/DCOPY:DAT", "/IS", "/XJ", "/R:1", "/W:1",
                    "/NFL", "/NDL", "/NJH", "/NJS"
                ],
                new ProcessRunOptions { IgnoreExitCode = true }, ct);
            if (result.ExitCode >= 8) {
                throw new IOException($"robocopy failed copying fs.path asset (exit {result.ExitCode}).");
            }
        }

        context.Log.Info($"copied asset '{changes[0].Change.After}' → image path '{changes[0].Change.Target}'");
        return ExecResult.Applied([.. changes.Select(c => c.Change)]);
    }

    private static List<PathChange> InspectCore(ExecContext context, ExecSpec spec) {
        var options = FsPathOptions.FromDesired(spec.Desired, spec.Ensure);
        if (spec.Ensure == Ensure.Absent) {
            var changes = new List<PathChange>();
            foreach (var relative in options.Paths) {
                var target = ResolveInsideMount(context.MountPath, relative);
                if (GetEntryKind(target) == EntryKind.Missing) {
                    continue;
                }

                EnsureTreeHasNoReparsePoints(target);
                changes.Add(new(new ChangeItem(ChangeKind.Removed, relative), target));
            }

            return changes;
        }

        var assetSource = ResolveAssetSource(context, options.Source!);
        var destination = ResolveInsideMount(context.MountPath, options.Path!);
        var assetKind = GetEntryKind(assetSource);
        var destinationKind = GetEntryKind(destination);
        var isSatisfied = destinationKind == assetKind && (assetKind switch {
            EntryKind.File => FilesEqual(assetSource, destination),
            EntryKind.Directory => DirectoryContainsAsset(assetSource, destination),
            _ => false,
        });
        return isSatisfied
            ? []
            : [
                new PathChange(
                    new ChangeItem(
                        destinationKind == EntryKind.Missing ? ChangeKind.Created : ChangeKind.Modified,
                        options.Path!,
                        After: options.Source),
                    destination, assetSource, assetKind)
            ];
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
        if (!target.StartsWith(mountRoot, StringComparison.OrdinalIgnoreCase)) {
            throw new ExecException($"path '{relativePath}' resolved outside the mounted image.");
        }

        EnsurePathChainHasNoReparsePoints(mountRoot, target);
        return target;
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

        EnsurePathChainHasNoReparsePoints(assetRoot, candidate);
        EnsureTreeHasNoReparsePoints(candidate);
        return candidate;
    }

    private static EntryKind GetEntryKind(string path) {
        if (File.Exists(path)) {
            return EntryKind.File;
        }

        return Directory.Exists(path) ? EntryKind.Directory : EntryKind.Missing;
    }

    private static bool FilesEqual(string source, string destination) {
        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        if (sourceInfo.Length != destinationInfo.Length) {
            return false;
        }

        using var sourceStream = File.OpenRead(source);
        using var destinationStream = File.OpenRead(destination);
        var sourceBuffer = new byte[64 * 1024];
        var destinationBuffer = new byte[sourceBuffer.Length];
        while (true) {
            var sourceRead = sourceStream.Read(sourceBuffer, 0, sourceBuffer.Length);
            var destinationRead = destinationStream.Read(destinationBuffer, 0, destinationBuffer.Length);
            if (sourceRead != destinationRead) {
                return false;
            }

            if (sourceRead == 0) {
                return true;
            }

            if (!sourceBuffer.AsSpan(0, sourceRead).SequenceEqual(destinationBuffer.AsSpan(0, destinationRead))) {
                return false;
            }
        }
    }

    private static bool DirectoryContainsAsset(string source, string destination) {
        foreach (var (sourcePath, isDirectory) in EnumerateTree(source)) {
            var relative = Path.GetRelativePath(source, sourcePath);
            var targetPath = Path.Combine(destination, relative);
            if (isDirectory) {
                EnsurePathChainHasNoReparsePoints(destination, targetPath);
                if (GetEntryKind(targetPath) != EntryKind.Directory) {
                    return false;
                }
            }
            else {
                EnsurePathChainHasNoReparsePoints(destination, targetPath);
                if (GetEntryKind(targetPath) != EntryKind.File || !FilesEqual(sourcePath, targetPath)) {
                    return false;
                }
            }
        }

        return true;
    }

    private static IEnumerable<(string Path, bool IsDirectory)> EnumerateTree(string root) {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current)) {
            foreach (var path in Directory.EnumerateFileSystemEntries(current)) {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) {
                    throw new ExecException($"reparse point is not allowed in fs.path tree: '{path}'.");
                }

                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                yield return (path, isDirectory);
                if (isDirectory) {
                    pending.Push(path);
                }
            }
        }
    }

    private static void EnsureTreeHasNoReparsePoints(string root) {
        var attributes = File.GetAttributes(root);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) {
            throw new ExecException($"reparse point is not allowed in fs.path tree: '{root}'.");
        }

        if (attributes.HasFlag(FileAttributes.Directory)) {
            _ = EnumerateTree(root).ToList();
        }
    }

    private static void EnsurePathChainHasNoReparsePoints(string root, string target) {
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) {
            throw new ExecException($"reparse point is not allowed in fs.path path: '{root}'.");
        }

        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries)) {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException) {
                break;
            }
            catch (DirectoryNotFoundException) {
                break;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint)) {
                throw new ExecException($"reparse point is not allowed in fs.path path: '{current}'.");
            }
        }
    }

    [GeneratedRegex(@"(^|\\)\.\.?(\\|$)")]
    private static partial Regex DotSegment();
}
