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
    public async Task MountedImageRequiresWinreAndReagentFiles() {
        var mount = Path.Combine(_root, "mount");
        Directory.CreateDirectory(Path.Combine(mount, "Windows", "System32", "Recovery"));
        await File.WriteAllTextAsync(Path.Combine(mount, "Windows", "System32", "Recovery", "ReAgent.xml"),
            "reagent");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SetupImageContractValidator.ValidateMountedImage(mount, "broken.wim", 1));

        await Assert.That(ex.Message).Contains("Winre.wim");
        await Assert.That(ex.Message).Contains("standard Windows Setup ISO");
    }

    [Test]
    public async Task MountedImagePassesWhenBothRecoveryFilesExist() {
        var mount = Path.Combine(_root, "mount");
        var recovery = Path.Combine(mount, "Windows", "System32", "Recovery");
        Directory.CreateDirectory(recovery);
        await File.WriteAllTextAsync(Path.Combine(recovery, "Winre.wim"), "winre");
        await File.WriteAllTextAsync(Path.Combine(recovery, "ReAgent.xml"), "reagent");

        SetupImageContractValidator.ValidateMountedImage(mount, "valid.wim", 1);
    }
}
