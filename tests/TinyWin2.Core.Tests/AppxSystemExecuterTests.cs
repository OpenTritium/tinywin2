using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Dism;

namespace TinyWin2.Core.Tests;

public sealed class AppxSystemExecuterTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();
    private readonly AppxSystemExecuter _executer;

    public AppxSystemExecuterTests() {
        _executer = new(_harness.Runner);
        _harness.CreateHiveFile("software");
    }

    public void Dispose() => _harness.Dispose();

    [Test]
    public async Task RemovesMatchingDirectoryAndDirectInboxRegistrations() {
        var directory = Path.Combine(_harness.MountPath, "Windows", "SystemApps",
            "Microsoft.Windows.AppRep.ChxApp_cw5n1h2txyewy");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "AppxManifest.xml"), "manifest");

        _harness.Runner.Handler = (_, args) => args.Contains("load")
            ? FakeProcessRunner.Ok()
            : args.Contains("/s")
                ? FakeProcessRunner.Ok("""
                    HKEY_LOCAL_MACHINE\TinyWin2_software\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\InboxApplications
                    HKEY_LOCAL_MACHINE\TinyWin2_software\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\InboxApplications\Microsoft.Windows.AppRep.ChxApp_cw5n1h2txyewy
                    HKEY_LOCAL_MACHINE\TinyWin2_software\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\InboxApplications\Microsoft.Windows.AppRep.ChxApp_cw5n1h2txyewy\Metadata
                    HKEY_LOCAL_MACHINE\TinyWin2_software\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\InboxApplications\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy
                    """)
                : FakeProcessRunner.Ok();

        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.system", OperationAction.Remove,
                ("patterns", new JsonArray("Microsoft.Windows.AppRep.ChxApp*"))), CancellationToken.None);

        await Assert.That(result.IsSkipped).IsFalse();
        await Assert.That(result.Changes).Count().IsEqualTo(2);
        await Assert.That(Directory.Exists(directory)).IsFalse();
        await Assert.That(_harness.Runner.Calls.Count(call => call.Args.Contains("delete"))).IsEqualTo(1);
        await Assert.That(string.Join(" ", _harness.Runner.Calls.Single(call => call.Args.Contains("delete")).Args))
            .Contains("Microsoft.Windows.AppRep.ChxApp_cw5n1h2txyewy");
        await Assert.That(_harness.Runner.Calls.Any(call => call.Args.Any(arg => arg.Contains("Metadata"))))
            .IsFalse();
    }

    [Test]
    public async Task RegistryOnlyMatchStillRequiresNoDirectory() {
        _harness.Runner.Handler = (_, args) => args.Contains("load")
            ? FakeProcessRunner.Ok()
            : args.Contains("/s")
                ? FakeProcessRunner.Ok("HKEY_LOCAL_MACHINE\\TinyWin2_software\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore\\Config\\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\r\n")
                : FakeProcessRunner.Ok();

        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.system", OperationAction.Remove,
                ("patterns", new JsonArray("MicrosoftWindows.Client.CBS*"))), CancellationToken.None);

        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences).Count().IsEqualTo(1);
        await Assert.That(diff.Differences[0].Target).Contains("software:");
    }

    [Test]
    public async Task NoMatchingSystemAppIsIdempotentlySkipped() {
        _harness.Runner.Handler = (_, args) => args.Contains("load")
            ? FakeProcessRunner.Ok()
            : args.Contains("/s")
                ? FakeProcessRunner.Ok("HKEY_LOCAL_MACHINE\\TinyWin2_software\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore\\InboxApplications\\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy\r\n")
                : FakeProcessRunner.Ok();

        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("appx.system", OperationAction.Remove,
                ("patterns", new JsonArray("Microsoft.Windows.AppRep.ChxApp*"))), CancellationToken.None);

        await Assert.That(result.IsSkipped).IsTrue();
        await Assert.That(_harness.Runner.Calls.Any(call => call.Args.Contains("delete"))).IsFalse();
    }
}
