using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Env;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Pipeline;

public enum OutputMode {
    Wim,
    Esd,
    Iso,
    IsoAndVhdx,
}

/// <summary>
/// Build phases exactly as they appear in the JSONL event stream and the GUI's routing.
/// Renaming a value here changes the wire contract — the GUI mirrors these strings.
/// </summary>
public static class BuildPhases {
    public const string Prepare = "prepare";
    public const string Media = "media";
    public const string BaseLayer = "base-layer";
    public const string Plan = "plan";
    public const string Capture = "capture";
    public const string Package = "package";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Preview = "preview";
    public const string Result = "result";
}

public sealed record BuildOptions {
    public const long DefaultBaseVhdxMaximumMb = 130_000;

    public required string SourcePath { get; init; }
    public required int ImageIndex { get; init; }
    public required IReadOnlyList<PlanSelection> Selections { get; init; }
    public required string OutputRoot { get; init; }
    public required PlanCatalog Catalog { get; init; }
    public OutputMode OutputMode { get; init; } = OutputMode.Iso;
    public LayerGranularity Granularity { get; init; } = LayerGranularity.Group;
    public bool Fast { get; init; }
    public bool ContinueOnError { get; init; }
    public bool KeepLayers { get; init; }
    public bool DryRun { get; init; }
    public string? OscdimgPath { get; init; }
    public string? PlansDirectory { get; init; }
    public long BaseVhdxMaximumMb { get; init; } = DefaultBaseVhdxMaximumMb;
}

public sealed record BuildResult {
    public required string BuildId { get; init; }
    public required string MediaPath { get; init; }
    public string? IsoPath { get; init; }
    public string? VhdxPath { get; init; }
    public required string InstallImagePath { get; init; }
    public required string ManifestPath { get; init; }
    public required int LayerCount { get; init; }
    public required bool Succeeded { get; init; }
    public string? FailedStepId { get; init; }
}

/// <summary>One plan step failed; its layer was discarded, so the chain stays consistent.</summary>
public sealed class BuildStepFailedException(
    string stepId,
    int layerIndex,
    Exception inner) : Exception($"plan step '{stepId}' failed at layer {layerIndex:000}: {inner.Message}", inner) {
    public string StepId { get; } = stepId;
    public int LayerIndex { get; } = layerIndex;
}

/// <summary>
/// Orchestrates a build: media prep → base layer (apply) → per-step VHDX diff layers
/// (atomic) → capture WIM/ESD → media rebuild → optional ISO/VHDX → manifest.
/// </summary>
public sealed class BuildEngine(
    IProcessRunner runner,
    ExecuterRegistry executers,
    ILayerBackend layerBackend,
    BuildLog log) {
    private const int ProgressAfterBase = 40;
    private const int ProgressPlanWeight = 40;
    private const int ProgressMedia = 10;
    private const int ProgressPackage = 90;
    private const int ProgressComplete = 100;

    public async Task<BuildResult> BuildAsync(BuildOptions options, CancellationToken ct) {
        var buildId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss");
        log.Phase = BuildPhases.Prepare;
        log.Info($"build {buildId} starting (granularity={options.Granularity}, out={options.OutputMode}, fast={options.Fast})");
        var plan = BuildPlanResolver.Resolve(options.Catalog, options.Selections, options.Granularity);
        executers.ValidateBuildPlan(plan);
        log.Info($"resolved {plan.PlanIds.Count} plans into {plan.Steps.Count} atomic steps");
        foreach (var step in plan.Steps) {
            log.Info($"  step {step.Id}: {step.Plans.Count} plan(s) [{string.Join(", ", step.Plans.Select(p => p.Definition.Id))}]");
        }
        if (options.DryRun) {
            log.Info("dry run: no mutations performed");
            return DryRunResult(buildId);
        }
        RunDoctor(options);
        var outputRoot = Path.GetFullPath(options.OutputRoot);
        var workspace = Path.Combine(outputRoot, "work", buildId);
        var mediaPath = Path.Combine(outputRoot, $"TinyWin2-{buildId}");
        var resolver = new SourceImageResolver(runner, log);
        var builder = new OutputBuilder(runner, log);
        SourceMedia? source = null;
        try {
            Directory.CreateDirectory(workspace);
            var (resolvedSource, stagingWim, sourceIndex) = await PrepareSourceAsync(options, workspace, resolver, ct);
            source = resolvedSource;
            var stack = await ApplyBaseAsync(options, buildId, workspace, stagingWim, sourceIndex, ct);
            var failedSteps = await RunStepsAsync(options, plan, stack, workspace, ct);
            var installPath = await CaptureInstallImageAsync(options, workspace, stack, builder, sourceIndex, ct);
            var (finalInstall, isoPath, vhdxPath) =
                await PackageOutputAsync(options, buildId, source, mediaPath, installPath, stack, builder, ct);
            var manifestPath = await WriteManifestAsync(buildId, options, plan, stack, mediaPath, finalInstall,
                isoPath, vhdxPath, sourceIndex, failedSteps, ct);
            log.Phase = BuildPhases.Done;
            log.Info($"build complete: {mediaPath}", data: new JsonObject { ["progress"] = ProgressComplete });
            if (isoPath is not null) {
                log.Info($"ISO: {isoPath}");
            }
            foreach (var (failedStepId, failedLayerIdx, _) in failedSteps) {
                log.Warn($"completed with skipped failed step '{failedStepId}' (layer {failedLayerIdx:000} discarded)");
            }
            if (!options.KeepLayers) {
                await TryDeleteDirectoryAsync(workspace);
            }
            return new BuildResult {
                BuildId = buildId,
                MediaPath = mediaPath,
                IsoPath = isoPath,
                VhdxPath = vhdxPath,
                InstallImagePath = finalInstall,
                ManifestPath = manifestPath,
                LayerCount = stack.CommittedDepth,
                Succeeded = true,
                FailedStepId = failedSteps.Count > 0 ? failedSteps[0].StepId : null,
            };
        }
        catch (Exception ex) {
            log.Phase = BuildPhases.Failed;
            log.Error($"build failed: {ex.Message}");
            // Keep the workspace on failure: the layer chain is the post-mortem data.
            throw;
        }
        finally {
            if (source is not null) {
                await resolver.DismountIsoAsync(source, ct);
            }
        }
    }

    /// <summary>Resolves the source, picks the image index, and stages it as a plain WIM.</summary>
    private async Task<(SourceMedia Source, string StagingWim, ImageIndexInfo SourceIndex)> PrepareSourceAsync(
        BuildOptions options, string workspace, SourceImageResolver resolver, CancellationToken ct) {
        log.Phase = BuildPhases.Media;
        var source = await resolver.ResolveAsync(options.SourcePath, ct);
        log.Info($"source media: {source.RootPath} ({(source.IsEsd ? "ESD" : "WIM")} install image)",
            data: new JsonObject { ["progress"] = ProgressMedia });
        var indexes = await resolver.GetIndexesAsync(source.InstallImagePath, ct);
        var sourceIndex = indexes.FirstOrDefault(i => i.Index == options.ImageIndex)
                          ?? throw new InvalidOperationException(
                              $"image index {options.ImageIndex} not found (available: {string.Join(", ", indexes.Select(i => i.Index))}).");
        var stagingWim = Path.Combine(workspace, "install.source.wim");
        if (source.IsEsd) {
            log.Info("source uses ESD; exporting selected index to WIM first");
        }
        await resolver.StageAsWimAsync(source, options.ImageIndex, stagingWim, options.Fast, ct);
        return (source, stagingWim, sourceIndex);
    }

    /// <summary>Creates (or reuses) the base layer and applies the staged image into it.</summary>
    private async Task<VhdLayerStack> ApplyBaseAsync(
        BuildOptions options, string buildId, string workspace, string stagingWim, ImageIndexInfo sourceIndex,
        CancellationToken ct) {
        log.Phase = BuildPhases.BaseLayer;
        var stack = VhdLayerStack.Load(workspace, layerBackend, log);
        await stack.EnsureBaseAsync(options.BaseVhdxMaximumMb, $"TinyWin2-{buildId}", ct);
        log.Info($"applying '{sourceIndex.Name}' (index {sourceIndex.Index}) into the base layer");
        await stack.ApplyImageToBaseAsync(async (mount, token) => {
            await runner.RunAsync("dism.exe",
                ["/English", "/Apply-Image", $"/ImageFile:{stagingWim}", $"/Index:{options.ImageIndex}", $"/ApplyDir:{mount}"],
                new ProcessRunOptions { Timeout = TimeSpan.FromHours(2) }, token);
            log.Info("capturing base-layer evidence snapshots (file manifest + registry)");
            await LayerEvidence.CaptureAsync(mount, workspace, 0, runner, log, token);
        }, ct);
        return stack;
    }

    /// <summary>Runs every plan step as one atomic layer; returns the failed ones (ContinueOnError).</summary>
    private async Task<List<(string StepId, int LayerIndex, string Error)>> RunStepsAsync(
        BuildOptions options, BuildPlan plan, VhdLayerStack stack, string workspace, CancellationToken ct) {
        log.Phase = BuildPhases.Plan;
        var failedSteps = new List<(string, int, string)>();
        var stepNumber = 0;
        foreach (var step in plan.Steps) {
            ct.ThrowIfCancellationRequested();
            stepNumber++;
            log.Info($"step {stepNumber}/{plan.Steps.Count}: '{step.Title}'",
                data: new JsonObject {
                    ["progress"] = ProgressAfterBase + (int)(ProgressPlanWeight * stepNumber / (double)plan.Steps.Count),
                });
            var session = await stack.BeginLayerAsync(step.Id, step.Title, null, ct);
            var execResults = new JsonArray();
            try {
                foreach (var resolved in step.Plans) {
                    await RunPlanInLayerAsync(resolved, session, execResults, options, ct);
                }
                if (!options.Fast) {
                    await CheckLayerHealthAsync(session, ct);
                }
                await LayerEvidence.CaptureAsync(session.MountPath, workspace, session.Record.Index, runner, log, ct);
                await stack.CommitLayerAsync(session, execResults, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                await stack.DiscardLayerAsync(session, ex.Message, ct);
                failedSteps.Add((step.Id, session.Record.Index, ex.Message));
                log.Error($"step '{step.Id}' failed and its layer was discarded: {ex.Message}", step.Id, session.Record.Index);
                if (!options.ContinueOnError) {
                    throw new BuildStepFailedException(step.Id, session.Record.Index, ex);
                }
            }
        }
        return failedSteps;
    }

    /// <summary>Captures the chain leaf into the install image (WIM directly; ESD via intermediate WIM).</summary>
    private async Task<string> CaptureInstallImageAsync(
        BuildOptions options, string workspace, VhdLayerStack stack, OutputBuilder builder, ImageIndexInfo sourceIndex,
        CancellationToken ct) {
        log.Phase = BuildPhases.Capture;
        var format = options.OutputMode switch {
            OutputMode.Wim => ImageFormat.Wim,
            _ => ImageFormat.Esd,
        };
        var leaf = stack.LeafVhdxPath;
        var letter = await layerBackend.AttachAsync(leaf, ct);
        try {
            if (format == ImageFormat.Esd) {
                var intermediate = Path.Combine(workspace, "install.intermediate.wim");
                await builder.CaptureAsync($"{letter}:\\", intermediate, sourceIndex.Name, sourceIndex.Description,
                    ImageFormat.Wim, options.Fast, ct);
                var esdPath = Path.Combine(workspace, "install.esd");
                await builder.ExportEsdAsync(intermediate, esdPath, ct);
                return esdPath;
            }
            var capturedWim = Path.Combine(workspace, "install.captured.wim");
            await builder.CaptureAsync($"{letter}:\\", capturedWim, sourceIndex.Name, sourceIndex.Description,
                ImageFormat.Wim, options.Fast, ct);
            return capturedWim;
        }
        finally {
            try { await layerBackend.DetachAsync(leaf, ct); } catch { /* already detached */ }
        }
    }

    /// <summary>Rebuilds the media folder and produces the ISO / merged-VHDX artifacts.</summary>
    private async Task<(string FinalInstall, string? IsoPath, string? VhdxPath)> PackageOutputAsync(
        BuildOptions options, string buildId, SourceMedia source, string mediaPath, string installPath,
        VhdLayerStack stack, OutputBuilder builder, CancellationToken ct) {
        log.Phase = BuildPhases.Package;
        log.Info("rebuilding installation media folder", data: new JsonObject { ["progress"] = ProgressPackage });
        var format = options.OutputMode switch {
            OutputMode.Wim => ImageFormat.Wim,
            _ => ImageFormat.Esd,
        };
        var finalInstall = await builder.RebuildMediaAsync(source.RootPath, mediaPath, installPath, format, ct);
        string? isoPath = null;
        if (options.OutputMode is OutputMode.Iso or OutputMode.IsoAndVhdx) {
            var oscdimg = options.OscdimgPath
                          ?? ToolLocator.Locate("oscdimg.exe")
                          ?? throw new FileNotFoundException("oscdimg.exe not found (pass --oscdimg or install Windows ADK).");
            isoPath = Path.Combine(Path.GetFullPath(options.OutputRoot), $"TinyWin2-{buildId}.iso");
            await builder.CreateIsoAsync(mediaPath, isoPath, oscdimg, ct);
        }
        string? vhdxPath = null;
        if (options.OutputMode == OutputMode.IsoAndVhdx) {
            vhdxPath = Path.Combine(Path.GetFullPath(options.OutputRoot), $"TinyWin2-{buildId}.vhdx");
            await stack.ExportMergedVhdxAsync(vhdxPath, ct);
        }
        return (finalInstall, isoPath, vhdxPath);
    }

    private async Task RunPlanInLayerAsync(
        ResolvedPlan resolved,
        LayerSession session,
        JsonArray execResults,
        BuildOptions options,
        CancellationToken ct) {
        var hiveCache = new RegistryHiveCache(session.MountPath, runner);
        hiveCache.SetSessionPrefix($"TinyWin2_L{session.Record.Index:D3}");
        var context = new ExecContext(
            session.MountPath,
            log,
            hiveCache,
            ResolveAssetsRoot(options.PlansDirectory, resolved.Definition.Id));
        log.PlanId = resolved.Definition.Id;
        try {
            foreach (var exec in resolved.Execs) {
                ct.ThrowIfCancellationRequested();
                var executer = executers.Get(exec.Resource);
                log.Debug($"exec {exec.Resource} ({exec.Ensure})", resolved.Definition.Id, session.Record.Index);
                var result = await executer.ApplyAsync(context, exec, ct);
                execResults.Add(new JsonObject {
                    ["planId"] = resolved.Definition.Id,
                    ["resource"] = exec.Resource,
                    ["ensure"] = exec.Ensure.ToString().ToLowerInvariant(),
                    ["status"] = result.Status.ToString().ToLowerInvariant(),
                    ["changes"] = new JsonArray(result.Changes.Select(c => (JsonNode)c.ToJson()).ToArray()),
                    ["skipReason"] = result.SkipReason,
                });
                if (result.Status == ExecStatus.Applied) {
                    log.Info($"{exec.Resource}: {result.Changes.Count} change(s)", resolved.Definition.Id, session.Record.Index);
                }
                else if (result.Status == ExecStatus.Skipped) {
                    log.Info($"{exec.Resource}: skipped ({result.SkipReason})", resolved.Definition.Id, session.Record.Index);
                }
            }
        }
        finally {
            log.PlanId = null;
            await hiveCache.UnloadAllAsync(log, ct);
        }
    }

    private async Task CheckLayerHealthAsync(LayerSession session, CancellationToken ct) {
        var result = await runner.RunAsync("dism.exe",
            [$"/Image:{session.MountPath}", "/Cleanup-Image", "/CheckHealth"],
            new ProcessRunOptions { IgnoreExitCode = true }, ct);
        if (result.ExitCode != 0) {
            throw new ExecException($"image health check failed after layer {session.Record.Index:000} (exit {result.ExitCode}).");
        }
    }

    private static string? ResolveAssetsRoot(string? plansDirectory, string planId) {
        if (plansDirectory is null) {
            return null;
        }
        var candidate = Path.Combine(plansDirectory, "assets", planId);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private async Task<string> WriteManifestAsync(
        string buildId,
        BuildOptions options,
        BuildPlan plan,
        VhdLayerStack stack,
        string mediaPath,
        string installPath,
        string? isoPath,
        string? vhdxPath,
        ImageIndexInfo sourceIndex,
        List<(string StepId, int LayerIndex, string Error)> failedSteps,
        CancellationToken ct) {
        log.Info("writing build manifest");
        var manifest = new JsonObject {
            ["schemaVersion"] = 2,
            ["tool"] = "TinyWin2",
            ["buildId"] = buildId,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sourcePath"] = Path.GetFullPath(options.SourcePath),
            ["sourceIndex"] = new JsonObject {
                ["index"] = sourceIndex.Index,
                ["name"] = sourceIndex.Name,
                ["editionId"] = sourceIndex.EditionId,
                ["version"] = sourceIndex.Version,
            },
            ["outputMode"] = options.OutputMode.ToString().ToLowerInvariant(),
            ["granularity"] = options.Granularity.ToString().ToLowerInvariant(),
            ["planIds"] = new JsonArray(plan.PlanIds.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
            ["mediaPath"] = mediaPath,
            ["installImage"] = new JsonObject {
                ["path"] = installPath,
                ["sha256"] = await OutputBuilder.ComputeSha256Async(installPath, ct),
                ["sizeBytes"] = new FileInfo(installPath).Length,
            },
            ["isoPath"] = isoPath,
            ["vhdxPath"] = vhdxPath,
            ["failedSteps"] = new JsonArray(failedSteps.Select(f => (JsonNode)new JsonObject {
                ["stepId"] = f.StepId,
                ["layerIndex"] = f.LayerIndex,
                ["error"] = f.Error,
            }).ToArray()),
            ["layers"] = new JsonArray(stack.Records.Select(r => (JsonNode)r.ToJson()).ToArray()),
        };
        var manifestPath = Path.Combine(mediaPath, "tinywin2-manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifest.ToPrettyString(), ct);
        return manifestPath;
    }

    private static BuildResult DryRunResult(string buildId) => new() {
        BuildId = buildId,
        MediaPath = "",
        InstallImagePath = "",
        ManifestPath = "",
        LayerCount = 0,
        Succeeded = true,
    };

    private void RunDoctor(BuildOptions options) {
        if (!OperatingSystem.IsWindows()) {
            throw new PlatformNotSupportedException("TinyWin2 builds are Windows-only (DISM/diskpart/VHDX).");
        }
        var failures = EnvironmentDoctor.Check(options.OutputRoot)
            .Where(c => c.Required && !c.Ok)
            .ToList();
        if (failures.Count > 0) {
            throw new InvalidOperationException(
                "environment checks failed: " + string.Join("; ", failures.Select(f => $"{f.Name}: {f.Detail}")));
        }
    }

    /// <summary>dism.exe can hold file handles briefly after exiting; retry before giving up.</summary>
    private static async Task TryDeleteDirectoryAsync(string path) {
        for (var attempt = 0; attempt < 3; attempt++) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (Exception ex) when (attempt < 2 && (ex is IOException or UnauthorizedAccessException)) {
                await Task.Delay(1000);
            }
            catch (Exception ex) {
                // Leftover work dirs are annoying but harmless; the next build uses a new id.
                Console.Error.WriteLine($"warning: could not clean workspace '{path}': {ex.Message}");
            }
        }
    }
}
