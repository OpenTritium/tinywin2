using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Pipeline;

public sealed record PreviewOptions {
    public required string InputPath { get; init; }
    public required int ImageIndex { get; init; }
    public required IReadOnlyList<PlanSelection> Selections { get; init; }
    public required string WorkDirectory { get; init; }
    public required PlanCatalog Catalog { get; init; }

    /// <summary>Plans directory: fs.path-present previews need each plan's assets root.</summary>
    public string? PlansDirectory { get; init; }

    public long BaseVhdxMaximumMb { get; init; } = BuildOptions.DefaultBaseVhdxMaximumMb;
}

public sealed record PlanPreview(
    string PlanId,
    string Title,
    bool Satisfied,
    IReadOnlyList<ChangeItem> Differences) {
    public JsonObject ToJson() => new() {
        ["planId"] = PlanId,
        ["title"] = Title,
        ["alreadyInDesiredState"] = Satisfied,
        ["differences"] = new JsonArray(Differences.Select(d => (JsonNode)d.ToJson()).ToArray())
    };
}

/// <summary>
///     The inspection phase: applies the base layer, then runs <c>InspectAsync</c> for
///     every operation (read-only) and reports what each plan would change. No plan layers, no output.
/// </summary>
public sealed class PreviewRunner(
    IProcessRunner runner,
    ExecuterRegistry executers,
    ILayerBackend layerBackend,
    BuildLog log) {
    public async Task<IReadOnlyList<PlanPreview>> RunAsync(PreviewOptions options, CancellationToken ct) {
        if (options.ImageIndex < 1) {
            throw new ArgumentOutOfRangeException(nameof(options.ImageIndex), options.ImageIndex,
                "image index must be greater than zero.");
        }

        var plan = BuildPlanResolver.Resolve(options.Catalog, options.Selections);
        executers.ValidateBuildPlan(plan);
        log.Phase = BuildPhases.Preview;
        log.Info($"preview: {plan.PlanIds.Count} plans resolved into {plan.Steps.Count} steps");
        var resolver = new SourceImageResolver(runner, log);
        var source = await resolver.ResolveAsync(options.InputPath, ct);
        var previewCompleted = false;
        try {
            Directory.CreateDirectory(options.WorkDirectory);
            var stack = VhdLayerStack.Load(options.WorkDirectory, layerBackend, log);
            stack.InitializeOrValidateSource(SourceImageResolver.ComputeSourceFingerprint(source, options.ImageIndex),
                options.ImageIndex);
            await stack.EnsureBaseAsync(options.BaseVhdxMaximumMb, "TinyWin2-preview", ct);
            if (!stack.BaseReady) {
                var stagingWim = Path.Combine(options.WorkDirectory, "install.source.wim");
                await resolver.StageAsWimAsync(source, options.ImageIndex, stagingWim, WimCompression.Fast, false, ct);
                log.Info("applying source image into the preview base layer");
                await stack.ApplyImageToBaseAsync(async (mount, token) => {
                    await runner.RunAsync("dism.exe",
                        [
                            "/English",
                            "/Apply-Image", $"/ImageFile:{stagingWim}", $"/Index:{options.ImageIndex}",
                            $"/ApplyDir:{mount}"
                        ],
                        new() { Timeout = TimeSpan.FromHours(2) }, token);
                }, ct);
            }
            else {
                log.Info("preview: reusing the existing base layer");
            }

            var letter = await layerBackend.AttachAsync(stack.BaseVhdxPath, ct);
            var inspectionCompleted = false;
            try {
                var previews = new List<PlanPreview>();
                foreach (var step in plan.Steps) {
                    var resolved = step.Plan;
                    var hiveCache = new RegistryHiveCache($"{letter}:\\", runner);
                    var context = new ExecContext($"{letter}:\\", log, hiveCache,
                        PlanAssets.ResolveRoot(options.PlansDirectory, resolved.Definition.Id));
                    var differences = new List<ChangeItem>();
                    try {
                        foreach (var operation in resolved.Operations) {
                            var diff = await executers.Get(operation.Resource)
                                .InspectAsync(context, operation, ct);
                            differences.AddRange(diff.Differences.Where(d => d.Kind != ChangeKind.Skipped));
                        }
                    }
                    finally {
                        await hiveCache.UnloadAllAsync(log, CancellationToken.None);
                    }

                    previews.Add(new(
                        resolved.Definition.Id,
                        resolved.Definition.Title,
                        differences.Count == 0,
                        differences));
                }

                inspectionCompleted = true;
                previewCompleted = true;
                return previews;
            }
            finally {
                try {
                    await layerBackend.DetachAsync(stack.BaseVhdxPath, CancellationToken.None);
                }
                catch (Exception ex) when (!inspectionCompleted) {
                    log.Error($"preview base detach failed after inspection failure: {ex.Message}");
                }
            }
        }
        finally {
            try {
                await resolver.DismountIsoAsync(source, CancellationToken.None);
            }
            catch (Exception ex) {
                log.Error($"preview source ISO cleanup failed: {ex.Message}");
                if (previewCompleted) {
                    throw;
                }
            }
        }
    }
}
