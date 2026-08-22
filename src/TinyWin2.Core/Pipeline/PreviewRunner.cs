using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Pipeline;

public sealed record PreviewOptions {
    public required string SourcePath { get; init; }
    public required int ImageIndex { get; init; }
    public required IReadOnlyList<PlanSelection> Selections { get; init; }
    public required string WorkDirectory { get; init; }
    public required PlanCatalog Catalog { get; init; }
    public LayerGranularity Granularity { get; } = LayerGranularity.Group;
    /// <summary>Plans directory: fs.path-present previews need each plan's assets root.</summary>
    public string? PlansDirectory { get; init; }
    public long BaseVhdxMaximumMb { get; } = BuildOptions.DefaultBaseVhdxMaximumMb;
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
        ["differences"] = new JsonArray(Differences.Select(d => (JsonNode)d.ToJson()).ToArray()),
    };
}

/// <summary>
/// The v1 "Inspection phase": applies the base layer, then runs <c>InspectAsync</c> for
/// every exec (read-only) and reports what each plan would change. No plan layers, no output.
/// </summary>
public sealed class PreviewRunner(
    IProcessRunner runner,
    ExecuterRegistry executers,
    ILayerBackend layerBackend,
    BuildLog log) {
    public async Task<IReadOnlyList<PlanPreview>> RunAsync(PreviewOptions options, CancellationToken ct) {
        var plan = BuildPlanResolver.Resolve(options.Catalog, options.Selections, options.Granularity);
        executers.ValidateBuildPlan(plan);
        log.Phase = BuildPhases.Preview;
        log.Info($"preview: {plan.PlanIds.Count} plans resolved into {plan.Steps.Count} steps");
        var resolver = new SourceImageResolver(runner, log);
        var source = await resolver.ResolveAsync(options.SourcePath, ct);
        try {
            Directory.CreateDirectory(options.WorkDirectory);
            var stagingWim = Path.Combine(options.WorkDirectory, "install.source.wim");
            await resolver.StageAsWimAsync(source, options.ImageIndex, stagingWim, fast: true, ct);
            var stack = VhdLayerStack.Load(options.WorkDirectory, layerBackend, log);
            await stack.EnsureBaseAsync(options.BaseVhdxMaximumMb, "TinyWin2-preview", ct);
            log.Info("applying source image into the preview base layer");
            await stack.ApplyImageToBaseAsync(async (mount, token) => {
                await runner.RunAsync("dism.exe",
                    ["/Apply-Image", $"/ImageFile:{stagingWim}", $"/Index:{options.ImageIndex}", $"/ApplyDir:{mount}"],
                    new ProcessRunOptions { Timeout = TimeSpan.FromHours(2) }, token);
            }, ct);
            var letter = await layerBackend.AttachAsync(stack.BaseVhdxPath, ct);
            try {
                var previews = new List<PlanPreview>();
                foreach (var step in plan.Steps) {
                    foreach (var resolved in step.Plans) {
                        var hiveCache = new RegistryHiveCache($"{letter}:\\", runner);
                        var context = new ExecContext($"{letter}:\\", log, hiveCache,
                            ResolveAssetsRoot(options.PlansDirectory, resolved.Definition.Id));
                        var differences = new List<ChangeItem>();
                        try {
                            foreach (var exec in resolved.Execs) {
                                var diff = await executers.Get(exec.Resource).InspectAsync(context, exec, ct);
                                differences.AddRange(diff.Differences.Where(d => d.Kind != ChangeKind.Skipped));
                            }
                        }
                        finally {
                            await hiveCache.UnloadAllAsync(log, CancellationToken.None);
                        }
                        previews.Add(new PlanPreview(
                            resolved.Definition.Id,
                            resolved.Definition.Title,
                            differences.Count == 0,
                            differences));
                    }
                }
                return previews;
            }
            finally {
                await layerBackend.DetachAsync(stack.BaseVhdxPath, CancellationToken.None);
            }
        }
        finally {
            await resolver.DismountIsoAsync(source, CancellationToken.None);
        }
    }

    private static string? ResolveAssetsRoot(string? plansDirectory, string planId) {
        if (plansDirectory is null) {
            return null;
        }
        var candidate = Path.Combine(plansDirectory, "assets", planId);
        return Directory.Exists(candidate) ? candidate : null;
    }
}
