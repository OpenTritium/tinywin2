using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Fs;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

/// <summary>
///     Resume reuses checkpoint layers byte-identically based on step fingerprints, so a
///     fingerprint must move whenever a referenced fs.path asset changes — including every
///     asset of a multi-copy step — while absent assets stay stable.
/// </summary>
public sealed class FingerprintAssetTests : IDisposable {
    private const string PlanId = "test.plan";

    private readonly string _root = TestPlans.CreateTempDirectory();

    public FingerprintAssetTests() {
        Directory.CreateDirectory(Path.Combine(PlansDirectory(), "assets", PlanId));
    }

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        }
        catch {
            /* best effort */
        }
    }

    [Test]
    public async Task AssetContentChangeMovesStepFingerprint() {
        await File.WriteAllTextAsync(AssetPath("asset.bin"), "payload-1");
        var before = await FingerprintAsync(CopyStep("asset.bin"));

        await File.WriteAllTextAsync(AssetPath("asset.bin"), "payload-2");
        var after = await FingerprintAsync(CopyStep("asset.bin"));

        await Assert.That(before).IsNotEqualTo(after);
    }

    [Test]
    public async Task UnchangedAssetKeepsStableFingerprint() {
        await File.WriteAllTextAsync(AssetPath("asset.bin"), "payload");
        var first = await FingerprintAsync(CopyStep("asset.bin"));
        var second = await FingerprintAsync(CopyStep("asset.bin"));

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task EachCopyAssetOfAStepMovesTheFingerprint() {
        await File.WriteAllTextAsync(AssetPath("first.bin"), "one");
        await File.WriteAllTextAsync(AssetPath("second.bin"), "two");
        var step = CopyStep("first.bin", "second.bin");
        var baseline = await FingerprintAsync(step);

        await File.WriteAllTextAsync(AssetPath("second.bin"), "two!");
        var afterSecondChanged = await FingerprintAsync(step);

        await Assert.That(baseline).IsNotEqualTo(afterSecondChanged);
    }

    [Test]
    public async Task TraversalAssetSourceFingerprintsLikeMissing() {
        await File.WriteAllTextAsync(AssetPath("asset.bin"), "payload");
        var traversal = await FingerprintAsync(CopyStep("..\\..\\escape.bin"));
        var traversalAgain = await FingerprintAsync(CopyStep("..\\..\\escape.bin"));

        // The traversal resolves nowhere, so no file content can enter the fingerprint:
        // it stays deterministic and unaffected by changes to real assets on disk.
        await File.WriteAllTextAsync(AssetPath("asset.bin"), "changed");
        var afterAssetChange = await FingerprintAsync(CopyStep("..\\..\\escape.bin"));

        // The fs-domain resolver rejects the escape outright.
        await Assert.That(FsPathAssets.ResolveAssetPath(
                Path.Combine(PlansDirectory(), "assets", PlanId), "..\\..\\escape.bin"))
            .IsNull();

        await Assert.That(traversal).IsEqualTo(traversalAgain);
        await Assert.That(traversal).IsEqualTo(afterAssetChange);
    }

    [Test]
    public async Task NonCopyOperationsCarryNoAsset() {
        var spec = new JsonObject { ["hive"] = "software", ["key"] = "K", ["name"] = "V" };
        var operation = new BoundOperation(new OperationSpec("registry.value", OperationAction.Apply, spec),
            new object());
        var definition = new PlanDefinition {
            Id = PlanId,
            Version = "1.0.0",
            Title = "test",
            Description = "test",
            Category = "test",
            Operations = [new PlanOperation(operation.Resource, operation.Action, operation.Spec.Spec)]
        };
        var step = new PlanStep(new ResolvedPlan(definition, [operation]));
        var before = await FingerprintAsync(step);

        await File.WriteAllTextAsync(AssetPath("asset.bin"), "irrelevant payload");
        var after = await FingerprintAsync(step);

        await Assert.That(before).IsEqualTo(after);
    }

    private string PlansDirectory() => Path.Combine(_root, "plans");

    private string AssetPath(string name) => Path.Combine(PlansDirectory(), "assets", PlanId, name);

    private static PlanStep CopyStep(params string[] sources) {
        var operations = sources.Select(source => {
            var spec = new OperationSpec("fs.path", OperationAction.Apply,
                new JsonObject { ["path"] = "destination", ["source"] = source });
            return new BoundOperation(spec, new object());
        }).ToList();
        var definition = new PlanDefinition {
            Id = PlanId,
            Version = "1.0.0",
            Title = "test",
            Description = "test",
            Category = "test",
            Operations = [.. operations.Select(o => new PlanOperation(o.Resource, o.Action, o.Spec.Spec))]
        };
        return new PlanStep(new ResolvedPlan(definition, operations));
    }

    /// <summary>Simulates a separate engine run: each fingerprint pass starts with an empty asset cache.</summary>
    private Task<string> FingerprintAsync(PlanStep step) =>
        TinyWin2.Core.Pipeline.BuildEngine.FingerprintAsync(step, PlansDirectory(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
}
