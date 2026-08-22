using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;

namespace TinyWin2.Core.Tests;

public sealed class LayerEvidenceTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();

    [Test]
    public async Task ManifestRoundTripsAndFiltersHiveTransactionNoise() {
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
            new BuildLog(), CancellationToken.None);
        var loaded = LayerEvidence.LoadManifest(manifest);
        await Assert.That(loaded.ContainsKey("Windows\\notepad.exe")).IsTrue();
        await Assert.That(loaded.ContainsKey("inetpub\\wwwroot")).IsFalse(); // directories are not listed
        await Assert.That(loaded.ContainsKey("Users\\All Users")).IsTrue(); // junction recorded as entry
        // hive transaction noise filtered out
        foreach (var key in loaded.Keys) {
            await Assert.That(key.Contains("regtrans-ms")).IsFalse();
            await Assert.That(key.Contains(".TM.")).IsFalse();
            await Assert.That(key.EndsWith(".LOG1")).IsFalse();
        }
    }

    [Test]
    public async Task RegistrySnapshotSplitsByHive() {
        var snapshot = ";;hive software\r\nWindows Registry Editor Version 5.00\r\n[HKEY_LOCAL_MACHINE\\TinyWin\\K]\r\n\"A\"=dword:1\r\n" +
                       ";;hive system\r\n[HKEY_LOCAL_MACHINE\\TinyWin\\S]\r\n";
        var hives = LayerEvidence.SplitByHive(snapshot);
        await Assert.That(hives.Count).IsEqualTo(2);
        await Assert.That(hives["software"]).Contains("\"A\"=dword:1");
        await Assert.That(hives["system"]).Contains("HKEY_LOCAL_MACHINE");
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public async Task ManifestKeepsReadableFilesWhenAChildDirectoryCannotBeRead() {
        var imageDir = Path.Combine(_root, "image");
        Directory.CreateDirectory(imageDir);
        await File.WriteAllTextAsync(Path.Combine(imageDir, "readable.txt"), "payload");

        var inaccessible = Path.Combine(imageDir, "inaccessible");
        Directory.CreateDirectory(inaccessible);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("current user has no SID");
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory | FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Deny);
        var inaccessibleDirectory = new DirectoryInfo(inaccessible);
        var security = inaccessibleDirectory.GetAccessControl();
        security.AddAccessRule(rule);
        inaccessibleDirectory.SetAccessControl(security);

        var manifest = LayerEvidence.ManifestPathFor(LayerEvidence.SnapshotsRoot(_root), 0);
        try {
            await LayerEvidence.CaptureAsync(imageDir, _root, 0, new FakeProcessRunner(),
                new BuildLog(), CancellationToken.None);
        }
        finally {
            security = inaccessibleDirectory.GetAccessControl();
            security.RemoveAccessRule(rule);
            inaccessibleDirectory.SetAccessControl(security);
        }
        var loaded = LayerEvidence.LoadManifest(manifest);

        await Assert.That(loaded.ContainsKey("readable.txt")).IsTrue();
        await Assert.That(loaded.ContainsKey("inaccessible")).IsFalse();
        await Assert.That(LayerEvidence.LoadManifestSnapshot(manifest).Complete).IsFalse();
    }

    [Test]
    public async Task LoadManifestRejectsMalformedRowsAndDuplicatePaths() {
        var manifest = Path.Combine(_root, "bad.tsv");
        await File.WriteAllTextAsync(manifest, "# tinywin2-files-v1 complete\nnot-a-row\n");
        var malformed = Assert.Throws<InvalidDataException>(() => LayerEvidence.LoadManifest(manifest));
        await Assert.That(malformed.Message).Contains("malformed");

        await File.WriteAllTextAsync(manifest,
            "# tinywin2-files-v1 complete\n1\t2\ta.txt\n2\t3\ta.txt\n");
        var duplicate = Assert.Throws<InvalidDataException>(() => LayerEvidence.LoadManifest(manifest));
        await Assert.That(duplicate.Message).Contains("duplicate");
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
