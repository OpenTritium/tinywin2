using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Executers.Driver;
using TinyWin2.Core.Executers.Fs;
using TinyWin2.Core.Executers.Registry;
using DismErrors = TinyWin2.Core.Executers.Dism.DismErrors;

namespace TinyWin2.Core.Tests;

public sealed class FeatureExecuterTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();
    private readonly FeatureExecuter _executer;

    public FeatureExecuterTests() {
        _executer = new FeatureExecuter(_harness.Runner);
    }

    private void SetupFeatures(params (string Name, string State)[] features) {
        var blocks = string.Join("\r\n\r\n", features.Select(f => $"Feature Name : {f.Name}\r\nState : {f.State}"));
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Features")
            ? FakeProcessRunner.Ok(blocks)
            : FakeProcessRunner.Ok();
    }

    [Test]
    public async Task EnabledFeatureNeedsRemoval() {
        SetupFeatures(("Hyper-V", "Enabled"), ("NetFx3", "Disabled"));

        // removePayload defaults to true: a merely-Disabled feature still carries payload.
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", Ensure.Absent,
                ("features", new JsonArray("Hyper-V", "NetFx3"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences.Count).IsEqualTo(2);
        var withoutPayload = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", Ensure.Absent,
                ("features", new JsonArray("Hyper-V", "NetFx3")), ("removePayload", false)), CancellationToken.None);
        await Assert.That(withoutPayload.Differences.Count).IsEqualTo(1);
        await Assert.That(withoutPayload.Differences[0].Target).IsEqualTo("Hyper-V");
    }

    [Test]
    public async Task DisabledWithoutPayloadTargetIsSatisfied() {
        SetupFeatures(("NetFx3", "Disabled"));
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", Ensure.Absent,
                ("features", new JsonArray("NetFx3")), ("removePayload", false)), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }

    [Test]
    public async Task ApplyDisablesWithRemoveFlag() {
        SetupFeatures(("Hyper-V", "Enabled"));
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", Ensure.Absent,
                ("features", new JsonArray("Hyper-V")), ("removePayload", true)), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        var disable = _harness.Runner.Calls.First(c => c.Args.Contains("/Disable-Feature"));
        await Assert.That(string.Join(" ", disable.Args)).Contains("/FeatureName:Hyper-V");
        await Assert.That(string.Join(" ", disable.Args)).Contains("/Remove");
    }

    [Test]
    public async Task PermanentFeatureIsSkippedNotFailed() {
        SetupFeatures(("Permanent", "Enabled"));
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Features")
            ? FakeProcessRunner.Ok("Feature Name : Permanent\r\nState : Enabled")
            : FakeProcessRunner.Fail(DismErrors.CbsEInvalidInstallState);
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", Ensure.Absent,
                ("features", new JsonArray("Permanent"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(result.Changes[0].Kind).IsEqualTo(ChangeKind.Skipped);
    }

    [Test]
    public async Task ProviderUnavailableEditionSkipsEverything() {
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Fail(50);
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", Ensure.Absent,
                ("features", new JsonArray("AnyFeature"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }

    public void Dispose() => _harness.Dispose();
}

public sealed class CapabilityAndPackageTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();

    [Test]
    public async Task InstalledCapabilityIsRemoved() {
        var capabilities = new CapabilityExecuter(_harness.Runner);
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Capabilities")
            ? FakeProcessRunner.Ok("Capability Identity : Language.OCR~~~zh-CN~0.0.1.0\r\nState : Installed")
            : FakeProcessRunner.Ok();
        var diff = await capabilities.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.capability", Ensure.Absent,
                ("capabilities", new JsonArray("Language.OCR~~~zh-CN~0.0.1.0"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        var result = await capabilities.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.capability", Ensure.Absent,
                ("capabilities", new JsonArray("Language.OCR~~~zh-CN~0.0.1.0"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(string.Join(" ", _harness.Runner.Calls.Last().Args)).Contains("/Remove-Capability");
    }

    [Test]
    public async Task PermanentCapabilityIsSkipped() {
        var capabilities = new CapabilityExecuter(_harness.Runner);
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Capabilities")
            ? FakeProcessRunner.Ok("Capability Identity : Cap1\r\nState : Installed")
            : FakeProcessRunner.Fail(DismErrors.CbsECannotUninstall);
        var result = await capabilities.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.capability", Ensure.Absent,
                ("capabilities", new JsonArray("Cap1"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(result.Changes[0].Kind).IsEqualTo(ChangeKind.Skipped);
    }

    [Test]
    public async Task PackagesMatchedByRegexWithRemovableStates() {
        var packages = new PackageExecuter(_harness.Runner);
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Packages")
            ? FakeProcessRunner.Ok("""
                                   Package Identity : Microsoft-Windows-Foo-Package~31bf3856ad364e35~amd64~~10.0.1
                                   State : Installed

                                   Package Identity : Microsoft-Windows-Bar-Package~31bf3856ad364e35~amd64~~10.0.1
                                   State : Superseded

                                   Package Identity : Microsoft-Windows-Pending-Package~31bf3856ad364e35~amd64~~10.0.1
                                   State : Install Pending
                                   """)
            : FakeProcessRunner.Ok();
        var diff = await packages.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.package", Ensure.Absent,
                ("patterns", new JsonArray("^Microsoft-Windows-(Foo|Bar|Pending)-Package~"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences.Count).IsEqualTo(3);
        await Assert.That(diff.Differences.Count(d => d.Kind == ChangeKind.Skipped)).IsEqualTo(1);
        await Assert.That(diff.Differences.Count(d => d.Kind == ChangeKind.Removed)).IsEqualTo(2);
    }

    [Test]
    public async Task ApplyPreservesSkippedTargetsWhenSomePackagesAreRemoved() {
        var packages = new PackageExecuter(_harness.Runner);
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Packages")
            ? FakeProcessRunner.Ok("Package Identity : Microsoft-Windows-Foo-Package~1\r\nState : Installed\r\n\r\n"
                                   + "Package Identity : Microsoft-Windows-Bar-Package~1\r\nState : Superseded")
            : FakeProcessRunner.Ok();
        var result = await packages.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.package", Ensure.Absent,
                ("patterns", new JsonArray("^Microsoft-Windows-(Foo|Bar)-Package~"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(result.Changes.Count).IsEqualTo(2);
        await Assert.That(result.Changes.Count(c => c.Kind == ChangeKind.Skipped)).IsEqualTo(1);
        await Assert.That(result.Changes.Count(c => c.Kind == ChangeKind.Removed)).IsEqualTo(1);
    }

    public void Dispose() => _harness.Dispose();
}

public sealed class ComponentStoreExecuterTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();
    private readonly ComponentStoreExecuter _executer;

    public ComponentStoreExecuterTests() {
        _executer = new ComponentStoreExecuter(_harness.Runner);
    }

    [Test]
    public async Task CleanupRunsAndApplies() {
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.component-store", Ensure.Absent, ("resetBase", true)),
            CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(string.Join(" ", _harness.Runner.Calls[0].Args)).Contains("/ResetBase");
    }

    [Test]
    public async Task Error4350DowngradesToSkipped() {
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Fail(4350);
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.component-store", Ensure.Absent), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Skipped);
        await Assert.That(result.SkipReason).Contains("4350");
    }

    [Test]
    public async Task PresentEnsureIsRejected() {
        var ex = Assert.Throws<ExecException>(() =>
            _executer.ApplyAsync(_harness.NewContext(),
                ExecuterTestHarness.Spec("dism.component-store", Ensure.Present), CancellationToken.None)
                .GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("present is not implemented");
        await Assert.That(_harness.Runner.Calls).IsEmpty();
    }

    public void Dispose() => _harness.Dispose();
}

public sealed class AppxProvisionedExecuterTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();
    private readonly AppxProvisionedExecuter _executer;

    public AppxProvisionedExecuterTests() {
        _executer = new AppxProvisionedExecuter(_harness.Runner);
    }

    [Test]
    public async Task WildcardsMatchDisplayNameAndRemove() {
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-ProvisionedAppxPackages")
            ? FakeProcessRunner.Ok("""
                                   DisplayName : Microsoft.XboxApp
                                   PackageName : Microsoft.XboxApp_48.48.48.0_x64__8wekyb3d8bbwe

                                   DisplayName : Microsoft.WindowsCalculator
                                   PackageName : Microsoft.WindowsCalculator_11.0_x64__8wekyb3d8bbwe
                                   """)
            : FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.provisioned", Ensure.Absent,
                ("patterns", new JsonArray("Microsoft.Xbox*"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(result.Changes.Count).IsEqualTo(1);
        var remove = _harness.Runner.Calls.First(c => c.Args.Contains("/Remove-ProvisionedAppxPackage"));
        await Assert.That(string.Join(" ", remove.Args)).Contains("Microsoft.XboxApp_48.48.48.0_x64__8wekyb3d8bbwe");
    }

    [Test]
    public async Task ServerExit87IsInapplicableAndSkips() {
        // Server without appx provisioning answers ERROR_INVALID_PARAMETER (87) on the listing.
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Fail(87);
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.provisioned", Ensure.Absent,
                ("patterns", new JsonArray("Microsoft.Xbox*"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Skipped);
    }

    [Test]
    public async Task UsesRealDismPackageNameField() {
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-ProvisionedAppxPackages")
            ? FakeProcessRunner.Ok("DisplayName : Microsoft.WindowsCalculator\r\n"
                                   + "PackageName : Microsoft.WindowsCalculator_1.0.0.0_neutral_~_8wekyb3d8bbwe")
            : FakeProcessRunner.Ok();
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.provisioned", Ensure.Absent,
                ("patterns", new JsonArray("Microsoft.WindowsCalculator"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences[0].Target)
            .IsEqualTo("Microsoft.WindowsCalculator_1.0.0.0_neutral_~_8wekyb3d8bbwe");
    }

    [Test]
    public async Task EditionWithoutAppxProviderSkips() {
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Fail(50);
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.provisioned", Ensure.Absent,
                ("patterns", new JsonArray("Microsoft.Xbox*"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }

    public void Dispose() => _harness.Dispose();
}

public sealed class FilesystemExecuterTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();
    private readonly FsPathExecuter _executer;

    public FilesystemExecuterTests() {
        _executer = new FsPathExecuter(_harness.Runner);
    }

    [Test]
    public async Task AbsentDeletesExistingPath() {
        var target = Path.Combine(_harness.MountPath, "Windows", "Web", "Wallpaper");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "img.jpg"), "x");
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("fs.path", Ensure.Absent,
                ("paths", new JsonArray("Windows/Web/Wallpaper"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(Directory.Exists(target)).IsFalse();
    }

    [Test]
    public async Task AbsentMissingPathSkips() {
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("fs.path", Ensure.Absent,
                ("paths", new JsonArray("Does/Not/Exist"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Skipped);
    }

    [Test]
    public async Task RejectsTraversalAndRootedPaths() {
        foreach (var unsafePath in new[] { "..\\escape", "C:\\Windows", "a/../../b" }) {
            var ex = Assert.Throws<ExecException>(() =>
                FsPathExecuter.ResolveInsideMount(_harness.MountPath, unsafePath));
            await Assert.That(ex.Message).Contains("unsafe");
        }
    }

    [Test]
    public async Task PresentCopiesAssetDirectoryIntoImage() {
        var assets = Path.Combine(_harness.MountPath, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "tools", "sub"));
        File.WriteAllText(Path.Combine(assets, "tools", "app.exe"), "bin");
        File.WriteAllText(Path.Combine(assets, "tools", "sub", "lib.dll"), "dll");
        var context = new ExecContext(_harness.MountPath, _harness.Log,
            new RegistryHiveCache(_harness.MountPath, _harness.Runner), assets);
        var result = await _executer.ApplyAsync(context,
            ExecuterTestHarness.Spec("fs.path", Ensure.Present,
                ("path", "ProgramData\\Tools"), ("source", "tools")), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(File.Exists(Path.Combine(_harness.MountPath, "ProgramData", "Tools", "app.exe"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(_harness.MountPath, "ProgramData", "Tools", "sub", "lib.dll")))
            .IsTrue();
    }

    public void Dispose() => _harness.Dispose();
}

public sealed class DriverStoreExecuterTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();
    private readonly DriverStoreExecuter _executer;

    public DriverStoreExecuterTests() {
        _executer = new DriverStoreExecuter(_harness.Runner);
    }

    private string CreateRepository(params string[] directoryNames) {
        var root = Path.Combine(_harness.MountPath, "Windows", "System32", "DriverStore", "FileRepository");
        foreach (var name in directoryNames) {
            Directory.CreateDirectory(Path.Combine(root, name));
        }

        return root;
    }

    [Test]
    public async Task RemovesDirectoriesNamedAfterInf() {
        var root = CreateRepository("mdm.inf_amd64_1234abcd", "usb.inf_amd64_5678ef");
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("driver.store", Ensure.Absent,
                ("infNames", new JsonArray("mdm.inf"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(Directory.Exists(Path.Combine(root, "mdm.inf_amd64_1234abcd"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(root, "usb.inf_amd64_5678ef"))).IsTrue();
    }

    [Test]
    public async Task UnknownInfIsSkipped() {
        CreateRepository("usb.inf_amd64_5678ef");
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("driver.store", Ensure.Absent,
                ("infNames", new JsonArray("ghost.inf"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Skipped);
    }

    [Test]
    public async Task InvalidInfNameRejected() {
        var ex = Assert.Throws<ExecException>(() =>
            _executer.InspectAsync(_harness.NewContext(),
                    ExecuterTestHarness.Spec("driver.store", Ensure.Absent,
                        ("infNames", new JsonArray("C:\\evil\\path.inf"))), CancellationToken.None).GetAwaiter()
                .GetResult());
        await Assert.That(ex.Message).Contains("invalid driver INF name");
    }

    public void Dispose() => _harness.Dispose();
}

public sealed class ExecuterRegistryTests {
    [Test]
    public async Task RegistersAllBuiltInResources() {
        var registry = new ExecuterRegistry(new FakeProcessRunner());
        foreach (var resource in new[] {
                     "registry.value", "registry.service", "dism.feature", "dism.capability",
                     "dism.package", "dism.component-store", "appx.provisioned", "driver.store", "fs.path",
                 }) {
            await Assert.That(registry.Get(resource).Resource).IsEqualTo(resource);
        }
    }

    [Test]
    public async Task UnknownResourceThrows() {
        var registry = new ExecuterRegistry(new FakeProcessRunner());
        var ex = Assert.Throws<ExecException>(() => registry.Get("nope.resource"));
        await Assert.That(ex.Message).Contains("no executer registered");
    }
}
