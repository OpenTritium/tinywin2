using TinyWin2.Core.Hashing;

namespace TinyWin2.Core.Tests;

public sealed class FingerprintingTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();

    [Test]
    public async Task FingerprintsAreStable() {
        var first = Fingerprinting.Compute("tinywin2");
        var second = Fingerprinting.Compute("tinywin2"u8);

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(first).Length().IsEqualTo(16);
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
