using System.IO.Hashing;
using System.Text;

namespace TinyWin2.Core.Hashing;

/// <summary>Fast XXH3 fingerprints for cache and resume decisions.</summary>
public static class Fingerprinting {
    public const string Algorithm = "xxh3";

    public static string Compute(string value) => Compute(Encoding.UTF8.GetBytes(value));

    public static string Compute(ReadOnlySpan<byte> data) =>
        Format(XxHash3.HashToUInt64(data));

    public static async Task<string> ComputeFileAsync(string path, CancellationToken ct) {
        await using var stream = File.OpenRead(path);
        var hasher = new XxHash3();
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0) {
            hasher.Append(buffer.AsSpan(0, read));
        }

        return Format(hasher.GetCurrentHashAsUInt64());
    }

    /// <summary>
    ///     Content hash of a file or a whole directory tree: directories hash a
    ///     canonicalized entry list (reparse points recorded, never followed), so any
    ///     content, name, or structural change moves the fingerprint. A missing path
    ///     hashes to the stable marker "missing".
    /// </summary>
    public static async Task<string> ComputeTreeAsync(string? path, CancellationToken ct) {
        if (path is null || (!File.Exists(path) && !Directory.Exists(path))) {
            return "missing";
        }

        if (File.Exists(path)) {
            return await ComputeFileAsync(path, ct);
        }

        var entries = new List<string>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var currentPath)) {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(currentPath)
                         .OrderBy(entryPath => entryPath, StringComparer.OrdinalIgnoreCase)) {
                var relative = Path.GetRelativePath(path, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    entries.Add("reparse:" + relative);
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0) {
                    entries.Add("directory:" + relative);
                    pending.Push(entry);
                }
                else {
                    var hash = await ComputeFileAsync(entry, ct);
                    entries.Add($"file:{relative}:{hash}");
                }
            }
        }

        var canonical = string.Join('\n', entries.OrderBy(entry => entry, StringComparer.Ordinal));
        return Compute(canonical);
    }

    private static string Format(ulong value) => value.ToString("x16");
}
