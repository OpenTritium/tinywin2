using System.IO.Hashing;
using System.Text;

namespace TinyWin2.Core.Hashing;

/// <summary>Fast, versioned fingerprints for cache and resume decisions.</summary>
public static class Fingerprinting {
    public const string Algorithm = "xxh3-v1";

    public static bool IsCurrent(string? fingerprint) =>
        fingerprint?.StartsWith(Algorithm + ":", StringComparison.Ordinal) == true;

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

    private static string Format(ulong value) => Algorithm + ":" + value.ToString("x16");
}
