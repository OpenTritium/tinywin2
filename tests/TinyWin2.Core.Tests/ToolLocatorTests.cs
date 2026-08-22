using TinyWin2.Core.Native;

namespace TinyWin2.Core.Tests;

[NotInParallel] // every instance mutates the process-wide PATH - instances must not interleave
public sealed class ToolLocatorTests : IDisposable {
    private readonly string _toolDir = TestPlans.CreateTempDirectory();
    private readonly string _originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";

    public ToolLocatorTests() {
        File.WriteAllText(Path.Combine(_toolDir, "tinywin2-fake-tool.exe"), "stub");
        Environment.SetEnvironmentVariable("PATH", _originalPath);
    }

    [Test]
    public async Task FindsToolOnPathAndReturnsAbsolutePath() {
        Environment.SetEnvironmentVariable("PATH", _toolDir);
        var found = ToolLocator.Locate("tinywin2-fake-tool.exe");
        await Assert.That(found).IsEqualTo(Path.Combine(_toolDir, "tinywin2-fake-tool.exe"));
        await Assert.That(Path.IsPathRooted(found!)).IsTrue();
    }

    [Test]
    public async Task ReturnsNullWhenMissing() {
        Environment.SetEnvironmentVariable("PATH", _toolDir);
        await Assert.That(ToolLocator.Locate("no-such-tool.exe")).IsNull();
    }

    [Test]
    public async Task ReturnsNullForBlankInput() {
        await Assert.That(ToolLocator.Locate("")).IsNull();
        await Assert.That(ToolLocator.Locate("   ")).IsNull();
    }

    [Test]
    public async Task AcceptsAbsoluteFileName() {
        var full = Path.Combine(_toolDir, "tinywin2-fake-tool.exe");
        await Assert.That(ToolLocator.Locate(full)).IsEqualTo(full);
        await Assert.That(ToolLocator.Locate(Path.Combine(_toolDir, "missing.exe"))).IsNull();
    }

    [Test]
    public async Task ToleratesQuotedAndEmptyPathEntries() {
        var quoted = $"\"{_toolDir}\"";
        Environment.SetEnvironmentVariable("PATH", $";;;{quoted};;");
        var found = ToolLocator.Locate("tinywin2-fake-tool.exe");
        await Assert.That(found).IsEqualTo(Path.Combine(_toolDir, "tinywin2-fake-tool.exe"));
    }

    public void Dispose() {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try { Directory.Delete(_toolDir, recursive: true); } catch { /* best effort */ }
    }
}
