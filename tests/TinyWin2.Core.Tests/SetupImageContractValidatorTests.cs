using TinyWin2.Core.Logging;
using TinyWin2.Core.Pipeline;

namespace TinyWin2.Core.Tests;

public sealed class SetupImageContractValidatorTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        }
        catch {
            /* best effort */
        }
    }

    [Test]
    public async Task MountedImageWithoutWinreWarnsButPasses() {
        var mount = Path.Combine(_root, "mount");
        Directory.CreateDirectory(Path.Combine(mount, "Windows", "System32", "Recovery"));
        await File.WriteAllTextAsync(Path.Combine(mount, "Windows", "System32", "Recovery", "ReAgent.xml"),
            "reagent");

        // WinRE-less images install and run fine; missing recovery files only downgrade to a warning.
        SetupImageContractValidator.ValidateMountedImage(mount, "compact.wim", 1, new BuildLog());
    }

    [Test]
    public async Task MountedImagePassesWhenBothRecoveryFilesExist() {
        var mount = Path.Combine(_root, "mount");
        var recovery = Path.Combine(mount, "Windows", "System32", "Recovery");
        Directory.CreateDirectory(recovery);
        await File.WriteAllTextAsync(Path.Combine(recovery, "Winre.wim"), "winre");
        await File.WriteAllTextAsync(Path.Combine(recovery, "ReAgent.xml"), "reagent");

        SetupImageContractValidator.ValidateMountedImage(mount, "valid.wim", 1, new BuildLog());
    }
}
