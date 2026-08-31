using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Executers.Driver;
using TinyWin2.Core.Executers.Fs;
using DismErrors = TinyWin2.Core.Executers.Dism.DismErrors;

namespace TinyWin2.Core.Tests;

public sealed class FeatureExecuterTests : IDisposable {
    private readonly FeatureExecuter _executer;
    private readonly ExecuterTestHarness _harness = new();

    public FeatureExecuterTests() {
        _executer = new(_harness.Runner);
    }

    public void Dispose() => _harness.Dispose();

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
            ExecuterTestHarness.Spec("dism.feature", OperationAction.Remove,
                ("features", new JsonArray("Hyper-V", "NetFx3"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences.Count).IsEqualTo(2);
        var withoutPayload = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", OperationAction.Remove,
                ("features", new JsonArray("Hyper-V", "NetFx3")), ("removePayload", false)), CancellationToken.None);
        await Assert.That(withoutPayload.Differences.Count).IsEqualTo(1);
        await Assert.That(withoutPayload.Differences[0].Target).IsEqualTo("Hyper-V");
    }

    [Test]
    public async Task DisabledWithoutPayloadTargetIsSatisfied() {
        SetupFeatures(("NetFx3", "Disabled"));
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", OperationAction.Remove,
                ("features", new JsonArray("NetFx3")), ("removePayload", false)), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }

    [Test]
    public async Task ApplyDisablesWithRemoveFlag() {
        SetupFeatures(("Hyper-V", "Enabled"));
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", OperationAction.Remove,
                ("features", new JsonArray("Hyper-V")), ("removePayload", true)), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
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
            ExecuterTestHarness.Spec("dism.feature", OperationAction.Remove,
                ("features", new JsonArray("Permanent"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsTrue();
        await Assert.That(result.SkipReason).Contains("not removable");
        await Assert.That(result.Changes[0].Kind).IsEqualTo(ChangeKind.Skipped);
    }

    [Test]
    public async Task ProviderUnavailableEditionSkipsEverything() {
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Fail(50);
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", OperationAction.Remove,
                ("features", new JsonArray("AnyFeature"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }

    [Test]
    public async Task ForceExplicitRemovesFeatureOmittedFromOfflineListing() {
        SetupFeatures(("OtherFeature", "Enabled"));
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.feature", OperationAction.Remove,
                ("features", new JsonArray("Microsoft-RemoteDesktopConnection")),
                ("forceExplicit", true)), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        var disable = _harness.Runner.Calls.First(c => c.Args.Contains("/Disable-Feature"));
        await Assert.That(string.Join(" ", disable.Args))
            .Contains("/FeatureName:Microsoft-RemoteDesktopConnection");
    }
}

public sealed class CapabilityAndPackageTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Test]
    public async Task InstalledCapabilityIsRemoved() {
        var capabilities = new CapabilityExecuter(_harness.Runner);
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Capabilities")
            ? FakeProcessRunner.Ok("Capability Identity : Language.OCR~~~zh-CN~0.0.1.0\r\nState : Installed")
            : FakeProcessRunner.Ok();
        var diff = await capabilities.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.capability", OperationAction.Remove,
                ("capabilities", new JsonArray("Language.OCR~~~zh-CN~0.0.1.0"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        var result = await capabilities.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.capability", OperationAction.Remove,
                ("capabilities", new JsonArray("Language.OCR~~~zh-CN~0.0.1.0"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(string.Join(" ", _harness.Runner.Calls.Last().Args)).Contains("/Remove-Capability");
    }

    [Test]
    public async Task CapabilityWildcardMatchesVersionsAndRemovesConcreteIdentity() {
        var capabilities = new CapabilityExecuter(_harness.Runner);
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Capabilities")
            ? FakeProcessRunner.Ok("Capability Identity : Browser.InternetExplorer~~~~0.0.11.0\r\nState : Installed\r\n\r\n"
                                   + "Capability Identity : Browser.InternetExplorer~~~~0.0.12.0\r\nState : Not Present")
            : FakeProcessRunner.Ok();
        var spec = ExecuterTestHarness.Spec("dism.capability", OperationAction.Remove,
            ("capabilities", new JsonArray("Browser.InternetExplorer~~~~*")));

        var diff = await capabilities.InspectAsync(_harness.NewContext(), spec, CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences.Count).IsEqualTo(1);
        await Assert.That(diff.Differences[0].Target)
            .IsEqualTo("Browser.InternetExplorer~~~~0.0.11.0");

        var result = await capabilities.ApplyAsync(_harness.NewContext(), spec, CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        var remove = _harness.Runner.Calls.Last(c => c.Args.Contains("/Remove-Capability"));
        await Assert.That(string.Join(" ", remove.Args))
            .Contains("/CapabilityName:Browser.InternetExplorer~~~~0.0.11.0");
    }

    [Test]
    public async Task PermanentCapabilityIsSkipped() {
        var capabilities = new CapabilityExecuter(_harness.Runner);
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Capabilities")
            ? FakeProcessRunner.Ok("Capability Identity : Cap1\r\nState : Installed")
            : FakeProcessRunner.Fail(DismErrors.CbsECannotUninstall);
        var result = await capabilities.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.capability", OperationAction.Remove,
                ("capabilities", new JsonArray("Cap1"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsTrue();
        await Assert.That(result.SkipReason).Contains("not removable");
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
            ExecuterTestHarness.Spec("dism.package", OperationAction.Remove,
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
            ExecuterTestHarness.Spec("dism.package", OperationAction.Remove,
                ("patterns", new JsonArray("^Microsoft-Windows-(Foo|Bar)-Package~"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(result.Changes.Count).IsEqualTo(2);
        await Assert.That(result.Changes.Count(c => c.Kind == ChangeKind.Skipped)).IsEqualTo(1);
        await Assert.That(result.Changes.Count(c => c.Kind == ChangeKind.Removed)).IsEqualTo(1);
    }

    [Test]
    public async Task SkipsSupersededStagedPackageWhenActiveFamilyIsInstalled() {
        var packages = new PackageExecuter(_harness.Runner);
        var output = "Package Identity : Microsoft-Windows-SenseClient-FoD-Package~31bf3856ad364e35~amd64~~10.0.1\r\n"
                     + "State : Staged\r\n\r\n"
                     + "Package Identity : Microsoft-Windows-SenseClient-FoD-Package~31bf3856ad364e35~amd64~~10.0.2\r\n"
                     + "State : Installed\r\n";
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Packages")
            ? FakeProcessRunner.Ok(output)
            : FakeProcessRunner.Ok();

        var result = await packages.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.package", OperationAction.Remove,
                ("patterns", new JsonArray("^Microsoft-Windows-SenseClient-FoD-Package~"))), CancellationToken.None);

        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(result.Changes.Count).IsEqualTo(2);
        await Assert.That(result.Changes.Count(c => c.Kind == ChangeKind.Skipped)).IsEqualTo(1);
        await Assert.That(result.Changes.Count(c => c.Kind == ChangeKind.Removed)).IsEqualTo(1);
        await Assert.That(_harness.Runner.Calls.Count(c => c.Args.Contains("/Remove-Package"))).IsEqualTo(1);
    }
}

public sealed class ComponentStoreExecuterTests : IDisposable {
    private readonly ComponentStoreExecuter _executer;
    private readonly ExecuterTestHarness _harness = new();

    public ComponentStoreExecuterTests() {
        _executer = new(_harness.Runner);
    }

    public void Dispose() => _harness.Dispose();

    [Test]
    public async Task CleanupRunsAndApplies() {
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.component-store", OperationAction.Cleanup, ("resetBase", true)),
            CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(string.Join(" ", _harness.Runner.Calls[0].Args)).Contains("/ResetBase");
    }

    [Test]
    public async Task Error4350DowngradesToSkipped() {
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Fail(4350);
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("dism.component-store", OperationAction.Cleanup), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsTrue();
        await Assert.That(result.SkipReason).Contains("4350");
    }

    [Test]
    public async Task NonCleanupActionIsRejected() {
        var ex = Assert.Throws<ExecException>(() =>
            _executer.ApplyAsync(_harness.NewContext(),
                    ExecuterTestHarness.Spec("dism.component-store", OperationAction.Apply), CancellationToken.None)
                .GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("action 'cleanup'");
        await Assert.That(_harness.Runner.Calls).IsEmpty();
    }
}

public sealed class AppxProvisionedExecuterTests : IDisposable {
    private readonly AppxProvisionedExecuter _executer;
    private readonly ExecuterTestHarness _harness = new();

    public AppxProvisionedExecuterTests() {
        _executer = new(_harness.Runner);
    }

    public void Dispose() => _harness.Dispose();

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
            ExecuterTestHarness.Spec("appx.provisioned", OperationAction.Remove,
                ("patterns", new JsonArray("Microsoft.Xbox*"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(result.Changes.Count).IsEqualTo(1);
        var remove = _harness.Runner.Calls.First(c => c.Args.Contains("/Remove-ProvisionedAppxPackage"));
        await Assert.That(string.Join(" ", remove.Args)).Contains("Microsoft.XboxApp_48.48.48.0_x64__8wekyb3d8bbwe");
    }

    [Test]
    public async Task ServerExit87IsInapplicableAndSkips() {
        // Server without appx provisioning answers ERROR_INVALID_PARAMETER (87) on the listing.
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Fail(87);
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.provisioned", OperationAction.Remove,
                ("patterns", new JsonArray("Microsoft.Xbox*"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsTrue();
    }

    [Test]
    public async Task UsesRealDismPackageNameField() {
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-ProvisionedAppxPackages")
            ? FakeProcessRunner.Ok("DisplayName : Microsoft.WindowsCalculator\r\n"
                                   + "PackageName : Microsoft.WindowsCalculator_1.0.0.0_neutral_~_8wekyb3d8bbwe")
            : FakeProcessRunner.Ok();
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.provisioned", OperationAction.Remove,
                ("patterns", new JsonArray("Microsoft.WindowsCalculator"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences[0].Target)
            .IsEqualTo("Microsoft.WindowsCalculator_1.0.0.0_neutral_~_8wekyb3d8bbwe");
    }

    [Test]
    public async Task EditionWithoutAppxProviderSkips() {
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Fail(50);
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.provisioned", OperationAction.Remove,
                ("patterns", new JsonArray("Microsoft.Xbox*"))), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }
}

public sealed class FilesystemExecuterTests : IDisposable {
    private readonly FsPathExecuter _executer;
    private readonly ExecuterTestHarness _harness = new();

    public FilesystemExecuterTests() {
        _executer = new(_harness.Runner);
    }

    public void Dispose() => _harness.Dispose();

    [Test]
    public async Task AbsentDeletesExistingPath() {
        var target = Path.Combine(_harness.MountPath, "Windows", "Web", "Wallpaper");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "img.jpg"), "x");
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("fs.path", OperationAction.Remove,
                ("paths", new JsonArray("Windows/Web/Wallpaper"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(Directory.Exists(target)).IsFalse();
    }

    [Test]
    public async Task AbsentMissingPathSkips() {
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("fs.path", OperationAction.Remove,
                ("paths", new JsonArray("Does/Not/Exist"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsTrue();
    }

    [Test]
    public async Task AbsentExpandsWildcardsPerPathSegment() {
        var aliceDesktop = Path.Combine(_harness.MountPath, "Users", "Alice", "Desktop");
        var bobDesktop = Path.Combine(_harness.MountPath, "Users", "Bob", "Desktop");
        var nestedDesktop = Path.Combine(_harness.MountPath, "Users", "Alice", "Nested", "Desktop");
        Directory.CreateDirectory(aliceDesktop);
        Directory.CreateDirectory(bobDesktop);
        Directory.CreateDirectory(nestedDesktop);
        await File.WriteAllTextAsync(Path.Combine(aliceDesktop, "Microsoft Edge.lnk"), "edge");
        await File.WriteAllTextAsync(Path.Combine(bobDesktop, "Microsoft Edge.lnk"), "edge");
        await File.WriteAllTextAsync(Path.Combine(nestedDesktop, "Microsoft Edge.lnk"), "keep");

        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("fs.path", OperationAction.Remove,
                ("paths", new JsonArray("Users\\*\\Desktop\\Microsoft Edge.lnk"))), CancellationToken.None);

        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(File.Exists(Path.Combine(aliceDesktop, "Microsoft Edge.lnk"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(bobDesktop, "Microsoft Edge.lnk"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(nestedDesktop, "Microsoft Edge.lnk"))).IsTrue();
        await Assert.That(result.Changes).Count().IsEqualTo(2);
    }

    [Test]
    public async Task WildcardSkipsReparseDirectory() {
        var users = Path.Combine(_harness.MountPath, "Users");
        Directory.CreateDirectory(users);
        var link = Path.Combine(users, "Escaped");
        var outside = Path.Combine(Path.GetTempPath(), $"tinywin-fs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try {
            Directory.CreateSymbolicLink(link, outside);
            var diff = await _executer.InspectAsync(_harness.NewContext(),
                ExecuterTestHarness.Spec("fs.path", OperationAction.Remove,
                    ("paths", new JsonArray("Users\\*\\Desktop\\x.lnk"))), CancellationToken.None);
            await Assert.That(diff.Satisfied).IsTrue();
        }
        finally {
            Directory.Delete(link);
            Directory.Delete(outside);
        }
    }

    [Test]
    public async Task RejectsTraversalAndRootedPaths() {
        foreach (var unsafePath in new[] { "..\\escape", "C:\\Windows", "a/../../b", "Users\\..\\Desktop" }) {
            var ex = Assert.Throws<ExecException>(() =>
                FsPathExecuter.ResolveInsideMount(_harness.MountPath, unsafePath));
            await Assert.That(ex.Message).Contains("unsafe");
        }
    }

    [Test]
    public async Task RejectsWildcardsForCopyDestinations() {
        var ex = Assert.Throws<ExecException>(() =>
            FsPathExecuter.ResolveInsideMount(_harness.MountPath, "Users\\*\\Desktop"));
        await Assert.That(ex.Message).Contains("unsafe");
    }

    [Test]
    public async Task PresentDirectoryUsesRobocopy() {
        var assets = Path.Combine(_harness.MountPath, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "tools", "sub"));
        await File.WriteAllTextAsync(Path.Combine(assets, "tools", "app.exe"), "bin");
        await File.WriteAllTextAsync(Path.Combine(assets, "tools", "sub", "lib.dll"), "dll");
        var context = new ExecContext(_harness.MountPath, _harness.Log,
            new(_harness.MountPath, _harness.Runner), assets);
        _harness.Runner.Handler = (_, _) => FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(context,
            ExecuterTestHarness.Spec("fs.path", OperationAction.Apply,
                ("path", "ProgramData\\Tools"), ("source", "tools")), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(_harness.Runner.Called("robocopy.exe")).IsTrue();
        var copy = _harness.Runner.Calls.Last(c => c.File == "robocopy.exe");
        await Assert.That(copy.Args).Contains(Path.Combine(assets, "tools"));
        await Assert.That(copy.Args).Contains(Path.Combine(_harness.MountPath, "ProgramData", "Tools"));
        await Assert.That(copy.Args).Contains("/E");
    }

    [Test]
    public async Task PresentFileOverwritesChangedDestination() {
        var assets = Path.Combine(_harness.MountPath, "assets");
        Directory.CreateDirectory(assets);
        var source = Path.Combine(assets, "settings.ini");
        await File.WriteAllTextAsync(source, "new");
        var destination = Path.Combine(_harness.MountPath, "ProgramData", "settings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "old");
        var context = new ExecContext(_harness.MountPath, _harness.Log,
            new(_harness.MountPath, _harness.Runner), assets);
        var result = await _executer.ApplyAsync(context,
            ExecuterTestHarness.Spec("fs.path", OperationAction.Apply,
                ("path", "ProgramData\\settings.ini"), ("source", "settings.ini")), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(destination)).IsEqualTo("new");
    }

    [Test]
    public async Task PresentDirectoryDetectsMissingAssetFiles() {
        var assets = Path.Combine(_harness.MountPath, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "tools"));
        await File.WriteAllTextAsync(Path.Combine(assets, "tools", "required.dll"), "payload");
        Directory.CreateDirectory(Path.Combine(_harness.MountPath, "ProgramData", "Tools"));
        var context = new ExecContext(_harness.MountPath, _harness.Log,
            new(_harness.MountPath, _harness.Runner), assets);
        var diff = await _executer.InspectAsync(context,
            ExecuterTestHarness.Spec("fs.path", OperationAction.Apply,
                ("path", "ProgramData\\Tools"), ("source", "tools")), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences[0].Kind).IsEqualTo(ChangeKind.Modified);
    }

    [Test]
    public async Task RobocopyFailureIsReported() {
        var assets = Path.Combine(_harness.MountPath, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "tools"));
        var context = new ExecContext(_harness.MountPath, _harness.Log,
            new(_harness.MountPath, _harness.Runner), assets);
        _harness.Runner.Handler = (file, _) => file == "robocopy.exe"
            ? FakeProcessRunner.Fail(8)
            : FakeProcessRunner.Ok();
        var ex = Assert.Throws<IOException>(() =>
            _executer.ApplyAsync(context,
                    ExecuterTestHarness.Spec("fs.path", OperationAction.Apply,
                        ("path", "ProgramData\\Tools"), ("source", "tools")), CancellationToken.None)
                .GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("robocopy");
    }
}

public sealed class DriverStoreExecuterTests : IDisposable {
    private readonly DriverStoreExecuter _executer;
    private readonly ExecuterTestHarness _harness = new();

    public DriverStoreExecuterTests() {
        _executer = new(_harness.Runner);
    }

    public void Dispose() => _harness.Dispose();

    [Test]
    public async Task RemovesThirdPartyDriverThroughDism() {
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Drivers")
            ? FakeProcessRunner.Ok("Published Name : oem42.inf\r\n"
                                   + "Original File Name : mdm.inf\r\n"
                                   + "Inbox : No\r\n")
            : FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("driver.store", OperationAction.Remove,
                ("infNames", new JsonArray("mdm.inf"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        var remove = _harness.Runner.Calls.Last(c => c.Args.Contains("/Remove-Driver"));
        await Assert.That(remove.Args).Contains("/Driver:oem42.inf");
    }

    [Test]
    public async Task UnknownInfIsSkipped() {
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Drivers")
            ? FakeProcessRunner.Ok("Published Name : oem42.inf\r\n"
                                   + "Original File Name : usb.inf\r\n"
                                   + "Inbox : No\r\n")
            : FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("driver.store", OperationAction.Remove,
                ("infNames", new JsonArray("ghost.inf"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsTrue();
    }

    [Test]
    public async Task ApplyPreservesSkippedInfNamesWhenSomeDriversAreRemoved() {
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Drivers")
            ? FakeProcessRunner.Ok("Published Name : oem42.inf\r\n"
                                   + "Original File Name : mdm.inf\r\n"
                                   + "Inbox : No\r\n")
            : FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("driver.store", OperationAction.Remove,
                ("infNames", new JsonArray("mdm.inf", "ghost.inf"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(result.Changes.Count).IsEqualTo(2);
        await Assert.That(result.Changes.Count(c => c.Kind == ChangeKind.Removed)).IsEqualTo(1);
        await Assert.That(result.Changes.Count(c => c.Kind == ChangeKind.Skipped)).IsEqualTo(1);
        await Assert.That(_harness.Runner.Calls.Count(c => c.Args.Contains("/Remove-Driver"))).IsEqualTo(1);
    }

    [Test]
    public async Task InboxDriverIsSkippedWithoutDeletingFiles() {
        _harness.Runner.Handler = (_, args) => args.Contains("/Get-Drivers")
            ? FakeProcessRunner.Ok("Published Name : mdm.inf\r\n"
                                   + "Original File Name : mdm.inf\r\n"
                                   + "Inbox : Yes\r\n")
            : FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("driver.store", OperationAction.Remove,
                ("infNames", new JsonArray("mdm.inf"))), CancellationToken.None);
        await Assert.That(result.IsSkipped).IsTrue();
        await Assert.That(result.Changes[0].Before)
            .Contains("inbox driver packages cannot be removed by DISM");
        await Assert.That(_harness.Runner.Calls.Any(c => c.Args.Contains("/Remove-Driver"))).IsFalse();
    }

    [Test]
    public async Task InboxDriverRemovesServicesFoundThroughOwners() {
        _harness.CreateHiveFile("system");
        var packageDirectory = Path.Combine(_harness.MountPath, "Windows", "System32", "DriverStore",
            "FileRepository", "nvraid.inf_amd64_test");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "nvraid.inf"), "inf");
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "nvstor.sys"), "driver");
        var systemDrivers = Path.Combine(_harness.MountPath, "Windows", "System32", "drivers");
        Directory.CreateDirectory(systemDrivers);
        await File.WriteAllTextAsync(Path.Combine(systemDrivers, "nvstor.sys"), "driver");

        _harness.Runner.Handler = (_, args) => {
            if (args.Contains("/Get-Drivers")) {
                return FakeProcessRunner.Ok("Published Name : nvraid.inf\r\n"
                                           + "Original File Name : nvraid.inf\r\n"
                                           + "Inbox : Yes\r\n");
            }

            if (args[0] is "load" or "delete" or "unload") {
                return FakeProcessRunner.Ok();
            }

            if (args[0] != "query") {
                return FakeProcessRunner.Ok();
            }

            var key = args[1].ToString();
            if (key == "HKLM\\TinyWin2_system") {
                return FakeProcessRunner.Ok("HKEY_LOCAL_MACHINE\\TinyWin2_system\\ControlSet001\r\n");
            }

            if (key.EndsWith("\\ControlSet001\\Enum", StringComparison.OrdinalIgnoreCase)) {
                return FakeProcessRunner.Fail(1);
            }

            if (key.EndsWith("\\ControlSet001\\Services", StringComparison.OrdinalIgnoreCase)) {
                return FakeProcessRunner.Ok(
                    "HKEY_LOCAL_MACHINE\\TinyWin2_system\\ControlSet001\\Services\\nvstor\r\n");
            }

            if (key.EndsWith("\\ControlSet001\\Services\\nvstor", StringComparison.OrdinalIgnoreCase)) {
                return FakeProcessRunner.Ok("    ImagePath    REG_EXPAND_SZ    System32\\drivers\\nvstor.sys\r\n"
                                           + "    Owners    REG_MULTI_SZ    nvraid.inf\r\n");
            }

            return FakeProcessRunner.Fail(1);
        };

        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("driver.store", OperationAction.Remove,
                ("infNames", new JsonArray("nvraid.inf")), ("forceUnusedInbox", true)),
            CancellationToken.None);

        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(File.Exists(Path.Combine(systemDrivers, "nvstor.sys"))).IsFalse();
        await Assert.That(_harness.Runner.Calls.Any(call =>
            call.Args.Count >= 2
            && call.Args[0] == "delete"
            && call.Args[1].ToString().EndsWith("\\Services\\nvstor", StringComparison.OrdinalIgnoreCase)))
            .IsTrue();
    }

    [Test]
    public async Task NonRemoveActionIsRejectedBeforeInspectingStore() {
        var ex = Assert.Throws<ExecException>(() =>
            _executer.ApplyAsync(_harness.NewContext(),
                    ExecuterTestHarness.Spec("driver.store", OperationAction.Apply), CancellationToken.None)
                .GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("action 'remove'");
        await Assert.That(_harness.Runner.Calls).IsEmpty();
    }

    [Test]
    public async Task InvalidInfNameRejected() {
        foreach (var infName in new[] { "C:\\evil\\path.inf", "*.inf", "vendor?.inf" }) {
            var ex = Assert.Throws<ExecException>(() =>
                _executer.InspectAsync(_harness.NewContext(),
                        ExecuterTestHarness.Spec("driver.store", OperationAction.Remove,
                            ("infNames", new JsonArray(infName))), CancellationToken.None).GetAwaiter()
                    .GetResult());
            await Assert.That(ex.Message).Contains("invalid driver INF name");
        }
    }
}

public sealed class ExecuterRegistryTests {
    [Test]
    public async Task RegistersAllBuiltInResources() {
        var registry = new ExecuterRegistry(new FakeProcessRunner());
        foreach (var resource in new[] {
                     "registry.value", "registry.service", "dism.feature", "dism.capability",
                     "dism.package", "dism.component-store", "appx.provisioned", "driver.store", "fs.path"
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
