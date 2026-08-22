using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;
using TUnit.Core;

namespace TinyWin2.Core.Tests;

/// <summary>
/// Integration tests gated behind TINYWIN2_IT=1 (+ admin, <see cref="ItGateAttribute"/>).
/// They create real VHDX layers and run real dism/reg tooling, but every write stays inside
/// %TEMP% workspaces and the mounted image — the host system is never modified (ISO mounts
/// are dismounted in finally). TINYWIN2_TEST_ISO points at an ISO file or a media folder.
/// </summary>
[NotInParallel] // every test claims drive letters, diskpart and ISO mounts — global resources
public sealed class LayerBackendIntegrationTests : IDisposable {
    private readonly string _root = ItGateAttribute.CreateTestRoot();

    [Test]
    [ItGate]
    public async Task VhdCreateAttachWriteDetachRoundtrip() {
        var runner = new ProcessRunner();
        var backend = new DiskPartVhdBackend(runner);
        var directory = TestPlans.CreateTempDirectory();
        try {
            var baseVhdx = Path.Combine(directory, "base.vhdx");
            await backend.CreateBaseAsync(baseVhdx, 512, "tinywin2-it", CancellationToken.None);
            await Assert.That(File.Exists(baseVhdx)).IsTrue();
            var letter = await backend.AttachAsync(baseVhdx, CancellationToken.None);
            try {
                var probe = $"{letter}:\\probe.txt";
                await File.WriteAllTextAsync(probe, "hello");
                await Assert.That(File.Exists(probe)).IsTrue();
            }
            finally {
                await backend.DetachAsync(baseVhdx, CancellationToken.None);
            }

            // Differencing layer sees the base content.
            var diff = Path.Combine(directory, "L001.vhdx");
            await backend.CreateDiffAsync(diff, baseVhdx, CancellationToken.None);
            letter = await backend.AttachAsync(diff, CancellationToken.None);
            try {
                await Assert.That(File.Exists($"{letter}:\\probe.txt")).IsTrue();
                await File.WriteAllTextAsync($"{letter}:\\probe2.txt", "layer2");
            }
            finally {
                await backend.DetachAsync(diff, CancellationToken.None);
            }

            // Base must be untouched by the layer write.
            letter = await backend.AttachAsync(baseVhdx, CancellationToken.None);
            try {
                await Assert.That(File.Exists($"{letter}:\\probe.txt")).IsTrue();
                await Assert.That(File.Exists($"{letter}:\\probe2.txt")).IsFalse();
            }
            finally {
                await backend.DetachAsync(baseVhdx, CancellationToken.None);
            }
        }
        finally {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    [ItGate(RequiresTestSource = true)]
    public async Task MiniBuildAgainstSourceIndex1() {
        var sourcePath = ItGateAttribute.TestSource();
        var runner = new ProcessRunner();
        var executers = new ExecuterRegistry(runner);
        var backend = new DiskPartVhdBackend(runner);
        var outputRoot = Path.Combine(_root, "out");
        var plansDir = Path.Combine(_root, "plans");
        Directory.CreateDirectory(plansDir);
        WriteRegistryProbePlan(plansDir);
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var log = new BuildLog();
        using var logSink = log.UseSerilog(Path.Combine(_root, "it-mini.log"), echoConsole: true);
        var engine = new BuildEngine(runner, executers, backend, log);
        var result = await engine.BuildAsync(new BuildOptions {
            SourcePath = sourcePath,
            ImageIndex = 1,
            Selections = [new PlanSelection("it.registry-probe")],
            OutputRoot = outputRoot,
            Catalog = catalog,
            OutputFormat = OutputFormat.Wim,
            Fast = true, // skip the per-layer dism health check to keep the run light
            PlansDirectory = plansDir,
            BaseVhdxMaximumMb = 8_192,
        }, CancellationToken.None);
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.FailedStepId).IsNull();
        await Assert.That(File.Exists(result.OutputPath)).IsTrue();
    }

    [Test]
    [ItGate(RequiresTestSource = true)]
    public async Task ContinueOnErrorDiscardsTheFailedLayerAndCompletes() {
        var sourcePath = ItGateAttribute.TestSource();
        var runner = new ProcessRunner();
        var executers = new ExecuterRegistry(runner);
        var backend = new DiskPartVhdBackend(runner);
        var outputRoot = Path.Combine(_root, "out-continue");
        var plansDir = Path.Combine(_root, "plans-continue");
        Directory.CreateDirectory(plansDir);
        WriteRegistryProbePlan(plansDir);
        // The unsupported hive only fails once the step runs (options parse at exec time),
        // so the plan resolves fine and the failure is a genuine step-level event.
        TestPlans.WritePlan(plansDir, "it.registry-boom", o => {
            o["group"] = "BoomGroup";
            o["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "registry.value",
                ["ensure"] = "present",
                ["with"] = new JsonObject {
                    ["hive"] = "bogus",
                    ["key"] = "SOFTWARE\\X",
                    ["name"] = "N",
                    ["type"] = "dword",
                    ["data"] = 1,
                },
            });
        });
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var log = new BuildLog();
        using var logSink = log.UseSerilog(Path.Combine(_root, "it-continue.log"), echoConsole: true);
        var engine = new BuildEngine(runner, executers, backend, log);
        var result = await engine.BuildAsync(new BuildOptions {
            SourcePath = sourcePath,
            ImageIndex = 1,
            Selections = [new PlanSelection("it.registry-probe"), new PlanSelection("it.registry-boom")],
            OutputRoot = outputRoot,
            Catalog = catalog,
            OutputFormat = OutputFormat.Wim,
            Fast = true,
            ContinueOnError = true,
            PlansDirectory = plansDir,
            BaseVhdxMaximumMb = 8_192,
        }, CancellationToken.None);

        // The build completes; only the healthy step's layer survives in the chain.
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.FailedStepId).IsEqualTo("group:BoomGroup"); // group granularity steps carry the group id, not the plan id
        await Assert.That(result.LayerCount).IsEqualTo(2); // base + the one surviving step layer (boom discarded)
        await Assert.That(File.Exists(result.ManifestPath)).IsTrue();
    }

    [Test]
    [ItGate(RequiresTestSource = true)]
    public async Task PreviewReportsWhatEachPlanWouldChange() {
        var sourcePath = ItGateAttribute.TestSource();
        var runner = new ProcessRunner();
        var executers = new ExecuterRegistry(runner);
        var backend = new DiskPartVhdBackend(runner);
        var plansDir = Path.Combine(_root, "plans-preview");
        Directory.CreateDirectory(plansDir);
        WriteRegistryProbePlan(plansDir);
        TestPlans.WritePlan(plansDir, "it.fs-remove", o => {
            o["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "fs.path",
                ["ensure"] = "absent",
                ["with"] = new JsonObject { ["paths"] = new JsonArray("Windows/System32") },
            });
        });
        // fs.path-present needs the plan assets root: previews must resolve it exactly like builds.
        var payload = Path.Combine(plansDir, "assets", "it.fs-copy", "payload");
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(payload, "pinned.txt"), "pinned");
        TestPlans.WritePlan(plansDir, "it.fs-copy", o => {
            o["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "fs.path",
                ["ensure"] = "present",
                ["with"] = new JsonObject {
                    ["path"] = "TinyWin2/pinned.txt",
                    ["source"] = "payload/pinned.txt",
                },
            });
        });
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var log = new BuildLog();
        using var logSink = log.UseSerilog(Path.Combine(_root, "it-preview.log"), echoConsole: true);
        var previewer = new PreviewRunner(runner, executers, backend, log);
        var previews = await previewer.RunAsync(new PreviewOptions {
            SourcePath = sourcePath,
            ImageIndex = 1,
            Selections = [
                new PlanSelection("it.registry-probe"),
                new PlanSelection("it.fs-remove"),
                new PlanSelection("it.fs-copy"),
            ],
            WorkDirectory = Path.Combine(_root, "work", "preview"),
            Catalog = catalog,
            PlansDirectory = plansDir,
        }, CancellationToken.None);

        await Assert.That(previews).Count().IsEqualTo(3);
        var registry = previews.First(p => p.PlanId == "it.registry-probe");
        await Assert.That(registry.Satisfied).IsFalse();
        await Assert.That(registry.Differences.Count).IsGreaterThan(0);
        var fs = previews.First(p => p.PlanId == "it.fs-remove");
        await Assert.That(fs.Satisfied).IsFalse(); // Windows/System32 always exists in the applied image
        await Assert.That(fs.Differences.Count).IsGreaterThan(0);
        var copy = previews.First(p => p.PlanId == "it.fs-copy");
        await Assert.That(copy.Satisfied).IsFalse();
        await Assert.That(copy.Differences[0].Kind).IsEqualTo(ChangeKind.Created);
    }

    private static void WriteRegistryProbePlan(string plansDir) {
        TestPlans.WritePlan(plansDir, "it.registry-probe", o => {
            o["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "registry.value",
                ["ensure"] = "present",
                ["with"] = new JsonObject {
                    ["hive"] = "software",
                    ["key"] = "SOFTWARE\\TinyWin2IT",
                    ["name"] = "Probe",
                    ["type"] = "dword",
                    ["data"] = 42,
                },
            });
        });
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
