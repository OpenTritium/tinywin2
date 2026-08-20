using TinyWin2.Core.Layers;

namespace TinyWin2.Core.Tests;

public sealed class LayerEvidenceTests : IDisposable
{
    private readonly string _root = TestPlans.CreateTempDirectory();

    [Test]
    public async Task ManifestRoundTripsAndFiltersHiveTransactionNoise()
    {
        var imageDir = Path.Combine(_root, "image");
        Directory.CreateDirectory(Path.Combine(imageDir, "Windows", "System32", "config"));
        Directory.CreateDirectory(Path.Combine(imageDir, "inetpub", "wwwroot"));
        var realFile = Path.Combine(imageDir, "Windows", "notepad.exe");
        await File.WriteAllTextAsync(realFile, "payload");
        var noise = Path.Combine(imageDir, "Windows", "System32", "config",
            "SOFTWARE{7dff3b8b-eff2-11ee-a54c-6045bd37bcec}.TMContainer00000000000000000001.regtrans-ms");
        await File.WriteAllTextAsync(noise, "tx");
        var logNoise = Path.Combine(imageDir, "Windows", "System32", "config", "SYSTEM.LOG1");
        await File.WriteAllTextAsync(logNoise, "log");

        // Junction noise: a self-referencing link must be recorded once, never followed.
        Directory.CreateDirectory(Path.Combine(imageDir, "Users"));
        Directory.CreateSymbolicLink(
            Path.Combine(imageDir, "Users", "All Users"),
            Path.Combine(imageDir, "Users"));

        var manifest = LayerEvidence.ManifestPathFor(LayerEvidence.SnapshotsRoot(_root), 0);
        await LayerEvidence.CaptureAsync(imageDir, _root, 0, new FakeProcessRunner(),
            new Core.Logging.BuildLog { EchoConsole = false }, CancellationToken.None);

        var loaded = LayerEvidence.LoadManifest(manifest);
        await Assert.That(loaded.ContainsKey("Windows\\notepad.exe")).IsTrue();
        await Assert.That(loaded.ContainsKey("inetpub\\wwwroot")).IsFalse(); // directories are not listed
        await Assert.That(loaded.ContainsKey("Users\\All Users")).IsTrue(); // junction recorded as entry
        // hive transaction noise filtered out
        foreach (var key in loaded.Keys)
        {
            await Assert.That(key.Contains("regtrans-ms")).IsFalse();
            await Assert.That(key.Contains(".TM.")).IsFalse();
            await Assert.That(key.EndsWith(".LOG1")).IsFalse();
        }
    }

    [Test]
    public async Task RegistrySnapshotSplitsByHive()
    {
        var snapshot = ";;hive software\r\nWindows Registry Editor Version 5.00\r\n[HKEY_LOCAL_MACHINE\\TinyWin\\K]\r\n\"A\"=dword:1\r\n" +
                       ";;hive system\r\n[HKEY_LOCAL_MACHINE\\TinyWin\\S]\r\n";
        var hives = LayerEvidence.SplitByHive(snapshot);

        await Assert.That(hives.Count).IsEqualTo(2);
        await Assert.That(hives["software"]).Contains("\"A\"=dword:1");
        await Assert.That(hives["system"]).Contains("HKEY_LOCAL_MACHINE");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
