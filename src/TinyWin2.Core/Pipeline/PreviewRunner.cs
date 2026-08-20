using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Pipeline;

public sealed record PreviewOptions
{
    public required string SourcePath { get; init; }
    public required int ImageIndex { get; init; }
    public required IReadOnlyList<PlanSelection> Selections { get; init; }
    public required string WorkDirectory { get; init; }
    public required PlanCatalog Catalog { get; init; }
    public LayerGranularity Granularity { get; init; } = LayerGranularity.Group;
    public string? PlansDirectory { get; init; }
    public long BaseVhdxMaximumMb { get; init; } = 130_000;
}

public sealed record PlanPreview(
    string PlanId,
    string Title,
    bool Satisfied,
    IReadOnlyList<ChangeItem> Differences,
    IReadOnlyList<(string Resource, string SkipReason)> Notes)
{
    public JsonObject ToJson() => new()
    {
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
    BuildLog log)
{
    public async Task<IReadOnlyList<PlanPreview>> RunAsync(PreviewOptions options, CancellationToken ct)
    {
        var plan = BuildPlanResolver.Resolve(options.Catalog, options.Selections, options.Granularity);
        executers.ValidateBuildPlan(plan);
        log.Phase = "preview";
        log.Info($"preview: {plan.PlanIds.Count} plans resolved into {plan.Steps.Count} steps");

        var resolver = new SourceImageResolver(runner, log);
        var source = await resolver.ResolveAsync(options.SourcePath, ct);
        try
        {
            Directory.CreateDirectory(options.WorkDirectory);
            var stagingWim = Path.Combine(options.WorkDirectory, "install.source.wim");
            if (source.IsEsd)
            {
                await resolver.ExportIndexToWimAsync(source.InstallImagePath, options.ImageIndex, stagingWim, fast: true, ct);
            }
            else
            {
                File.Copy(source.InstallImagePath, stagingWim, overwrite: true);
            }

            var stack = VhdLayerStack.Load(options.WorkDirectory, layerBackend, log);
            await stack.EnsureBaseAsync(options.BaseVhdxMaximumMb, "TinyWin2-preview", ct);
            log.Info("applying source image into the preview base layer");
            await stack.ApplyImageToBaseAsync(async (mount, token) =>
            {
                await runner.RunAsync("dism.exe",
                    ["/Apply-Image", $"/ImageFile:{stagingWim}", $"/Index:{options.ImageIndex}", $"/ApplyDir:{mount}"],
                    new ProcessRunOptions { Timeout = TimeSpan.FromHours(2) }, token);
            }, ct);

            var letter = Layers.DiskPartVhdBackend.FreeDriveLetters().First(l => l is >= 'S' and <= 'Z');
            await layerBackend.AttachAsync(stack.BaseVhdxPath, letter.ToString(), ct);
            try
            {
                var previews = new List<PlanPreview>();
                var hiveCache = new Executers.Registry.RegistryHiveCache($"{letter}:\\", runner);
                var context = new ExecContext($"{letter}:\\", log, 0, fastMode: true) { Hives = hiveCache };
                foreach (var step in plan.Steps)
                {
                    foreach (var resolved in step.Plans)
                    {
                        var differences = new List<ChangeItem>();
                        var notes = new List<(string, string)>();
                        foreach (var exec in resolved.Execs)
                        {
                            var diff = await executers.Get(exec.Resource).InspectAsync(context, exec, ct);
                            differences.AddRange(diff.Differences.Where(d => d.Kind != ChangeKind.Skipped));
                            notes.AddRange(diff.Differences
                                .Where(d => d.Kind == ChangeKind.Skipped)
                                .Select(d => (exec.Resource, d.Before ?? "not present")));
                        }
                        previews.Add(new PlanPreview(
                            resolved.Definition.Id,
                            resolved.Definition.Title,
                            differences.Count == 0,
                            differences,
                            notes));
                    }
                }
                await hiveCache.UnloadAllAsync(log, ct);
                return previews;
            }
            finally
            {
                await layerBackend.DetachAsync(stack.BaseVhdxPath, ct);
            }
        }
        finally
        {
            await resolver.DismountIsoAsync(source, ct);
        }
    }
}
