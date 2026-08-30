using TinyWin2.Core.Executers;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

/// <summary>Unit tests for both VHDX backends: everything native is answered by the fake runner.</summary>
public sealed class LayerBackendTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();
    private readonly FakeProcessRunner _runner = new();
    private readonly List<string> _scripts = [];

    public LayerBackendTests() {
        _runner.Handler = (file, args) => {
            // diskpart scripts are passed as /s <file>: capture the content before deletion.
            if (file == "diskpart.exe" && args.Count > 1 && args[0] == "/s") {
                _scripts.Add(File.ReadAllText(args[1]));
            }

            return FakeProcessRunner.Ok();
        };
    }

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        }
        catch {
            /* best effort */
        }
    }

    // ---- DiskPartVhdBackend ------------------------------------------------

    [Test]
    public async Task DiskPartCreateBaseScriptsCreateFormatAssignDetach() {
        var backend = new DiskPartVhdBackend(_runner);
        var vhdx = Path.Combine(_root, "base.vhdx");
        await backend.CreateBaseAsync(vhdx, 512, "tinywin2", CancellationToken.None);
        var script = _scripts[0];
        await Assert.That(script).Contains($"create vdisk file=\"{Path.GetFullPath(vhdx)}\" maximum=512");
        await Assert.That(script).Contains("format fs=ntfs label=\"tinywin2\" quick");
        await Assert.That(script).Contains("detach vdisk");
        await Assert.That(script.Contains('/')).IsFalse(); // diskpart rejects forward slashes
    }

    [Test]
    public async Task DiskPartCreateDiffRequiresExistingParent() {
        var backend = new DiskPartVhdBackend(_runner);
        var ex = Assert.Throws<FileNotFoundException>(() =>
            backend
                .CreateDiffAsync(
                    Path.Combine(_root, "L001.vhdx"),
                    Path.Combine(_root, "missing.vhdx"),
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult()
        );
        await Assert.That(ex.Message).Contains("differencing parent not found");
    }

    [Test]
    public async Task DiskPartCreateDiffScriptsParentChain() {
        var parent = Path.Combine(_root, "base.vhdx");
        await File.WriteAllTextAsync(parent, "vhd");
        var backend = new DiskPartVhdBackend(_runner);
        await backend.CreateDiffAsync(Path.Combine(_root, "L001.vhdx"), parent, CancellationToken.None);
        await Assert.That(_scripts[0]).Contains("create vdisk");
        await Assert.That(_scripts[0]).Contains($"parent=\"{Path.GetFullPath(parent)}\"");
    }

    [Test]
    public async Task DiskPartAttachRecoversFromAnAlreadyAttachedDisk() {
        var vhdx = Path.Combine(_root, "base.vhdx");
        await File.WriteAllTextAsync(vhdx, "vhd");
        var backend = new DiskPartVhdBackend(_runner);
        var attempts = 0;
        _runner.Handler = (file, args) => {
            if (file == "diskpart.exe" && args.Count > 1 && args[0] == "/s") {
                _scripts.Add(File.ReadAllText(args[1]));
            }

            return ++attempts == 1
                ? throw new ProcessRunnerException(
                    "diskpart.exe",
                    FakeProcessRunner.Fail(1, "the virtual disk is already attached")
                )
                : FakeProcessRunner.Ok();
        };

        var letter = await backend.AttachAsync(vhdx, CancellationToken.None);

        await Assert.That(attempts).IsEqualTo(3); // attach, detach, attach
        await Assert.That(_scripts[1]).Contains("detach vdisk");
        await Assert.That(_scripts[2]).Contains($"assign letter={letter}");
        await Assert.That(letter is >= 'S' and <= 'Z').IsTrue();
    }

    [Test]
    public async Task DiskPartScriptErrorsSurfaceAsIoException() {
        var backend = new DiskPartVhdBackend(_runner);
        _runner.Handler = (_, _) => FakeProcessRunner.Ok("diskpart has encountered an error");
        var ex = Assert.Throws<IOException>(() =>
            backend
                .CreateBaseAsync(Path.Combine(_root, "b.vhdx"), 512, "l", CancellationToken.None)
                .GetAwaiter()
                .GetResult()
        );
        await Assert.That(ex.Message).Contains("diskpart reported an error");
    }

    [Test]
    public async Task DiskPartAttachRequiresExistingVhdx() {
        var backend = new DiskPartVhdBackend(_runner);
        var ex = Assert.Throws<FileNotFoundException>(() =>
            backend.AttachAsync(Path.Combine(_root, "ghost.vhdx"), CancellationToken.None).GetAwaiter().GetResult()
        );
        await Assert.That(ex.Message).Contains("layer VHDX not found");
    }

    // ---- HyperVhdBackend ---------------------------------------------------

    [Test]
    public async Task HyperVhdAttachReusesExistingAttachmentWithoutDismounting() {
        var vhdx = Path.Combine(_root, "base.vhdx");
        await File.WriteAllTextAsync(vhdx, "vhd");
        var backend = new HyperVhdBackend(_runner);
        _runner.Handler = (_, _) => FakeProcessRunner.Ok("X");
        var letter = await backend.AttachAsync(vhdx, CancellationToken.None);
        await Assert.That(letter).IsEqualTo('X');
        var script = _runner.ArgsOf(0)[4];
        await Assert.That(script).Contains("Mount-VHD");
        await Assert.That(script).Contains("Get-DiskImage");
        await Assert.That(script).DoesNotContain("Dismount-VHD");
    }

    [Test]
    public async Task HyperVhdAttachWithoutDriveLetterThrows() {
        var vhdx = Path.Combine(_root, "base.vhdx");
        await File.WriteAllTextAsync(vhdx, "vhd");
        var backend = new HyperVhdBackend(_runner);
        _runner.Handler = (_, _) => FakeProcessRunner.Ok();
        var ex = Assert.Throws<IOException>(() =>
            backend.AttachAsync(vhdx, CancellationToken.None).GetAwaiter().GetResult()
        );
        await Assert.That(ex.Message).Contains("could not resolve its drive letter");
    }

    [Test]
    public async Task HyperVhdDetachLeavesPreExistingAttachmentAlone() {
        var vhdx = Path.Combine(_root, "base.vhdx");
        await File.WriteAllTextAsync(vhdx, "vhd");
        var backend = new HyperVhdBackend(_runner);
        _runner.Handler = (_, _) => FakeProcessRunner.Ok("tinywin2-existing|X");

        await backend.AttachAsync(vhdx, CancellationToken.None);
        await backend.DetachAsync(vhdx, CancellationToken.None);

        await Assert.That(_runner.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task HyperVhdDetachReleasesAnAttachmentCreatedByThisBackend() {
        var vhdx = Path.Combine(_root, "base.vhdx");
        await File.WriteAllTextAsync(vhdx, "vhd");
        var backend = new HyperVhdBackend(_runner);
        _runner.Handler = (_, args) =>
            args[4].Contains("tinywin2-owned", StringComparison.Ordinal)
                ? FakeProcessRunner.Ok("tinywin2-owned|X")
                : FakeProcessRunner.Ok();

        await backend.AttachAsync(vhdx, CancellationToken.None);
        await backend.DetachAsync(vhdx, CancellationToken.None);

        await Assert.That(_runner.Calls.Count).IsEqualTo(2);
        await Assert.That(_runner.ArgsOf(1)[4]).Contains("Dismount-VHD");
    }

    [Test]
    public async Task HyperVhdScriptsQuotePathsAndDoubleApostrophes() {
        var vhdx = Path.Combine(_root, "it's", "base.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(vhdx)!);
        await File.WriteAllTextAsync(vhdx, "vhd");
        var backend = new HyperVhdBackend(_runner);
        _runner.Handler = (_, _) => FakeProcessRunner.Ok("tinywin2-existing|X");
        await backend.AttachAsync(vhdx, CancellationToken.None);
        var script = _runner.ArgsOf(0)[4];
        await Assert.That(script).Contains("'" + vhdx.Replace("'", "''") + "'");
    }

    [Test]
    public async Task HyperVhdMergeWalksParentsWithoutUnsupportedDepthParameter() {
        var backend = new HyperVhdBackend(_runner);
        await backend.MergeAsync(Path.Combine(_root, "L002.vhdx"), 2, CancellationToken.None);
        await Assert.That(_runner.ArgsOf(0)[4]).Contains("Merge-VHD -Path");
        await Assert.That(_runner.ArgsOf(0)[4]).Contains("-DestinationPath $parent");
        await Assert.That(_runner.ArgsOf(0)[4]).Contains("$i -lt 2");
        await Assert.That(_runner.ArgsOf(0)[4]).DoesNotContain("-Depth");
    }

    [Test]
    public async Task HyperVhdCreateDiffRequiresExistingParent() {
        var backend = new HyperVhdBackend(_runner);
        var ex = Assert.Throws<FileNotFoundException>(() =>
            backend
                .CreateDiffAsync(
                    Path.Combine(_root, "L001.vhdx"),
                    Path.Combine(_root, "missing.vhdx"),
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult()
        );
        await Assert.That(ex.Message).Contains("differencing parent not found");
    }

    [Test]
    public async Task ChainDepthLimitsMatchBackendReliability() {
        await Assert.That(new DiskPartVhdBackend(_runner).MaxSafeChainDepth).IsEqualTo(30);
        await Assert.That(new HyperVhdBackend(_runner).MaxSafeChainDepth).IsEqualTo(int.MaxValue);
    }

    // ---- ExecuterRegistry validation ----------------------------------------

    [Test]
    public async Task HyperVhdAvailabilityProbeIsInjectableAndCached() {
        HyperVhdBackend.ResetAvailabilityCache();
        var probes = 0;
        var runner = new FakeProcessRunner {
            Handler = (_, _) => {
                probes++;
                return FakeProcessRunner.Ok("True");
            }
        };
        try {
            await Assert.That(HyperVhdBackend.IsAvailable(runner)).IsTrue();
            await Assert.That(HyperVhdBackend.IsAvailable(runner)).IsTrue();
            await Assert.That(probes).IsEqualTo(1); // second call served from the cache
        }
        finally {
            HyperVhdBackend.ResetAvailabilityCache();
        }
    }

    [Test]
    public async Task LoadRejectsCorruptLayerManifestsWithARecoveryHint() {
        var directory = TestPlans.CreateTempDirectory();
        try {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "layers.json"), "{ not json");
            var ex = Assert.Throws<IOException>(() =>
                VhdLayerStack.Load(directory, new FakeLayerBackend(), new())
            );
            await Assert.That(ex.Message).Contains("is corrupt");
            await Assert.That(ex.Message).Contains("delete the workspace");
        }
        finally {
            try {
                Directory.Delete(directory, true);
            }
            catch {
                /* best effort */
            }
        }
    }

    [Test]
    public async Task LoadRejectsLayerManifestWithoutLayersArray() {
        var directory = TestPlans.CreateTempDirectory();
        try {
            await File.WriteAllTextAsync(Path.Combine(directory, "layers.json"), "{\"version\":1}");
            var ex = Assert.Throws<IOException>(() =>
                VhdLayerStack.Load(directory, new FakeLayerBackend(), new())
            );
            await Assert.That(ex.Message).Contains("layers");
        }
        finally {
            try {
                Directory.Delete(directory, true);
            }
            catch {
                /* best effort */
            }
        }
    }

    [Test]
    public async Task LoadRejectsLayerManifestPathTraversalAndDuplicateIndexes() {
        var directory = TestPlans.CreateTempDirectory();
        try {
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            var baseLayer =
                $"{{\"index\":0,\"vhdx\":\"base.vhdx\",\"status\":\"committed\",\"startedUtc\":\"{timestamp}\"}}";
            var escaped =
                $"{{\"index\":1,\"vhdx\":\"..\\\\outside.vhdx\",\"status\":\"committed\",\"startedUtc\":\"{timestamp}\"}}";
            await File.WriteAllTextAsync(
                Path.Combine(directory, "layers.json"),
                $"{{\"layers\":[{baseLayer},{escaped}]}}"
            );
            var traversal = Assert.Throws<IOException>(() =>
                VhdLayerStack.Load(directory, new FakeLayerBackend(), new())
            );
            await Assert.That(traversal.Message).Contains("invalid VHDX");

            var layer =
                $"{{\"index\":1,\"vhdx\":\"L001.vhdx\",\"status\":\"discarded\",\"startedUtc\":\"{timestamp}\"}}";
            await File.WriteAllTextAsync(
                Path.Combine(directory, "layers.json"),
                $"{{\"layers\":[{baseLayer},{layer},{layer}]}}"
            );
            var duplicate = Assert.Throws<IOException>(() =>
                VhdLayerStack.Load(directory, new FakeLayerBackend(), new())
            );
            await Assert.That(duplicate.Message).Contains("unique");
        }
        finally {
            try {
                Directory.Delete(directory, true);
            }
            catch {
                /* best effort */
            }
        }
    }

    [Test]
    public async Task DuplicateRegistrationIsRejected() {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _ = new ExecuterRegistry([
                new FakeExecuter("dup.resource", false),
                new FakeExecuter("dup.resource", false)
            ])
        );
        await Assert.That(ex.Message).Contains("duplicate executer registration for resource 'dup.resource'");
    }

    [Test]
    public async Task ValidateBuildPlanRejectsUnknownResources() {
        var registry = new ExecuterRegistry([new FakeExecuter("known.resource", false)]);
        var plan = BuildPlan(
            new OperationSpec("known.resource", OperationAction.Remove, []),
            new OperationSpec("ghost.resource", OperationAction.Set, [])
        );
        var ex = Assert.Throws<ExecException>(() => registry.ValidateBuildPlan(plan));
        await Assert.That(ex.Message).Contains("no executer registered for resource 'ghost.resource'");
    }

    [Test]
    public void ValidateBuildPlanAcceptsFullyRegisteredPlans() {
        var registry = new ExecuterRegistry([new FakeExecuter("known.resource", false)]);
        var plan = BuildPlan(new OperationSpec("known.resource", OperationAction.Remove, []));
        registry.ValidateBuildPlan(plan);
    }

    private static BuildPlan BuildPlan(params OperationSpec[] execs) =>
        new(
            execs
                .Select((operation, index) => {
                    var definition = new PlanDefinition {
                        Id = $"p.{index}",
                        Version = "1.0.0",
                        Title = $"P {index}",
                        Description = "d",
                        Category = "G",
                        Operation = new(operation.Resource, operation.Action, operation.Spec)
                    };
                    return new PlanStep(new(definition, operation));
                }
                )
                .ToArray()
        );
}
