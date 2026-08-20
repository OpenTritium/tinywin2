using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
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

public sealed record BuildOptions {
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
    public long BaseVhdxMaximumMb { get; init; } = 130_000;
}

public sealed record BuildResult {
    public required string BuildId { get; init; }
    public required string WorkspacePath { get; init; }
    public required string MediaPath { get; init; }
    public string? IsoPath { get; init; }
    public string? VhdxPath { get; init; }
    public required string InstallImagePath { get; init; }
    public required string ManifestPath { get; init; }
    public required int LayerCount { get; init; }
    public required bool Succeeded { get; init; }
    public string? FailedStepId { get; init; }
    public int? FailedLayerIndex { get; init; }
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
    public BuildLog Log => log;

    public async Task<BuildResult> BuildAsync(BuildOptions options, CancellationToken ct) {
        var buildId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss");
        log.Phase = "prepare";
        log.Info($"build {buildId} starting (granularity={options.Granularity}, out={options.OutputMode}, fast={options.Fast})");
        var plan = BuildPlanResolver.Resolve(options.Catalog, options.Selections, options.Granularity);
        executers.ValidateBuildPlan(plan);
        log.Info($"resolved {plan.PlanIds.Count} plans into {plan.Steps.Count} atomic steps");
        foreach (var step in plan.Steps) {
            log.Info($"  step {step.Id}: {step.Plans.Count} plan(s) [{string.Join(", ", step.Plans.Select(p => p.Definition.Id))}]");
        }
        if (options.DryRun) {
            log.Info("dry run: no mutations performed");
            return DryRunResult(buildId, options);
        }
        RunDoctor(options);
        var outputRoot = Path.GetFullPath(options.OutputRoot);
        var workspace = Path.Combine(outputRoot, "work", buildId);
        var mediaPath = Path.Combine(outputRoot, $"TinyWin2-{buildId}");
        var resolver = new SourceImageResolver(runner, log);
        var builder = new OutputBuilder(runner, log);
        SourceMedia? source = null;
        var failedSteps = new List<(string StepId, int LayerIndex, string Error)>();
        try {
            Directory.CreateDirectory(workspace);
            source = await resolver.ResolveAsync(options.SourcePath, ct);
            log.Phase = "media";
            log.Info($"source media: {source.RootPath} ({(source.IsEsd ? "ESD" : "WIM")} install image)", data: new JsonObject { ["progress"] = 10 });
            var indexes = await resolver.GetIndexesAsync(source.InstallImagePath, ct);
            var sourceIndex = indexes.FirstOrDefault(i => i.Index == options.ImageIndex)
                              ?? throw new InvalidOperationException(
                                  $"image index {options.ImageIndex} not found (available: {string.Join(", ", indexes.Select(i => i.Index))}).");

            // Stage the selected index as a plain WIM (ESD sources get exported first).
            var stagingWim = Path.Combine(workspace, "install.source.wim");
            if (source.IsEsd) {
                log.Info("source uses ESD; exporting selected index to WIM first");
                await resolver.ExportIndexToWimAsync(source.InstallImagePath, options.ImageIndex, stagingWim, options.Fast, ct);
            }
            else {
                File.Copy(source.InstallImagePath, stagingWim, overwrite: true);
            }
            log.Phase = "base-layer";
            var stack = VhdLayerStack.Load(workspace, layerBackend, log);
            await stack.EnsureBaseAsync(options.BaseVhdxMaximumMb, $"TinyWin2-{buildId}", ct);
            log.Info($"applying '{sourceIndex.Name}' (index {sourceIndex.Index}) into the base layer");
            await stack.ApplyImageToBaseAsync(async (mount, token) => {
                await runner.RunAsync("dism.exe",
                    ["/English", "/Apply-Image", $"/ImageFile:{stagingWim}", $"/Index:{options.ImageIndex}", $"/ApplyDir:{mount}"],
                    new ProcessRunOptions { Timeout = TimeSpan.FromHours(2) }, token);
                log.Info("capturing base-layer evidence snapshots (file manifest + registry)");
                await Layers.LayerEvidence.CaptureAsync(mount, workspace, 0, runner, log, token);
            }, ct);
            log.Phase = "plan";
            var stepNumber = 0;
            foreach (var step in plan.Steps) {
                ct.ThrowIfCancellationRequested();
                stepNumber++;
                log.Info($"step {stepNumber}/{plan.Steps.Count}: '{step.Title}'",
                    data: new JsonObject { ["progress"] = ProgressAfterBase + (int)(PlanWeight * stepNumber / (double)plan.Steps.Count) });
                var session = await stack.BeginLayerAsync(step.Id, step.Title, null, ct);
                var execResults = new JsonArray();
                try {
                    foreach (var resolved in step.Plans) {
                        await RunPlanInLayerAsync(resolved, session, execResults, workspace, options, ct);
                    }
                    if (!options.Fast) {
                        await CheckLayerHealthAsync(session, ct);
                    }
                    await Layers.LayerEvidence.CaptureAsync(session.MountPath, workspace, session.Record.Index, runner, log, ct);
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
            log.Phase = "capture";
            var format = options.OutputMode switch {
                OutputMode.Wim => ImageFormat.Wim,
                _ => ImageFormat.Esd,
            };
            var capturedWim = Path.Combine(workspace, "install.captured.wim");
            var leaf = stack.LeafVhdxPath;
            var letter = await layerBackend.AttachAsync(leaf, ct);
            string installPath;
            try {
                if (format == ImageFormat.Esd) {
                    // ESD (LZMS) capture goes through an intermediate WIM export for reliability.
                    var intermediate = Path.Combine(workspace, "install.intermediate.wim");
                    await builder.CaptureAsync($"{letter}:\\", intermediate, sourceIndex.Name, sourceIndex.Description, ImageFormat.Wim, options.Fast, ct);
                    await layerBackend.DetachAsync(leaf, ct);
                    installPath = Path.Combine(workspace, "install.esd");
                    await runner.RunAsync("dism.exe",
                        ["/English", "/Export-Image", $"/SourceImageFile:{intermediate}", "/SourceIndex:1",
                         $"/DestinationImageFile:{installPath}", "/Compress:recovery"],
                        new ProcessRunOptions { Timeout = TimeSpan.FromHours(3) }, ct);
                }
                else {
                    await builder.CaptureAsync($"{letter}:\\", capturedWim, sourceIndex.Name, sourceIndex.Description, ImageFormat.Wim, options.Fast, ct);
                    await layerBackend.DetachAsync(leaf, ct);
                    installPath = capturedWim;
                }
            }
            finally {
                try { await layerBackend.DetachAsync(leaf, ct); } catch { /* already detached */ }
            }
            log.Phase = "package";
            log.Info("rebuilding installation media folder", data: new JsonObject { ["progress"] = 90 });
            var finalInstall = await builder.RebuildMediaAsync(source.RootPath, mediaPath, installPath, format, ct);
            string? isoPath = null;
            if (options.OutputMode is OutputMode.Iso or OutputMode.IsoAndVhdx) {
                var oscdimg = options.OscdimgPath
                              ?? ToolLocator.Locate("oscdimg.exe")
                              ?? throw new FileNotFoundException("oscdimg.exe not found (pass --oscdimg or install Windows ADK).");
                isoPath = Path.Combine(outputRoot, $"TinyWin2-{buildId}.iso");
                await builder.CreateIsoAsync(mediaPath, isoPath, oscdimg, ct);
            }
            string? vhdxPath = null;
            if (options.OutputMode == OutputMode.IsoAndVhdx) {
                vhdxPath = Path.Combine(outputRoot, $"TinyWin2-{buildId}.vhdx");
                await stack.ExportMergedVhdxAsync(vhdxPath, ct);
            }
            var manifestPath = await WriteManifestAsync(buildId, options, plan, stack, mediaPath, finalInstall, isoPath, vhdxPath, sourceIndex, failedSteps, ct);
            log.Phase = "done";
            log.Info($"build complete: {mediaPath}", data: new JsonObject { ["progress"] = 100 });
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
                WorkspacePath = workspace,
                MediaPath = mediaPath,
                IsoPath = isoPath,
                VhdxPath = vhdxPath,
                InstallImagePath = finalInstall,
                ManifestPath = manifestPath,
                LayerCount = stack.CommittedDepth,
                Succeeded = true,
                FailedStepId = failedSteps.Count > 0 ? failedSteps[0].StepId : null,
                FailedLayerIndex = failedSteps.Count > 0 ? failedSteps[0].LayerIndex : null,
            };
        }
        catch (Exception ex) {
            log.Phase = "failed";
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

    internal const int ProgressAfterBase = 40;
    internal const int PlanWeight = 40;

    private async Task RunPlanInLayerAsync(
        ResolvedPlan resolved,
        LayerSession session,
        JsonArray execResults,
        string workspace,
        BuildOptions options,
        CancellationToken ct) {
        var hiveCache = new Executers.Registry.RegistryHiveCache(session.MountPath, runner);
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

    private static BuildResult DryRunResult(string buildId, BuildOptions options) => new() {
        BuildId = buildId,
        WorkspacePath = "",
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
        var failures = Env.EnvironmentDoctor.Check(options.OutputRoot)
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
