using System.Runtime.Versioning;
using TinyWin2.Core.Env;

namespace TinyWin2.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class EnvironmentDoctorTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();

    [Test]
    public async Task FreeSpaceBelowMinimumFailsWithGbDetail() {
        var result = EnvironmentDoctor.CheckFreeSpace(_root, long.MaxValue);
        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Required).IsTrue();
        await Assert.That(result.Name).IsEqualTo("free-space");
        await Assert.That(result.Detail).Contains("GB free");
    }

    [Test]
    public async Task FreeSpaceAboveMinimumPasses() {
        var result = EnvironmentDoctor.CheckFreeSpace(_root, 1);
        await Assert.That(result.Ok).IsTrue();
    }

    [Test]
    public async Task NetworkPathsAreRejected() {
        var result = EnvironmentDoctor.CheckFreeSpace(@"\\server\share", 1);
        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Detail).Contains("network path");
    }

    [Test]
    public async Task UninspectablePathsFailGracefully() {
        var result = EnvironmentDoctor.CheckFreeSpace("", 1);
        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Detail).Contains("cannot inspect");
    }

    [Test]
    public async Task CheckReportsAdministratorAndCoreTools() {
        var results = EnvironmentDoctor.Check();
        var names = results.Select(r => r.Name).ToList();
        await Assert.That(names).Contains("administrator");
        await Assert.That(names).Contains("dism.exe");
        await Assert.That(names).Contains("reg.exe");
        // System32-first resolution must defeat PATH shadowing on this host.
        var dism = results.First(r => r.Name == "dism.exe");
        await Assert.That(dism.Ok).IsTrue();
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
