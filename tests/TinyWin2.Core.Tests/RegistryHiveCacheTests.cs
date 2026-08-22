using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Tests;

public sealed class RegistryHiveCacheTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();
    private readonly FakeProcessRunner _runner = new();
    private readonly RegistryHiveCache _cache;

    public RegistryHiveCacheTests() {
        _cache = new RegistryHiveCache(_root, _runner);
    }

    [Test]
    public async Task UnknownHiveIdThrows() {
        var ex = Assert.Throws<ExecException>(() =>
            _cache.GetAsync("bogus", new BuildLog(), CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("unknown registry hive");
    }

    [Test]
    public async Task MissingHiveFileThrows() {
        var ex = Assert.Throws<ExecException>(() =>
            _cache.GetAsync("software", new BuildLog(), CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("was not found at");
    }

    [Test]
    public async Task GetLoadsOnceAndCachesWithinSession() {
        CreateHiveFile("software");
        var log = new BuildLog();
        var first = await _cache.GetAsync("software", log, CancellationToken.None);
        var second = await _cache.GetAsync("SOFTWARE", log, CancellationToken.None);
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(_runner.Calls.Count(c => c.Args.Count > 0 && c.Args[0] == "load")).IsEqualTo(1);
        await Assert.That(first.HiveKey).IsEqualTo("HKLM\\TinyWin2_software");
    }

    [Test]
    public async Task SessionPrefixFlowsIntoHiveKey() {
        CreateHiveFile("system");
        _cache.SetSessionPrefix("TinyWin2_L003");
        var hive = await _cache.GetAsync("system", new BuildLog(), CancellationToken.None);
        await Assert.That(hive.HiveKey).IsEqualTo("HKLM\\TinyWin2_L003_system");
    }

    [Test]
    public async Task UnloadRetriesUntilTheHiveReleases() {
        CreateHiveFile("software");
        var log = new BuildLog();
        _ = await _cache.GetAsync("software", log, CancellationToken.None);
        var unloadAttempts = 0;
        _runner.Handler = (_, args) => args.Count > 0 && args[0] == "unload" && ++unloadAttempts <= 2
            ? throw new ProcessRunnerException("reg.exe", FakeProcessRunner.Fail(1))
            : FakeProcessRunner.Ok();

        await _cache.UnloadAllAsync(log, CancellationToken.None);

        await Assert.That(unloadAttempts).IsEqualTo(3);
    }

    [Test]
    public async Task UnloadGivesUpAfterFiveAttemptsAndFailsVisibly() {
        CreateHiveFile("software");
        var log = new BuildLog();
        var errors = new List<string>();
        using var token = log.Attach(evt => {
            if (evt.Level == BuildEventLevel.Error) {
                errors.Add(evt.Message);
            }
        });
        _ = await _cache.GetAsync("software", log, CancellationToken.None);
        var unloadAttempts = 0;
        _runner.Handler = (_, args) => args.Count > 0 && args[0] == "unload"
            ? throw new ProcessRunnerException("reg.exe", FakeProcessRunner.Fail(++unloadAttempts))
            : FakeProcessRunner.Ok();

        var ex = Assert.Throws<AggregateException>(() =>
            _cache.UnloadAllAsync(log, CancellationToken.None).GetAwaiter().GetResult());

        await Assert.That(unloadAttempts).IsEqualTo(5);
        await Assert.That(ex.Message).Contains("could not be unloaded");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("could not unload offline hive 'software'");

        _runner.Handler = (_, args) => args.Count > 0 && args[0] == "unload"
            ? FakeProcessRunner.Ok()
            : FakeProcessRunner.Ok();
        await _cache.UnloadAllAsync(log, CancellationToken.None);
    }

    [Test]
    public async Task UnloadWithoutLoadedHivesIsANoOp() {
        await _cache.UnloadAllAsync(new BuildLog(), CancellationToken.None);
        await Assert.That(_runner.Calls).IsEmpty();
    }

    private void CreateHiveFile(string hiveId) {
        var relative = RegistryHiveCache.HiveFiles[hiveId];
        var path = Path.Combine(_root, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hive");
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
