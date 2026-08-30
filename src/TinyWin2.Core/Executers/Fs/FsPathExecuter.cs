using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Fs;

/// <summary>
///     Converges image paths. absent = delete paths inside the mount; present = copy a plan
///     asset (file or directory) into the image.
/// </summary>
public sealed partial class FsPathExecuter(IProcessRunner runner) : IExecuter {
    private const string ResourceId = "fs.path";
    public string Resource => ResourceId;

    public void Validate(OperationSpec spec) {
        if (spec.Action is not (OperationAction.Remove or OperationAction.Copy)) {
            throw new ExecException($"{ResourceId} supports actions 'remove' and 'copy'.");
        }

        _ = FsPathOptions.FromDesired(spec.Spec, spec.Action);
    }

    public Task<ResourceDiff> InspectAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = InspectCore(context, spec);
        return Task.FromResult(new ResourceDiff(changes.Count == 0, [.. changes.Select(c => c.Change)]));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = InspectCore(context, spec);
        if (changes.Count == 0) {
            return ExecResult.Skipped("paths already in the desired state");
        }

        if (spec.Action == OperationAction.Remove) {
            foreach (var change in changes) {
                context.Log.Info($"removing image path: {change.Change.Target}");
                await ImageFs.DeleteWithRescueAsync(runner, change.AbsoluteTarget, ct);
            }

            return ExecResult.Applied([.. changes.Select(c => c.Change)]);
        }

        var (_, destination, assetSource, assetKind) = changes[0];
        var assetPath = assetSource!;
        if (GetEntryKind(destination) != assetKind) {
            if (GetEntryKind(destination) != EntryKind.Missing) {
                EnsureTreeHasNoReparsePoints(destination);
            }

            await ImageFs.DeleteWithRescueAsync(runner, destination, ct);
        }

        if (assetKind == EntryKind.File) {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(assetPath, destination, true);
        }
        else {
            var result = await runner.RunAsync("robocopy.exe",
                [
                    assetPath, destination, "/E", "/COPY:DAT", "/DCOPY:DAT", "/IS", "/XJ", "/R:1", "/W:1",
                    "/NFL", "/NDL", "/NJH", "/NJS"
                ],
                new() { IgnoreExitCode = true }, ct);
            if (result.ExitCode >= 8) {
                throw new IOException($"robocopy failed copying fs.path asset (exit {result.ExitCode}).");
            }
        }

        context.Log.Info($"copied asset '{changes[0].Change.After}' → image path '{changes[0].Change.Target}'");
        return ExecResult.Applied([.. changes.Select(c => c.Change)]);
    }

    private static List<PathChange> InspectCore(ExecContext context, OperationSpec spec) {
        var options = FsPathOptions.FromDesired(spec.Spec, spec.Action);
        if (spec.Action == OperationAction.Remove) {
            var changes = new List<PathChange>();
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in options.Paths) {
                foreach (var target in ResolveRemoveTargets(context.MountPath, relative)) {
                    if (!seenTargets.Add(target)) {
                        continue;
                    }

                    // A whole-directory removal must work for protected trees such as
                    // Defender ATP, whose child ACLs may deny enumeration. The path
                    // chain and target itself are still checked; the delete fallback
                    // takes ownership if the recursive delete needs it.
                    EnsureRemovalTargetSafe(target);
                    var actualRelative = Path.GetRelativePath(context.MountPath, target)
                        .Replace(Path.DirectorySeparatorChar, '\\');
                    changes.Add(new(new(ChangeKind.Removed, actualRelative), target));
                }
            }

            return changes;
        }

        var assetSource = ResolveAssetSource(context, options.Source!);
        var destination = ResolveInsideMount(context.MountPath, options.Path!);
        var assetKind = GetEntryKind(assetSource);
        var destinationKind = GetEntryKind(destination);
        var isSatisfied = destinationKind == assetKind && assetKind switch {
            EntryKind.File => FilesEqual(assetSource, destination),
            EntryKind.Directory => DirectoryContainsAsset(assetSource, destination),
            _ => false
        };
        return isSatisfied
            ? []
            : [
                new(
                    new(
                        destinationKind == EntryKind.Missing ? ChangeKind.Created : ChangeKind.Modified,
                        options.Path!,
                        After: options.Source),
                    destination, assetSource, assetKind)
            ];
    }

    /// <summary>Rejects rooted paths, .. traversal, and anything escaping the mount root.</summary>
    internal static string ResolveInsideMount(string mountPath, string relativePath) {
        var segments = ValidateRelativeSegments(relativePath, allowWildcards: false);
        var normalized = string.Join('\\', segments);

        var mountRoot = GetMountRoot(mountPath);
        var target = SafePath.TryResolveInside(mountRoot, normalized)
                     ?? throw new ExecException($"path '{relativePath}' resolved outside the mounted image.");

        EnsurePathChainHasNoReparsePoints(mountRoot, target);
        return target;
    }

    /// <summary>
    ///     Expands a remove path one segment at a time. A wildcard can only match
    ///     direct children of the current directory, and reparse directories are
    ///     never used as intermediate traversal points.
    /// </summary>
    private static IEnumerable<string> ResolveRemoveTargets(string mountPath, string relativePath) {
        var segments = ValidateRelativeSegments(relativePath, allowWildcards: true);
        var mountRoot = GetMountRoot(mountPath);
        var candidates = new List<string> { mountRoot };

        foreach (var (segment, isLast) in segments.Select((value, index) =>
                     (value, index == segments.Length - 1))) {
            var next = new List<string>();
            foreach (var current in candidates) {
                if (GetEntryKind(current) != EntryKind.Directory) {
                    continue;
                }

                var currentAttributes = File.GetAttributes(current);
                if (currentAttributes.HasFlag(FileAttributes.ReparsePoint)) {
                    throw new ExecException($"reparse point is not allowed in fs.path path: '{current}'.");
                }

                foreach (var child in Directory.EnumerateFileSystemEntries(current)) {
                    var name = Path.GetFileName(child);
                    if (!LikePattern.IsMatch(segment, name)) {
                        continue;
                    }

                    var attributes = File.GetAttributes(child);
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    if (!isLast) {
                        if (!isDirectory) {
                            continue;
                        }

                        if (attributes.HasFlag(FileAttributes.ReparsePoint)) {
                            // Junctions such as Users\All Users are intentionally not
                            // followed. They are outside the wildcard traversal scope.
                            continue;
                        }
                    }

                    next.Add(child);
                }
            }

            candidates = next;
            if (candidates.Count == 0) {
                yield break;
            }
        }

        foreach (var candidate in candidates) {
            yield return candidate;
        }
    }

    private static string GetMountRoot(string mountPath) =>
        Path.GetFullPath(mountPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string[] ValidateRelativeSegments(string relativePath, bool allowWildcards) {
        var normalized = relativePath.Replace('/', '\\');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(relativePath)
            || normalized.StartsWith('\\')
            || DotSegment().IsMatch(normalized)) {
            throw new ExecException($"unsafe relative path '{relativePath}'.");
        }

        var segments = normalized.Split('\\');
        if (segments.Any(string.IsNullOrEmpty)
            || segments.Any(segment => segment.Contains(':'))
            || (!allowWildcards && segments.Any(ContainsWildcard))) {
            throw new ExecException($"unsafe relative path '{relativePath}'.");
        }

        foreach (var character in normalized) {
            if (char.IsControl(character) || character is '"' or '<' or '>' or '|') {
                throw new ExecException($"unsafe relative path '{relativePath}'.");
            }
        }

        return segments;
    }

    private static bool ContainsWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

    private static string ResolveAssetSource(ExecContext context, string source) {
        if (context.PlanAssetsRoot is null) {
            throw new ExecException("fs.path present requires a plan assets root, which this build did not provide.");
        }

        var candidate = SafePath.TryResolveInside(context.PlanAssetsRoot, source)
                        ?? throw new ExecException(
                            $"asset source '{source}' resolved outside the plan assets directory.");

        if (!File.Exists(candidate) && !Directory.Exists(candidate)) {
            throw new ExecException($"asset source '{source}' was not found under '{context.PlanAssetsRoot}'.");
        }

        EnsurePathChainHasNoReparsePoints(context.PlanAssetsRoot, candidate);
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
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                // WIM-mounted Windows files commonly use WOF reparse metadata. A
                // reparse file is still a safe leaf to delete; reparse directories
                // remain forbidden because traversing them could escape the image.
                if (attributes.HasFlag(FileAttributes.ReparsePoint) && isDirectory) {
                    throw new ExecException($"reparse point is not allowed in fs.path tree: '{path}'.");
                }

                yield return (path, isDirectory);
                if (isDirectory) {
                    pending.Push(path);
                }
            }
        }
    }

    private static void EnsureTreeHasNoReparsePoints(string root) {
        var attributes = File.GetAttributes(root);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)
            && attributes.HasFlag(FileAttributes.Directory)) {
            throw new ExecException($"reparse point is not allowed in fs.path tree: '{root}'.");
        }

        if (attributes.HasFlag(FileAttributes.Directory)) {
            _ = EnumerateTree(root).ToList();
        }
    }

    private static void EnsureRemovalTargetSafe(string target) {
        var attributes = File.GetAttributes(target);
        if (attributes.HasFlag(FileAttributes.ReparsePoint)
            && attributes.HasFlag(FileAttributes.Directory)) {
            throw new ExecException($"reparse point is not allowed in fs.path removal target: '{target}'.");
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

            var isTarget = string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)
                && (!isTarget || attributes.HasFlag(FileAttributes.Directory))) {
                throw new ExecException($"reparse point is not allowed in fs.path path: '{current}'.");
            }
        }
    }

    [GeneratedRegex(@"(^|\\)\.\.?(\\|$)")]
    private static partial Regex DotSegment();

    /// <summary>
    ///     One structured difference carrying its resolved targets — apply never re-parses
    ///     options, re-validates paths, or re-resolves the asset source.
    /// </summary>
    private enum EntryKind {
        Missing,
        File,
        Directory
    }

    private sealed record PathChange(
        ChangeItem Change,
        string AbsoluteTarget,
        string? AssetSource = null,
        EntryKind AssetKind = EntryKind.Missing);
}
