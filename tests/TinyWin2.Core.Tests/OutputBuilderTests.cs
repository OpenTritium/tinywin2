using TinyWin2.Core.Hashing;
using TinyWin2.Core.Pipeline;

namespace TinyWin2.Core.Tests;

public sealed class OutputBuilderTests : IDisposable {
    private readonly OutputBuilder _builder;
    private readonly string _root = TestPlans.CreateTempDirectory();
    private readonly FakeProcessRunner _runner = new();

    public OutputBuilderTests() {
        _builder = new(_runner, new());
    }

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        }
        catch {
            /* best effort */
        }
    }

    [Test]
    public async Task CaptureBuildsDismArgumentsPerFormatAndSpeed() {
        await _builder.CaptureAsync("M:\\", "out.wim", "name", "desc", OutputFormat.Wim, true,
            CancellationToken.None);
        var args = string.Join(' ', _runner.ArgsOf(0));
        await Assert.That(args).Contains("/Capture-Image");
        await Assert.That(args).Contains("/CaptureDir:M:\\");
        await Assert.That(args).Contains("/Name:name");
        await Assert.That(args).Contains("/Description:desc");
        await Assert.That(args).Contains("/Compress:fast");
        await Assert.That(args.Contains("/Verify")).IsFalse();

        await _builder.CaptureAsync("M:\\", "out.esd", "name", null, OutputFormat.Esd, false,
            CancellationToken.None);
        var esdArgs = string.Join(' ', _runner.ArgsOf(1));
        await Assert.That(esdArgs).Contains("/Compress:recovery");
        await Assert.That(esdArgs).Contains("/Verify");
        await Assert.That(esdArgs.Contains("/Description")).IsFalse();
    }

    [Test]
    public async Task UncompressedStagingCaptureSkipsCompressionAndVerify() {
        await _builder.CaptureAsync("M:\\", "staging.wim", "name", null, "none", false,
            CancellationToken.None);
        var args = string.Join(' ', _runner.ArgsOf(0));
        await Assert.That(args).Contains("/Compress:none");
        await Assert.That(args.Contains("/Verify")).IsFalse();
    }

    [Test]
    public void VhdxCannotBeCapturedAsAnInstallImage() {
        Assert.Throws<ArgumentException>(() => _builder.CaptureAsync(
            "M:\\", "out.vhdx", "name", null, OutputFormat.Vhdx, true, CancellationToken.None));
    }

    [Test]
    public async Task VhdxCannotRebuildInstallationMedia() {
        var ex = Assert.Throws<ArgumentException>(() => _builder.RebuildMediaAsync(
                _root, Path.Combine(_root, "out"), "captured.vhdx", OutputFormat.Vhdx, CancellationToken.None)
            .GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("VHDX");
        await Assert.That(_runner.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RebuildMediaToleratesRobocopySuccessCodesOnlyBelowEight() {
        var source = CreateMediaSource();
        var captured = Path.Combine(_root, "captured.wim");
        await File.WriteAllTextAsync(captured, "payload");
        _runner.Handler = (_, _) => FakeProcessRunner.Fail(1);
        await _builder.RebuildMediaAsync(source, Path.Combine(_root, "out"), captured, OutputFormat.Wim,
            CancellationToken.None);

        _runner.Handler = (_, _) => FakeProcessRunner.Fail(8);
        var ex = Assert.Throws<IOException>(() => _builder.RebuildMediaAsync(
                source, Path.Combine(_root, "out2"), "captured.wim", OutputFormat.Wim, CancellationToken.None)
            .GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("robocopy failed");
    }

    [Test]
    public async Task RebuildMediaReplacesStaleInstallImages() {
        var source = CreateMediaSource();
        var outDir = Path.Combine(_root, "out");
        var sourcesDir = Path.Combine(outDir, "sources");
        Directory.CreateDirectory(sourcesDir);
        await File.WriteAllTextAsync(Path.Combine(sourcesDir, "install.esd"), "stale");
        await File.WriteAllTextAsync(Path.Combine(sourcesDir, "install.staging.wim"), "stale");
        var captured = Path.Combine(_root, "captured.wim");
        await File.WriteAllTextAsync(captured, "payload");

        var finalPath = await _builder.RebuildMediaAsync(source, outDir, captured, OutputFormat.Wim,
            CancellationToken.None);

        await Assert.That(finalPath).IsEqualTo(Path.Combine(sourcesDir, "install.wim"));
        await Assert.That(File.Exists(finalPath)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(finalPath)).IsEqualTo("payload");
        await Assert.That(File.Exists(Path.Combine(sourcesDir, "install.esd"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(sourcesDir, "install.staging.wim"))).IsFalse();
        await Assert.That(File.Exists(captured)).IsFalse();
    }

    [Test]
    public async Task CreateIsoRejectsMediaWithoutBootFiles() {
        var media = Path.Combine(_root, "media");
        Directory.CreateDirectory(media);
        var ex = Assert.Throws<FileNotFoundException>(() => _builder.CreateIsoAsync(
            media, Path.Combine(_root, "x.iso"), "oscdimg.exe", CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("boot files");
    }

    [Test]
    public async Task CreateIsoBuildsDualBootData() {
        var media = Path.Combine(_root, "media");
        Directory.CreateDirectory(Path.Combine(media, "boot"));
        Directory.CreateDirectory(Path.Combine(media, "efi", "microsoft", "boot"));
        await File.WriteAllTextAsync(Path.Combine(media, "boot", "etfsboot.com"), "b");
        await File.WriteAllTextAsync(Path.Combine(media, "efi", "microsoft", "boot", "efisys_noprompt.bin"), "e");
        _runner.Handler = (_, args) => {
            File.WriteAllText(args[^1], "iso");
            return FakeProcessRunner.Ok();
        };

        await _builder.CreateIsoAsync(media, Path.Combine(_root, "x.iso"), "oscdimg.exe", CancellationToken.None);

        var args = string.Join(' ', _runner.ArgsOf(0));
        await Assert.That(args).Contains("-bootdata:2#p0,e,b");
        await Assert.That(args).Contains("#pEF,e,b");
        await Assert.That(args).Contains("-udfver102");
    }

    [Test]
    public async Task ComputeHashUsesXxHash3() {
        var file = Path.Combine(_root, "data.bin");
        await File.WriteAllTextAsync(file, "abc");
        var hash = await OutputBuilder.ComputeHashAsync(file, CancellationToken.None);
        await Assert.That(hash).IsEqualTo(await Fingerprinting.ComputeFileAsync(file, CancellationToken.None));
        await Assert.That(hash).Length().IsEqualTo(16);
    }

    private string CreateMediaSource() {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(source, "sources"));
        File.WriteAllText(Path.Combine(source, "sources", "install.wim"), "old");
        return source;
    }
}
