using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Logging;

namespace TinyWin2.Core.Tests;

public sealed class ExecuterTestHarness : IDisposable {
    public string MountPath { get; } = TestPlans.CreateTempDirectory();
    public FakeProcessRunner Runner { get; } = new();
    public BuildLog Log { get; } = new() { Phase = "test" };

    public ExecContext NewContext() => new(MountPath, Log, new RegistryHiveCache(MountPath, Runner));

    /// <summary>Creates the offline hive file the cache requires before loading.</summary>
    public void CreateHiveFile(string hiveId = "software") {
        var relative = RegistryHiveCache.HiveFiles[hiveId];
        var path = Path.Combine(MountPath, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x51, 0x46, 0x49, 0x42]); // arbitrary non-empty content
    }

    public static ExecSpec Spec(string resource, Ensure ensure, params (string Key, JsonNode? Value)[] desired) {
        var obj = new JsonObject();
        foreach (var (key, value) in desired) {
            obj[key] = value?.DeepClone();
        }
        return new ExecSpec(resource, ensure, obj);
    }

    public void Dispose() {
        try { Directory.Delete(MountPath, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class DismClassifierTests {
    [Test]
    public async Task ClassifiesKnownCodes() {
        await Assert.That(DismErrors.Classify(0, "")).IsEqualTo(DismOutcome.Success);
        await Assert.That(DismErrors.Classify(3010, "")).IsEqualTo(DismOutcome.SuccessRebootRequired);
        await Assert.That(DismErrors.Classify(4350, "")).IsEqualTo(DismOutcome.ComponentCleanupUnsupported);
        await Assert.That(DismErrors.Classify(50, "")).IsEqualTo(DismOutcome.ProviderUnavailable);
        await Assert.That(DismErrors.Classify(DismErrors.CbsEInvalidInstallState, "")).IsEqualTo(DismOutcome.InvalidInstallState);
        await Assert.That(DismErrors.Classify(DismErrors.CbsECannotUninstall, "")).IsEqualTo(DismOutcome.CannotUninstall);
        await Assert.That(DismErrors.Classify(2, "")).IsEqualTo(DismOutcome.Fatal);
    }

    [Test]
    public async Task FallsBackToTextHintsForProviderGaps() {
        await Assert.That(DismErrors.Classify(-1, "这个文件当前不能用于此计算机")).IsEqualTo(DismOutcome.ProviderUnavailable);
        await Assert.That(DismErrors.Classify(-1, "file cannot be used on this computer")).IsEqualTo(DismOutcome.ProviderUnavailable);
    }

    [Test]
    public async Task ParsesFormatListBlocks() {
        const string output = """
            Deployment Image Servicing and Management tool
            Version: 10.0.1

            Feature Name : Microsoft-Hyper-V
            State : Enabled

            Feature Name : NetFx3
            State : Disabled

            """;
        var records = DismListParser.Parse(output);
        await Assert.That(records.Count).IsEqualTo(2);
        await Assert.That(DismListParser.Get(records[0], "State")).IsEqualTo("Enabled");
        await Assert.That(DismListParser.Get(records[1], "Feature Name")).IsEqualTo("NetFx3");
    }
}

public sealed class RegistryValueExecuterTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();
    private readonly RegistryValueExecuter _executer = new(new FakeProcessRunner());

    public RegistryValueExecuterTests() {
        _executer = new RegistryValueExecuter(_harness.Runner);
        _harness.CreateHiveFile("software");
    }

    private static string QueryOutput(string valueName, string type, string data) =>
        $"\r\nHKEY_LOCAL_MACHINE\\TinyWin2_software\\Policies\\Test\r\n    {valueName}    {type}    {data}\r\n";

    [Test]
    public async Task InspectReportsCreatedWhenValueMissing() {
        _harness.Runner.Handler = (_, args) => args[0] == "query"
            ? FakeProcessRunner.Fail(1, "The system was unable to find the specified registry key or value.")
            : FakeProcessRunner.Ok();
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.value", Ensure.Present,
                ("hive", "software"),
                ("key", "Policies\\Test"),
                ("name", "EnableSpyware"),
                ("type", "dword"),
                ("data", 1)), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences.Count).IsEqualTo(1);
        await Assert.That(diff.Differences[0].Kind).IsEqualTo(ChangeKind.Created);
    }

    [Test]
    public async Task InspectSatisfiedWhenValueMatches() {
        _harness.Runner.Handler = (_, args) => args[0] == "query"
            ? FakeProcessRunner.Ok(QueryOutput("EnableSpyware", "REG_DWORD", "0x1"))
            : FakeProcessRunner.Ok();
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.value", Ensure.Present,
                ("hive", "software"), ("key", "Policies\\Test"), ("name", "EnableSpyware"),
                ("type", "dword"), ("data", 1)), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }

    [Test]
    public async Task ApplyPresentIssuesRegAddWithRenderedData() {
        _harness.Runner.Handler = (_, args) => args[0] == "query"
            ? FakeProcessRunner.Fail(1)
            : FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.value", Ensure.Present,
                ("hive", "software"), ("key", "Policies\\Test"), ("name", "Value"),
                ("type", "multi"), ("data", new JsonArray("a", "b"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        var add = _harness.Runner.Calls.First(c => c.Args[0] == "add");
        await Assert.That(string.Join(" ", add.Args)).Contains("REG_MULTI_SZ");
        await Assert.That(string.Join(" ", add.Args)).Contains("a\\0b");
    }

    [Test]
    public async Task ApplyAbsentDeletesValueOrKey() {
        _harness.Runner.Handler = (_, args) => args[0] == "query"
            ? FakeProcessRunner.Ok(QueryOutput("EnableSpyware", "REG_DWORD", "0x1"))
            : FakeProcessRunner.Ok();
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.value", Ensure.Absent,
                ("hive", "software"),
                ("values", new JsonArray(new JsonObject { ["key"] = "Policies\\Test", ["name"] = "EnableSpyware" })),
                ("deleteKeys", new JsonArray("Policies\\Gone"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(result.Changes.Count).IsEqualTo(2);
        var deletes = _harness.Runner.Calls.Where(c => c.Args[0] == "delete").ToList();
        // one /v value delete + one whole-key delete
        await Assert.That(deletes.Count).IsEqualTo(2);
        await Assert.That(deletes.Count(d => d.Args.Contains("/f") && d.Args.Contains("/v"))).IsEqualTo(1);
    }

    [Test]
    public async Task IdempotentSecondApplySkips() {
        var existing = QueryOutput("Value", "REG_SZ", "hello");
        _harness.Runner.Handler = (_, args) => args[0] == "query"
            ? FakeProcessRunner.Ok(existing)
            : FakeProcessRunner.Ok();
        var spec = ExecuterTestHarness.Spec("registry.value", Ensure.Present,
            ("hive", "software"), ("key", "K"), ("name", "Value"), ("type", "string"), ("data", "hello"));
        var first = await _executer.InspectAsync(_harness.NewContext(), spec, CancellationToken.None);
        await Assert.That(first.Satisfied).IsTrue();
        var result = await _executer.ApplyAsync(_harness.NewContext(), spec, CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Skipped);
        await Assert.That(_harness.Runner.Calls.Any(c => c.Args[0] == "add")).IsFalse();
    }

    public void Dispose() => _harness.Dispose();
}

public sealed class RegistryServiceExecuterTests : IDisposable {
    private readonly ExecuterTestHarness _harness = new();
    private readonly RegistryServiceExecuter _executer;

    public RegistryServiceExecuterTests() {
        _executer = new RegistryServiceExecuter(_harness.Runner);
        _harness.CreateHiveFile("system");
    }

    private void SetupServices(params (string Name, string Start, string? Delayed)[] services) {
        var hiveKey = "HKLM\\TinyWin2_system";
        var servicesRoot = $"{hiveKey}\\ControlSet001\\Services";
        _harness.Runner.Handler = (_, args) => {
            if (args[0] == "load" || args[0] == "add") {
                return FakeProcessRunner.Ok();
            }
            if (args[0] != "query") {
                return FakeProcessRunner.Ok();
            }
            if (args.Count == 4 && args[1] == $"{hiveKey}\\Select" && args[2] == "/v") {
                return FakeProcessRunner.Ok($"\r\n    Current    REG_DWORD    0x1\r\n");
            }
            if (args.Count == 2 && args[1] == servicesRoot) {
                var lines = services.Select(s => $"{servicesRoot}\\{s.Name}");
                return FakeProcessRunner.Ok($"\r\n{servicesRoot}\r\n" + string.Join("\r\n", lines) + "\r\n");
            }
            // /v Start or /v DelayedAutoStart on a service key
            var key = args[1];
            var valueName = args[3];
            var service = services.FirstOrDefault(s => key.Equals($"{servicesRoot}\\{s.Name}", StringComparison.OrdinalIgnoreCase));
            if (service.Name is null) {
                return FakeProcessRunner.Fail(1);
            }
            var value = valueName == "Start" ? service.Start : service.Delayed;
            return value is null
                ? FakeProcessRunner.Fail(1)
                : FakeProcessRunner.Ok($"\r\n    {valueName}    REG_DWORD    0x{value}\r\n");
        };
    }

    [Test]
    public async Task DetectsManualServiceNeedingDisable() {
        SetupServices(("LanmanWorkstation", "3", null));
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", Ensure.Present,
                ("services", new JsonArray("LanmanWorkstation")), ("start", "disabled")), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences[0].Kind).IsEqualTo(ChangeKind.Modified);
        await Assert.That(diff.Differences[0].Before).IsEqualTo("manual");
        await Assert.That(diff.Differences[0].After).IsEqualTo("disabled");
    }

    [Test]
    public async Task SatisfiedWhenAlreadyInDesiredMode() {
        SetupServices(("Svc", "4", null));
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", Ensure.Present,
                ("services", new JsonArray("Svc")), ("start", "disabled")), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }

    [Test]
    public async Task DelayedAutoRequiresBothValues() {
        SetupServices(("Svc", "2", "1"));
        var satisfied = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", Ensure.Present,
                ("services", new JsonArray("Svc")), ("start", "delayedAuto")), CancellationToken.None);
        await Assert.That(satisfied.Satisfied).IsTrue();
        var notSatisfied = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", Ensure.Present,
                ("services", new JsonArray("Svc")), ("start", "auto")), CancellationToken.None);
        await Assert.That(notSatisfied.Satisfied).IsFalse();
    }

    [Test]
    public async Task PatternsMatchWildcardServiceNames() {
        SetupServices(("WpnUserService_12345", "2", null), ("Other", "2", null));
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", Ensure.Present,
                ("servicePatterns", new JsonArray("WpnUserService_*")), ("start", "manual")), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences.Count).IsEqualTo(1);
        await Assert.That(diff.Differences[0].Target).IsEqualTo("WpnUserService_12345");
    }

    [Test]
    public async Task MissingServicesAreSkippedNotFailed() {
        SetupServices(("Existing", "3", null));
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", Ensure.Present,
                ("services", new JsonArray("Existing", "Ghost")), ("start", "disabled")), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(result.Changes.Count).IsEqualTo(1);
        var adds = _harness.Runner.Calls.Where(c => c.Args[0] == "add").ToList();
        await Assert.That(adds.Count).IsEqualTo(2); // Start + DelayedAutoStart for one service
        await Assert.That(string.Join(" ", adds[0].Args)).Contains("/d 4");
    }

    public void Dispose() => _harness.Dispose();
}
