using System.Text;
using TinyWin2.Core.Hashing;

namespace TinyWin2.Core.Tests;

public sealed class FingerprintingTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();

    [Test]
    public async Task FingerprintsAreVersionedAndStable() {
        var first = Fingerprinting.Compute("tinywin2");
        var second = Fingerprinting.Compute(Encoding.UTF8.GetBytes("tinywin2"));

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(first).StartsWith("xxh3-v1:");
        await Assert.That(Fingerprinting.IsCurrent(first)).IsTrue();
        await Assert.That(Fingerprinting.IsCurrent("ba7816bf")).IsFalse();
    }

    [Test]
    public async Task FileFingerprintReadsContentIncrementally() {
        var path = Path.Combine(_root, "asset.bin");
        await File.WriteAllTextAsync(path, "tinywin2");

        var first = await Fingerprinting.ComputeFileAsync(path, CancellationToken.None);
        await File.AppendAllTextAsync(path, "-changed");
        var second = await Fingerprinting.ComputeFileAsync(path, CancellationToken.None);

        await Assert.That(first).IsNotEqualTo(second);
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
