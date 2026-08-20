using TinyWin2.Core.Logging;

namespace TinyWin2.Core.Tests;

public sealed class SerilogSinkTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();

    [Test]
    public async Task BridgesBuildEventsToFileWithDomainFields() {
        var log = new BuildLog();
        var logFile = Path.Combine(_root, "build.log");
        using (log.UseSerilog(logFile, echoConsole: false)) {
            log.Phase = "plan";
            log.Info("step 1/3: '移除 inetpub 目录'", "fs.inetpub", 1);
            log.Warn("no services matched pattern 'X_*' in ControlSet001; skipping.");
            log.Error("boom");
        }
        var content = await File.ReadAllTextAsync(logFile);
        await Assert.That(content).Contains("step 1/3");
        await Assert.That(content).Contains("no services matched");
        await Assert.That(content).Contains("boom");
        await Assert.That(content).Contains("[WRN]");
        await Assert.That(content).Contains("[ERR]");
    }

    [Test]
    public async Task FlushingOnDisposeProducesCompleteFile() {
        var log = new BuildLog();
        var logFile = Path.Combine(_root, "flush.log");
        using (var sink = log.UseSerilog(logFile, echoConsole: false)) {
            log.Info("before-dispose");
            // Not yet disposed: Serilog file sink buffers; content may be partial.
            sink.Dispose();
            var content = await File.ReadAllTextAsync(logFile);
            await Assert.That(content).Contains("before-dispose");
        }
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
