using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class FakeLayerBackend : ILayerBackend {
    public List<string> Calls { get; } = [];
    public int MergeDepth { get; private set; }
    public int MaxSafeChainDepth { get; set; } = 30;

    public Task CreateBaseAsync(string vhdxPath, long maximumMb, string volumeLabel, CancellationToken ct) {
        Calls.Add($"create-base:{Path.GetFileName(vhdxPath)}");
        Directory.CreateDirectory(Path.GetDirectoryName(vhdxPath)!);
        File.WriteAllText(vhdxPath, "base");
        return Task.CompletedTask;
    }

    public Task CreateDiffAsync(string diffPath, string parentPath, CancellationToken ct) {
        Calls.Add($"create-diff:{Path.GetFileName(diffPath)}<-{Path.GetFileName(parentPath)}");
        File.WriteAllText(diffPath, $"diff<-{Path.GetFileName(parentPath)}");
        return Task.CompletedTask;
    }

    public Task<char> AttachAsync(string vhdxPath, CancellationToken ct) {
        var letter = 'S';
        Calls.Add($"attach:{Path.GetFileName(vhdxPath)}:{letter}");
        return Task.FromResult(letter);
    }

    public Task DetachAsync(string vhdxPath, CancellationToken ct) {
        Calls.Add($"detach:{Path.GetFileName(vhdxPath)}");
        return Task.CompletedTask;
    }

    public Task MergeAsync(string vhdxPath, int depth, CancellationToken ct) {
        MergeDepth = depth;
        Calls.Add($"merge:{Path.GetFileName(vhdxPath)}:depth={depth}");
        return Task.CompletedTask;
    }
}

public sealed class VhdLayerStackTests : IDisposable {
    private readonly string _workDir = TestPlans.CreateTempDirectory();
    private readonly FakeLayerBackend _backend = new();
    private readonly BuildLog _log = new();
    private VhdLayerStack Stack => VhdLayerStack.Load(_workDir, _backend, _log);

    private async Task<VhdLayerStack> CreateWithBaseAsync() {
        var stack = Stack;
        await stack.EnsureBaseAsync(1000, "test", CancellationToken.None);
        return stack;
    }

    [Test]
    public async Task BaseLayerIsLayerZeroAndLeaf() {
        var stack = await CreateWithBaseAsync();
        await Assert.That(stack.Records.Count).IsEqualTo(1);
        await Assert.That(stack.Records[0].Index).IsEqualTo(0);
        await Assert.That(stack.Records[0].Status).IsEqualTo(LayerStatus.Committed);
        await Assert.That(stack.BaseReady).IsFalse();
        await Assert.That(stack.LeafVhdxPath).IsEqualTo(Path.Combine(_workDir, "base.vhdx"));
    }

    [Test]
    public async Task ApplyingBaseMarksItReadyAndPersistsTheMarker() {
        var stack = await CreateWithBaseAsync();
        await stack.ApplyImageToBaseAsync((_, _) => Task.CompletedTask, CancellationToken.None);
        await Assert.That(stack.BaseReady).IsTrue();

        var reloaded = Stack;
        await Assert.That(reloaded.BaseReady).IsTrue();
    }

    [Test]
    public async Task IncompleteBaseRecreationDropsDependentLayers() {
        var stack = await CreateWithBaseAsync();
        var layer = await stack.BeginLayerAsync("step1", "S1", CancellationToken.None);
        await stack.CommitLayerAsync(layer, null, CancellationToken.None);

        await stack.EnsureBaseAsync(1000, "test", CancellationToken.None);

        await Assert.That(stack.BaseReady).IsFalse();
        await Assert.That(stack.Records.Count).IsEqualTo(1);
        await Assert.That(stack.Records[0].Index).IsEqualTo(0);
        await Assert.That(File.Exists(layer.VhdxPath)).IsFalse();
    }

    [Test]
    public async Task CommitLayerAdvancesLeafAndChain() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("step-apps", "Applications", CancellationToken.None);
        await stack.CommitLayerAsync(session, null, CancellationToken.None);
        var second = await stack.BeginLayerAsync("step-network", "Networking", CancellationToken.None);
        await Assert.That(session.Record.Index).IsEqualTo(1);
        await Assert.That(second.Record.Index).IsEqualTo(2);
        await Assert.That(_backend.Calls).Contains("create-diff:L001.vhdx<-base.vhdx");
        await Assert.That(_backend.Calls).Contains("create-diff:L002.vhdx<-L001.vhdx");
        // A pending (uncommitted) layer is not the leaf yet.
        await Assert.That(stack.LeafVhdxPath).EndsWith("L001.vhdx");
        await stack.CommitLayerAsync(second, null, CancellationToken.None);
        await Assert.That(stack.LeafVhdxPath).EndsWith("L002.vhdx");
    }

    [Test]
    public async Task DiscardLayerDeletesVhdxAndKeepsPreviousLeaf() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("step1", "S1", CancellationToken.None);
        await stack.DiscardLayerAsync(session, "boom");
        await Assert.That(File.Exists(session.VhdxPath)).IsFalse();
        await Assert.That(stack.LeafVhdxPath).EndsWith("base.vhdx");
        await Assert.That(stack.Records[1].Status).IsEqualTo(LayerStatus.Discarded);
        await Assert.That(stack.Records[1].Error).IsEqualTo("boom");

        // the next layer starts again from the base with a fresh index
        var next = await stack.BeginLayerAsync("step2", "S2", CancellationToken.None);
        await Assert.That(_backend.Calls).Contains("create-diff:L002.vhdx<-base.vhdx");
        await Assert.That(next.Record.Index).IsEqualTo(2);
    }

    [Test]
    public async Task OperationResultRoundTripsAcrossLoads() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("step1", "S1", CancellationToken.None);
        await stack.CommitLayerAsync(session, new JsonObject { ["status"] = "applied" }, CancellationToken.None);
        stack.Save();
        var reloaded = Stack;
        await Assert.That(reloaded.Records.Count).IsEqualTo(2);
        await Assert.That(reloaded.Records[1].Status).IsEqualTo(LayerStatus.Committed);
        await Assert.That(reloaded.Records[1].OperationResult!["status"]!.GetValue<string>()).IsEqualTo("applied");
        await Assert.That(reloaded.LeafVhdxPath).EndsWith("L001.vhdx");
    }

    [Test]
    public async Task ConsolidateMergesWholeChainAndClearsDiffs() {
        var stack = await CreateWithBaseAsync();
        foreach (var title in new[] { "A", "B", "C" }) {
            var session = await stack.BeginLayerAsync(title, title, CancellationToken.None);
            await stack.CommitLayerAsync(session, null, CancellationToken.None);
        }
        await stack.ConsolidateAsync(CancellationToken.None);
        await Assert.That(_backend.MergeDepth).IsEqualTo(3);
        await Assert.That(stack.ConsolidationPending).IsFalse();
        await Assert.That(File.Exists(Path.Combine(_workDir, "L001.vhdx"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(_workDir, "L003.vhdx"))).IsFalse();
        await Assert.That(File.Exists(stack.BaseVhdxPath)).IsTrue();
        await Assert.That(stack.Records.Count(r => r.Status == LayerStatus.Merged)).IsEqualTo(3);
        await Assert.That(stack.LeafVhdxPath).EndsWith("base.vhdx");
    }

    [Test]
    public async Task TruncateRejectsMergedLayersBecauseBaseCannotRollBack() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("step1", "S1", CancellationToken.None);
        await stack.CommitLayerAsync(session, null, CancellationToken.None);
        await stack.ConsolidateAsync(CancellationToken.None);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            stack.TruncateToAsync(0, CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("merged");
    }

    [Test]
    public async Task SourceProvenanceRoundTripsAndRejectsChanges() {
        var stack = Stack;
        stack.InitializeOrValidateSource("source-a", 1);
        await Assert.That(stack.Records).IsEmpty();

        var reloaded = Stack;
        reloaded.InitializeOrValidateSource("source-a", 1);
        var ex = Assert.Throws<IOException>(() => reloaded.InitializeOrValidateSource("source-b", 1));
        await Assert.That(ex.Message).Contains("different source");
    }

    [Test]
    public async Task SecondConsolidationMergesOnlyLiveDiffs() {
        var stack = await CreateWithBaseAsync();
        foreach (var title in new[] { "A", "B", "C" }) {
            var session = await stack.BeginLayerAsync(title, title, CancellationToken.None);
            await stack.CommitLayerAsync(session, null, CancellationToken.None);
        }
        await stack.ConsolidateAsync(CancellationToken.None);
        var reborn = await stack.BeginLayerAsync("D", "D", CancellationToken.None);
        await stack.CommitLayerAsync(reborn, null, CancellationToken.None);
        await stack.ConsolidateAsync(CancellationToken.None);
        // merged-away layers must not inflate the second merge's depth (their files are gone)
        await Assert.That(_backend.MergeDepth).IsEqualTo(1);
        await Assert.That(File.Exists(reborn.VhdxPath)).IsFalse();
        await Assert.That(stack.LeafVhdxPath).EndsWith("base.vhdx");
    }

    [Test]
    public async Task DeepChainNeverMergesMidBuildWhenTheBackendSupportsIt() {
        _backend.MaxSafeChainDepth = int.MaxValue; // Hyper-V-shaped backend
        var stack = await CreateWithBaseAsync();
        for (var i = 0; i < 35; i++) { // past diskpart's limit — must not matter here
            var session = await stack.BeginLayerAsync($"s{i}", $"S{i}", CancellationToken.None);
            await stack.CommitLayerAsync(session, null, CancellationToken.None);
        }
        await Assert.That(_backend.Calls.Count(c => c.StartsWith("merge:"))).IsEqualTo(0);

        // Merging is export-time work: one merge pass when the artifact demands it.
        await stack.ExportMergedVhdxAsync(Path.Combine(_workDir, "merged.vhdx"), CancellationToken.None);
        await Assert.That(_backend.Calls.Count(c => c.StartsWith("merge:"))).IsEqualTo(1);
        await Assert.That(_backend.MergeDepth).IsEqualTo(35);
    }

    [Test]
    public async Task TruncateToDropsDivergedLayersAndKeepsThePrefix() {
        var stack = await CreateWithBaseAsync();
        var sessions = new List<LayerSession>();
        foreach (var title in new[] { "A", "B", "C" }) {
            var session = await stack.BeginLayerAsync(title, title, CancellationToken.None);
            await stack.CommitLayerAsync(session, null, CancellationToken.None);
            sessions.Add(session);
        }
        await stack.TruncateToAsync(sessions[0].Record.Index, CancellationToken.None);
        var records = stack.Records.Where(r => r.VhdxFileName != "base.vhdx").ToList();
        await Assert.That(records.Count).IsEqualTo(1);            // only layer A survives
        await Assert.That(File.Exists(sessions[1].VhdxPath)).IsFalse();
        await Assert.That(File.Exists(sessions[2].VhdxPath)).IsFalse();
        await Assert.That(stack.LeafVhdxPath).IsEqualTo(sessions[0].VhdxPath);
    }

    [Test]
    public async Task TruncateDoesNotReuseDeletedLayerIndexes() {
        var stack = await CreateWithBaseAsync();
        foreach (var title in new[] { "A", "B" }) {
            var session = await stack.BeginLayerAsync(title, title, CancellationToken.None);
            await stack.CommitLayerAsync(session, null, CancellationToken.None);
        }

        await stack.TruncateToAsync(1, CancellationToken.None);
        var next = await stack.BeginLayerAsync("C", "C", CancellationToken.None);

        await Assert.That(next.Record.Index).IsEqualTo(3);
        await Assert.That(next.VhdxPath).EndsWith("L003.vhdx");
        await stack.DiscardLayerAsync(next, "test cleanup");
    }

    [Test]
    public async Task AutoConsolidationGuardTracksLiveChainDepth() {
        var stack = await CreateWithBaseAsync();
        for (var i = 0; i < 31; i++) { // crosses the chain-depth threshold of 30
            var session = await stack.BeginLayerAsync($"s{i}", $"S{i}", CancellationToken.None);
            await stack.CommitLayerAsync(session, null, CancellationToken.None);
        }
        await Assert.That(_backend.Calls.Count(c => c.StartsWith("merge:"))).IsEqualTo(1);
        await Assert.That(_backend.MergeDepth).IsEqualTo(31);
        var after = await stack.BeginLayerAsync("after", "after", CancellationToken.None);
        await stack.CommitLayerAsync(after, null, CancellationToken.None);
        // the guard must reset after consolidation instead of firing on every later commit
        await Assert.That(_backend.Calls.Count(c => c.StartsWith("merge:"))).IsEqualTo(1);
    }

    [Test]
    public async Task VhdxForLayerRejectsUncommitted() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("step1", "S1", CancellationToken.None);
        await stack.DiscardLayerAsync(session, "x");
        Assert.Throws<ArgumentException>(() => stack.VhdxForLayer(1));
    }

    public void Dispose() {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Fake executer registered under "test.noop" / "test.boom" for engine tests.</summary>
public sealed class FakeExecuter(string resource, bool fail) : IExecuter {
    public string Resource { get; } = resource;
    private int ApplyCount { get; set; }

    public void Validate(OperationSpec spec) { }

    public Task<ResourceDiff> InspectAsync(ExecContext context, OperationSpec spec, CancellationToken ct) =>
        Task.FromResult(new ResourceDiff(false, [new ChangeItem(ChangeKind.Modified, Resource)]));

    public Task<ExecResult> ApplyAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        ApplyCount++;
        return Task.FromResult(fail
            ? throw new ExecException("boom from " + Resource)
            : ExecResult.Applied([new ChangeItem(ChangeKind.Modified, Resource)]));
    }
}

public sealed class BuildEngineDryRunTests : IDisposable {
    private readonly string _plansDir = TestPlans.CreateTempDirectory();

    private PlanCatalog Catalog() {
        TestPlans.WritePlan(_plansDir, "engine.sample", o => {
            o["operation"] = new JsonObject {
                ["resource"] = "test.noop",
                ["action"] = "remove",
                ["spec"] = new JsonObject(),
            };
        });
        return PlanCatalog.LoadDirectory(_plansDir);
    }

    [Test]
    public async Task NoLayersAppliesEverythingAgainstOneAttach() {
        TestPlans.WritePlan(_plansDir, "no.a", o => o["operation"] = new JsonObject {
            ["resource"] = "test.noop",
            ["action"] = "remove",
            ["spec"] = new JsonObject(),
        });
        TestPlans.WritePlan(_plansDir, "no.b", o => o["operation"] = new JsonObject {
            ["resource"] = "test.noop",
            ["action"] = "remove",
            ["spec"] = new JsonObject(),
        });
        var runner = new FakeProcessRunner {
            Handler = (_, args) => {
                if (args.Contains("/Get-WimInfo")) {
                    return FakeProcessRunner.Ok("Index : 1\r\nName : Fake Edition\r\n");
                }
                // fake dism capture/export: materialize the target file so packaging can move it
                var target = args.FirstOrDefault(a => a.StartsWith("/ImageFile:") || a.StartsWith("/DestinationImageFile:"));
                if (target is not null) {
                    var path = target.Split(':', 2)[1];
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    File.WriteAllText(path, "captured");
                }
                return FakeProcessRunner.Ok();
            },
        };
        var media = TestPlans.CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(media, "sources"));
        await File.WriteAllTextAsync(Path.Combine(media, "sources", "install.wim"), "wim");
        await File.WriteAllTextAsync(Path.Combine(media, "sources", "boot.wim"), "boot");
        var executers = new ExecuterRegistry([new FakeExecuter("test.noop", fail: false)]);
        var backend = new FakeLayerBackend();
        var engine = new BuildEngine(runner, executers, backend, new BuildLog());
        var result = await engine.BuildAsync(new BuildOptions {
            SourcePath = media,
            ImageIndex = 1,
            Selections = [new PlanSelection("no.a"), new PlanSelection("no.b")],
            OutputRoot = TestPlans.CreateTempDirectory(),
            Catalog = PlanCatalog.LoadDirectory(_plansDir),
            DryRun = false,
            NoLayers = true,
            OutputFormat = OutputFormat.Wim,
            SkipEnvironmentChecks = true,
        }, CancellationToken.None);
        await Assert.That(result.Succeeded).IsTrue();
        // layerless: no differencing layers at all; exactly two attaches (base image apply + the single working mount)
        await Assert.That(backend.Calls.Count(c => c.StartsWith("create-diff:"))).IsEqualTo(0);
        await Assert.That(backend.Calls.Count(c => c.StartsWith("attach:"))).IsEqualTo(2);
        var scanIndex = runner.Calls.FindIndex(c => c.Args.Contains("/ScanHealth"));
        var captureIndex = runner.Calls.FindIndex(c => c.Args.Contains("/Capture-Image"));
        await Assert.That(scanIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(captureIndex).IsGreaterThan(scanIndex);
        await Assert.That(runner.ArgsOf(scanIndex)).Contains("/English");
        await Assert.That(runner.ArgsOf(scanIndex)).Contains("/Image:S:\\");
    }

    [Test]
    public async Task VhdxOutputExportsOneDiskWithoutCapturingInstallImage() {
        TestPlans.WritePlan(_plansDir, "vhdx.noop", o => o["operation"] = new JsonObject {
            ["resource"] = "test.noop",
            ["action"] = "remove",
            ["spec"] = new JsonObject(),
        });
        var runner = new FakeProcessRunner {
            Handler = (_, args) => {
                if (args.Contains("/Get-WimInfo")) {
                    return FakeProcessRunner.Ok("Index : 1\r\nName : Fake Edition\r\n");
                }

                return FakeProcessRunner.Ok();
            },
        };
        var media = TestPlans.CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(media, "sources"));
        await File.WriteAllTextAsync(Path.Combine(media, "sources", "install.wim"), "wim");
        await File.WriteAllTextAsync(Path.Combine(media, "sources", "boot.wim"), "boot");
        var outputRoot = TestPlans.CreateTempDirectory();
        var backend = new FakeLayerBackend();
        var engine = new BuildEngine(
            runner,
            new ExecuterRegistry([new FakeExecuter("test.noop", fail: false)]),
            backend,
            new BuildLog());

        var result = await engine.BuildAsync(new BuildOptions {
            SourcePath = media,
            ImageIndex = 1,
            Selections = [new PlanSelection("vhdx.noop")],
            OutputRoot = outputRoot,
            Catalog = PlanCatalog.LoadDirectory(_plansDir),
            NoLayers = true,
            OutputFormat = OutputFormat.Vhdx,
            SkipEnvironmentChecks = true,
        }, CancellationToken.None);

        await Assert.That(result.OutputFormat).IsEqualTo(OutputFormat.Vhdx);
        await Assert.That(result.MediaPath).IsNull();
        await Assert.That(result.IsoPath).IsNull();
        await Assert.That(result.OutputPath).EndsWith(".vhdx");
        await Assert.That(File.Exists(result.OutputPath)).IsTrue();
        await Assert.That(File.Exists(result.ManifestPath)).IsTrue();
        await Assert.That(runner.Calls.Any(c => c.Args.Contains("/Capture-Image"))).IsFalse();
        await Assert.That(runner.Calls.Any(c => c.Args.Contains("/ScanHealth"))).IsTrue();
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(result.ManifestPath))!.AsObject();
        await Assert.That(manifest["outputFormat"]!.GetValue<string>()).IsEqualTo("vhdx");
        await Assert.That(manifest["output"]!["path"]!.GetValue<string>()).IsEqualTo(result.OutputPath);
        await Assert.That(manifest["output"]!["hashAlgorithm"]!.GetValue<string>()).IsEqualTo("xxh3");
        await Assert.That(manifest["output"]!["hash"]!.GetValue<string>()).Length().IsEqualTo(16);
        await Assert.That(manifest["installImage"]).IsNull();
    }

    [Test]
    public async Task DryRunResolvesAndTouchesNothing() {
        var runner = new FakeProcessRunner();
        var executers = new ExecuterRegistry([new FakeExecuter("test.noop", fail: false)]);
        var engine = new BuildEngine(runner, executers, new FakeLayerBackend(), new BuildLog());
        var result = await engine.BuildAsync(new BuildOptions {
            SourcePath = @"C:\does\not\exist.iso",
            ImageIndex = 1,
            Selections = [new PlanSelection("engine.sample")],
            OutputRoot = Path.GetTempPath(),
            Catalog = Catalog(),
            DryRun = true,
        }, CancellationToken.None);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
    }

    public void Dispose() {
        try { Directory.Delete(_plansDir, recursive: true); } catch { /* best effort */ }
    }
}
