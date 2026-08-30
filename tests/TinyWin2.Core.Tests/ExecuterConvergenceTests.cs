using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using DismErrors = TinyWin2.Core.Executers.Dism.DismErrors;

namespace TinyWin2.Core.Tests;

public sealed class ExecuterTestHarness : IDisposable {
    public string MountPath { get; } = TestPlans.CreateTempDirectory();
    public FakeProcessRunner Runner { get; } = new();
    public BuildLog Log { get; } = new() { Phase = "test" };

    public void Dispose() {
        try {
            Directory.Delete(MountPath, true);
        }
        catch {
            /* best effort */
        }
    }

    public ExecContext NewContext() => new(MountPath, Log, new(MountPath, Runner));

    /// <summary>Creates the offline hive file the cache requires before loading.</summary>
    public void CreateHiveFile(string hiveId) {
        var relative = RegistryHiveCache.HiveFiles[hiveId];
        var path = Path.Combine(MountPath, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, "QFIB"u8); // arbitrary non-empty content
    }

    public static OperationSpec Spec(string resource, OperationAction action,
        params (string Key, JsonNode? Value)[] desired) {
        var obj = new JsonObject();
        foreach (var (key, value) in desired) {
            obj[key] = value?.DeepClone();
        }

        return new(resource, action, obj);
    }
}

public sealed class DismClassifierTests {
    [Test]
    public async Task ClassifiesKnownCodes() {
        await Assert.That(DismErrors.Classify(0, "")).IsEqualTo(DismOutcome.Success);
        await Assert.That(DismErrors.Classify(3010, "")).IsEqualTo(DismOutcome.SuccessRebootRequired);
        await Assert.That(DismErrors.Classify(4350, "")).IsEqualTo(DismOutcome.ComponentCleanupUnsupported);
        await Assert.That(DismErrors.Classify(50, "")).IsEqualTo(DismOutcome.ProviderUnavailable);
        await Assert.That(DismErrors.Classify(DismErrors.CbsEInvalidInstallState, ""))
            .IsEqualTo(DismOutcome.InvalidInstallState);
        await Assert.That(DismErrors.Classify(DismErrors.CbsECannotUninstall, ""))
            .IsEqualTo(DismOutcome.CannotUninstall);
        await Assert.That(DismErrors.Classify(DismErrors.CbsEInvalidPackage, ""))
            .IsEqualTo(DismOutcome.CannotUninstall);
        await Assert.That(DismErrors.Classify(2, "")).IsEqualTo(DismOutcome.Fatal);
    }

    [Test]
    public async Task FallsBackToTextHintsForProviderGaps() {
        await Assert.That(DismErrors.Classify(-1, "这个文件当前不能用于此计算机")).IsEqualTo(DismOutcome.ProviderUnavailable);
        await Assert.That(DismErrors.Classify(-1, "file cannot be used on this computer"))
            .IsEqualTo(DismOutcome.ProviderUnavailable);
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
        var records = DismListParser.Parse(output, "Feature Name");
        await Assert.That(records.Count).IsEqualTo(2);
        await Assert.That(DismListParser.Get(records[0], "State")).IsEqualTo("Enabled");
        await Assert.That(DismListParser.Get(records[1], "Feature Name")).IsEqualTo("NetFx3");
    }

    [Test]
    public async Task SuccessfulEmptyDismListingIsRejectedByExecuter() {
        using var harness = new ExecuterTestHarness();
        var executer = new FeatureExecuter(harness.Runner);
        harness.Runner.Handler = (_, _) => FakeProcessRunner.Ok("The operation completed successfully.\r\n");

        var context = harness.NewContext();
        var spec = ExecuterTestHarness.Spec("dism.feature", OperationAction.Remove,
            ("features", new JsonArray("Feature")));
        var ex = (await Assert.ThrowsAsync<ExecException>(() =>
            executer.InspectAsync(context, spec, CancellationToken.None)))!;
        await Assert.That(ex.Message).Contains("returned no dism.feature records");
    }
}

public sealed class RegistryValueExecuterTests : IDisposable {
    private readonly RegistryValueExecuter _executer;
    private readonly ExecuterTestHarness _harness = new();

    public RegistryValueExecuterTests() {
        _executer = new(_harness.Runner);
        _harness.CreateHiveFile("software");
    }

    public void Dispose() => _harness.Dispose();

    private static string QueryOutput(string valueName, string type, string data) =>
        $"\r\nHKEY_LOCAL_MACHINE\\TinyWin2_software\\Policies\\Test\r\n    {valueName}    {type}    {data}\r\n";

    [Test]
    public async Task InspectReportsCreatedWhenValueMissing() {
        _harness.Runner.Handler = (_, args) => args[0] == "query"
            ? FakeProcessRunner.Fail(1, "The system was unable to find the specified registry key or value.")
            : FakeProcessRunner.Ok();
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.value", OperationAction.Set,
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
            ExecuterTestHarness.Spec("registry.value", OperationAction.Set,
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
            ExecuterTestHarness.Spec("registry.value", OperationAction.Set,
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
            ExecuterTestHarness.Spec("registry.value", OperationAction.Remove,
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
        var spec = ExecuterTestHarness.Spec("registry.value", OperationAction.Set,
            ("hive", "software"), ("key", "K"), ("name", "Value"), ("type", "string"), ("data", "hello"));
        var first = await _executer.InspectAsync(_harness.NewContext(), spec, CancellationToken.None);
        await Assert.That(first.Satisfied).IsTrue();
        var result = await _executer.ApplyAsync(_harness.NewContext(), spec, CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Skipped);
        await Assert.That(_harness.Runner.Calls.Any(c => c.Args[0] == "add")).IsFalse();
    }

    [Test]
    public async Task MultiStringComparisonIgnoresRegQueryTerminator() =>
        await Assert.That(RegValues.Equals("REG_MULTI_SZ", "a\\0b\\0", "a\\0b")).IsTrue();

    [Test]
    public async Task BinaryDataRendersAndComparesIgnoringFormatting() {
        var rendered = RegValues.RenderData("REG_BINARY", JsonValue.Create("22 22:00-ff"));
        await Assert.That(rendered).IsEqualTo("222200FF");
        await Assert.That(RegValues.Equals("REG_BINARY", "22 22 00 ff", rendered)).IsTrue();
    }

    [Test]
    public async Task BinaryDataRejectsInvalidHex() {
        var ex = Assert.Throws<ExecException>(() =>
            RegValues.RenderData("REG_BINARY", JsonValue.Create("abc")));
        await Assert.That(ex.Message).Contains("even number of hexadecimal digits");
    }

    [Test]
    public async Task RegistryQueryAccessFailureIsNotTreatedAsMissing() {
        _harness.Runner.Handler = (_, args) => args[0] == "query"
            ? FakeProcessRunner.Fail(5, "Access is denied.")
            : FakeProcessRunner.Ok();
        var ex = Assert.Throws<ProcessRunnerException>(() =>
            _executer.InspectAsync(_harness.NewContext(),
                    ExecuterTestHarness.Spec("registry.value", OperationAction.Remove,
                        ("hive", "software"),
                        ("key", "Policies\\Test"),
                        ("name", "EnableSpyware")), CancellationToken.None)
                .GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("code 5");
    }
}

public sealed class RegistryServiceExecuterTests : IDisposable {
    private readonly RegistryServiceExecuter _executer;
    private readonly ExecuterTestHarness _harness = new();

    public RegistryServiceExecuterTests() {
        _executer = new(_harness.Runner);
        _harness.CreateHiveFile("system");
    }

    public void Dispose() => _harness.Dispose();

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

            if (args.Count == 4 && args[1] == hiveKey + "\\Select" && args[2] == "/v") {
                return FakeProcessRunner.Ok("\r\n    Current    REG_DWORD    0x1\r\n");
            }

            if (args.Count == 2 && args[1] == servicesRoot) {
                var lines = services.Select(s => $"{servicesRoot}\\{s.Name}");
                return FakeProcessRunner.Ok($"\r\n{servicesRoot}\r\n" + string.Join("\r\n", lines) + "\r\n");
            }

            if (args.Count >= 2 && args[1].ToString().Contains("\\TriggerInfo", StringComparison.OrdinalIgnoreCase)) {
                return FakeProcessRunner.Fail(1);
            }

            // /v Start or /v DelayedAutoStart on a service key
            var key = args[1];
            var valueName = args[3];
            var service = services.FirstOrDefault(s =>
                key.Equals($"{servicesRoot}\\{s.Name}", StringComparison.OrdinalIgnoreCase));
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
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
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
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("services", new JsonArray("Svc")), ("start", "disabled")), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsTrue();
    }

    [Test]
    public async Task DelayedAutoRequiresBothValues() {
        SetupServices(("Svc", "2", "1"));
        var satisfied = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("services", new JsonArray("Svc")), ("start", "delayedAuto")), CancellationToken.None);
        await Assert.That(satisfied.Satisfied).IsTrue();
        var notSatisfied = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("services", new JsonArray("Svc")), ("start", "auto")), CancellationToken.None);
        await Assert.That(notSatisfied.Satisfied).IsFalse();
    }

    [Test]
    public async Task PatternsMatchWildcardServiceNames() {
        SetupServices(("WpnUserService_12345", "2", null), ("Other", "2", null));
        var diff = await _executer.InspectAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("servicePatterns", new JsonArray("WpnUserService_*")), ("start", "manual")), CancellationToken.None);
        await Assert.That(diff.Satisfied).IsFalse();
        await Assert.That(diff.Differences.Count).IsEqualTo(1);
        await Assert.That(diff.Differences[0].Target).IsEqualTo("WpnUserService_12345");
    }

    [Test]
    public async Task TriggerStartWritesManualAndTriggerInfo() {
        SetupServices(("W32Time", "2", null));
        var hiveKey = "HKLM\\TinyWin2_system";
        var key32 = $"{hiveKey}\\ControlSet001\\Services\\W32Time";
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("services", new JsonArray("W32Time")), ("start", "trigger"),
                ("triggers", new JsonArray("domain-join", "device:{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}"))),
            CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        var adds = _harness.Runner.Calls.Where(c => c.Args[0] == "add").Select(c => string.Join(" ", c.Args)).ToList();
        await Assert.That(adds.Any(a => a.Contains($"{key32} /v Start") && a.Contains("/d 3"))).IsTrue(); // manual
        var trigger1 = $"{key32}\\TriggerInfo\\0";
        await Assert.That(adds.Any(a => a.Contains(trigger1) && a.Contains("/v Type") && a.Contains("/d 3"))).IsTrue();
        await Assert.That(adds.Any(a => a.Contains(trigger1) && a.Contains("/v Action") && a.Contains("/d 1")))
            .IsTrue();
        var trigger2 = $"{key32}\\TriggerInfo\\1";
        await Assert.That(adds.Any(a => a.Contains(trigger2) && a.Contains("/v GUID")
                                                             && a.Contains("0D63F553BFB6D01194F200A0C91EFB8B")))
            .IsTrue();
    }

    [Test]
    public async Task TriggerStartDoesNotSkipWhenManualServiceLacksTriggers() {
        SetupServices(("W32Time", "3", null));
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("services", new JsonArray("W32Time")), ("start", "trigger"),
                ("triggers", new JsonArray("domain-join"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(_harness.Runner.Calls.Any(c => c.Args[0] == "add" && c.Args.Contains("GUID"))).IsTrue();
    }

    [Test]
    public async Task TriggerStartSkipsWhenTriggerInfoMatches() {
        var hiveKey = "HKLM\\TinyWin2_system";
        var servicesRoot = $"{hiveKey}\\ControlSet001\\Services";
        var serviceKey = $"{servicesRoot}\\W32Time";
        var triggerRoot = $"{serviceKey}\\TriggerInfo";
        var triggerKey = $"{triggerRoot}\\0";
        _harness.Runner.Handler = (_, args) => {
            if (args[0] is "load" or "add" or "delete") {
                return FakeProcessRunner.Ok();
            }

            if (args[0] != "query") {
                return FakeProcessRunner.Ok();
            }

            if (args.Count == 4 && args[1] == hiveKey + "\\Select") {
                return FakeProcessRunner.Ok("\r\n    Current    REG_DWORD    0x1\r\n");
            }

            if (args.Count == 2 && args[1] == servicesRoot) {
                return FakeProcessRunner.Ok($"\r\n{servicesRoot}\r\n{serviceKey}\r\n");
            }

            if (args.Count >= 2 && args[1] == triggerRoot) {
                return FakeProcessRunner.Ok($"\r\n{triggerRoot}\r\n{triggerKey}\r\n");
            }

            if (args.Count == 2 && args[1] == triggerKey) {
                return FakeProcessRunner.Ok($"\r\n{triggerKey}\r\n"
                                            + "    Action    REG_DWORD    0x1\r\n"
                                            + "    GUID    REG_BINARY    BA0AE21C5198214494301DDEB766E809\r\n"
                                            + "    Type    REG_DWORD    0x3\r\n");
            }

            if (args.Count > 3 && args[1] == serviceKey && args[3] == "Start") {
                return FakeProcessRunner.Ok("\r\n    Start    REG_DWORD    0x3\r\n");
            }

            return FakeProcessRunner.Fail(1);
        };
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("services", new JsonArray("W32Time")), ("start", "trigger"),
                ("triggers", new JsonArray("domain-join"))), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Skipped);
        await Assert.That(_harness.Runner.Calls.Any(c => c.Args[0] is "add" or "delete")).IsFalse();
    }

    [Test]
    public async Task DeniedServiceKeyTakesOwnershipAndRetries() {
        // TrustedInstaller-owned keys (e.g. DPS) deny reg add; the rescue path must
        // grant ACLs via regini and retry the write.
        var hiveKey = "HKLM\\TinyWin2_system";
        var servicesRoot = $"{hiveKey}\\ControlSet001\\Services";
        var deniedKey = $"{servicesRoot}\\DPS";
        var startAdds = 0;
        _harness.Runner.Handler = (file, args) => {
            if (file == "regini.exe") {
                // Script must carry the NT-object path with the HKLM\ prefix stripped:
                // \Registry\Machine\TinyWin2_...\Services\DPS [1 17]. A stray HKLM\
                // makes real regini exit 1 ("Failed to load from file (87)").
                var script = File.ReadAllText(args[0]);
                var expected = "\\Registry\\Machine\\" + deniedKey["HKLM\\".Length..];
                if (!script.Contains(expected) || script.Contains("HKLM\\") || !script.Contains("[1 17]")) {
                    throw new InvalidOperationException("regini script malformed: " + script);
                }

                return FakeProcessRunner.Ok();
            }

            if (args[0] == "add" && args[1] == deniedKey && args.Contains("/v") && args.Contains("Start")) {
                return ++startAdds == 1
                    ? throw new ProcessRunnerException("reg.exe", FakeProcessRunner.Fail(1, "Access is denied."))
                    : FakeProcessRunner.Ok();
            }

            if (args[0] == "load" || args[0] == "add") {
                return FakeProcessRunner.Ok();
            }

            if (args[0] != "query") {
                return FakeProcessRunner.Ok();
            }

            if (args.Count == 4 && args[1] == hiveKey + "\\Select" && args[2] == "/v") {
                return FakeProcessRunner.Ok("\r\n    Current    REG_DWORD    0x1\r\n");
            }

            if (args.Count == 2 && args[1] == servicesRoot) {
                return FakeProcessRunner.Ok($"\r\n{servicesRoot}\r\n{deniedKey}\r\n");
            }

            if (args.Count >= 2 && args[1].ToString().Contains("\\TriggerInfo", StringComparison.OrdinalIgnoreCase)) {
                return FakeProcessRunner.Fail(1);
            }

            return args.Count > 3 && args[3] == "Start"
                ? FakeProcessRunner.Ok("\r\n    Start    REG_DWORD    0x2\r\n")
                : FakeProcessRunner.Fail(1);
        };
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("services", new JsonArray("DPS")), ("start", "disabled")), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(startAdds).IsEqualTo(2); // denied once, rescued, written
        await Assert.That(_harness.Runner.Called("regini.exe")).IsTrue();
    }

    [Test]
    public async Task MissingServicesAreSkippedNotFailed() {
        SetupServices(("Existing", "3", null));
        var result = await _executer.ApplyAsync(_harness.NewContext(),
            ExecuterTestHarness.Spec("registry.service", OperationAction.Configure,
                ("services", new JsonArray("Existing", "Ghost")), ("start", "disabled")), CancellationToken.None);
        await Assert.That(result.Status).IsEqualTo(ExecStatus.Applied);
        await Assert.That(result.Changes.Count).IsEqualTo(1);
        var adds = _harness.Runner.Calls.Where(c => c.Args[0] == "add").ToList();
        await Assert.That(adds.Count).IsEqualTo(2); // Start + DelayedAutoStart for one service
        await Assert.That(string.Join(" ", adds[0].Args)).Contains("/d 4");
    }
}
