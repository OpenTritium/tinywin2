using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class FakeLayerBackend : ILayerBackend {
    public List<string> Calls { get; } = [];
    public int MergeDepth { get; private set; }

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
        await Assert.That(stack.LeafVhdxPath).IsEqualTo(Path.Combine(_workDir, "base.vhdx"));
    }

    [Test]
    public async Task CommitLayerAdvancesLeafAndChain() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("group:Apps", "Applications", null, CancellationToken.None);
        await stack.CommitLayerAsync(session, [], CancellationToken.None);

        var second = await stack.BeginLayerAsync("group:Net", "Networking", null, CancellationToken.None);

        await Assert.That(session.Record.Index).IsEqualTo(1);
        await Assert.That(second.Record.Index).IsEqualTo(2);
        await Assert.That(_backend.Calls).Contains("create-diff:L001.vhdx<-base.vhdx");
        await Assert.That(_backend.Calls).Contains("create-diff:L002.vhdx<-L001.vhdx");
        // A pending (uncommitted) layer is not the leaf yet.
        await Assert.That(stack.LeafVhdxPath).EndsWith("L001.vhdx");
        await stack.CommitLayerAsync(second, [], CancellationToken.None);
        await Assert.That(stack.LeafVhdxPath).EndsWith("L002.vhdx");
    }

    [Test]
    public async Task DiscardLayerDeletesVhdxAndKeepsPreviousLeaf() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("step1", "S1", null, CancellationToken.None);
        await stack.DiscardLayerAsync(session, "boom", CancellationToken.None);

        await Assert.That(File.Exists(session.VhdxPath)).IsFalse();
        await Assert.That(stack.LeafVhdxPath).EndsWith("base.vhdx");
        await Assert.That(stack.Records[1].Status).IsEqualTo(LayerStatus.Discarded);
        await Assert.That(stack.Records[1].Error).IsEqualTo("boom");

        // the next layer starts again from the base with a fresh index
        var next = await stack.BeginLayerAsync("step2", "S2", null, CancellationToken.None);
        await Assert.That(_backend.Calls).Contains("create-diff:L002.vhdx<-base.vhdx");
        await Assert.That(next.Record.Index).IsEqualTo(2);
    }

    [Test]
    public async Task ManifestRoundTripsAcrossLoads() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("step1", "S1", new JsonObject { ["mode"] = "safe" }, CancellationToken.None);
        await stack.CommitLayerAsync(session, new JsonArray(), CancellationToken.None);
        stack.Save();

        var reloaded = Stack;

        await Assert.That(reloaded.Records.Count).IsEqualTo(2);
        await Assert.That(reloaded.Records[1].Status).IsEqualTo(LayerStatus.Committed);
        await Assert.That(reloaded.Records[1].BoundArgs!["mode"]!.GetValue<string>()).IsEqualTo("safe");
        await Assert.That(reloaded.LeafVhdxPath).EndsWith("L001.vhdx");
    }

    [Test]
    public async Task ConsolidateMergesWholeChainAndClearsDiffs() {
        var stack = await CreateWithBaseAsync();
        foreach (var title in new[] { "A", "B", "C" }) {
            var session = await stack.BeginLayerAsync(title, title, null, CancellationToken.None);
            await stack.CommitLayerAsync(session, [], CancellationToken.None);
        }

        await stack.ConsolidateAsync(CancellationToken.None);

        await Assert.That(_backend.MergeDepth).IsEqualTo(3);
        await Assert.That(File.Exists(Path.Combine(_workDir, "L001.vhdx"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(_workDir, "L003.vhdx"))).IsFalse();
        await Assert.That(File.Exists(stack.BaseVhdxPath)).IsTrue();
        await Assert.That(stack.Records.Count(r => r.Status == LayerStatus.Merged)).IsEqualTo(3);
        await Assert.That(stack.LeafVhdxPath).EndsWith("base.vhdx");
    }

    [Test]
    public async Task VhdxForLayerRejectsUncommitted() {
        var stack = await CreateWithBaseAsync();
        var session = await stack.BeginLayerAsync("step1", "S1", null, CancellationToken.None);
        await stack.DiscardLayerAsync(session, "x", CancellationToken.None);

        Assert.Throws<ArgumentException>(() => stack.VhdxForLayer(1));
    }

    public void Dispose() {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Fake executer registered under "test.noop" / "test.boom" for engine tests.</summary>
public sealed class FakeExecuter(string resource, bool fail) : IExecuter {
    public string Resource { get; } = resource;
    public int ApplyCount { get; private set; }

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) =>
        Task.FromResult(new ResourceDiff(false, [new ChangeItem(ChangeKind.Modified, Resource)]));

    public Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
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
            o["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "test.noop",
                ["ensure"] = "absent",
                ["with"] = new JsonObject(),
            });
        });
        return PlanCatalog.LoadDirectory(_plansDir);
    }

    [Test]
    public async Task DryRunResolvesAndTouchesNothing() {
        var runner = new FakeProcessRunner();
        var executers = new ExecuterRegistry([new FakeExecuter("test.noop", fail: false)]);
        var engine = new BuildEngine(runner, executers, new FakeLayerBackend(), new BuildLog { EchoConsole = false });

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
