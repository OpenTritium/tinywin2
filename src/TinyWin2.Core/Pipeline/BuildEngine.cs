using System.Text;
using System.Text.Json.Nodes;
using TinyWin2.Core.Env;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Fs;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Hashing;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Pipeline;

public enum OutputFormat {
    Wim,
    Esd,
    Vhdx
}

/// <summary>
///     Build phases exactly as they appear in the JSONL event stream and CLI diagnostics.
///     Renaming a value here changes the event wire contract.
/// </summary>
public static class BuildPhases {
    public const string Prepare = "prepare";
    public const string Media = "media";
    public const string BaseLayer = "base-layer";
    public const string Plan = "plan";
    public const string CbsScan = "cbs-scan";
    public const string Optimize = "optimize";
    public const string Capture = "capture";
    public const string Package = "package";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Preview = "preview";
    public const string Result = "result";
}

public sealed record BuildOptions {
    public const long DefaultBaseVhdxMaximumMb = 130_000;

    public required string InputPath { get; init; }
    public required int ImageIndex { get; init; }
    public required IReadOnlyList<PlanSelection> Selections { get; init; }
    public required string OutputPath { get; init; }
    public required string WorkspacePath { get; init; }
    public required PlanCatalog Catalog { get; init; }
    public OutputFormat OutputFormat { get; init; } = OutputFormat.Esd;

    /// <summary>Skips per-layer DISM health checks; export behavior is configured by <see cref="Export" />.</summary>
    public bool SkipLayerHealthCheck { get; init; }

    public ImageExportOptions Export { get; init; } = new();
    public bool ContinueOnError { get; init; }
    public bool DryRun { get; init; }
    public bool CaptureEvidence { get; init; } = true;

    /// <summary>
    ///     Layerless fast mode: one working mount, steps applied in place. It has no per-step
    ///     VHDX rollback, but it persists a completed prefix so a later resume can rebuild the
    ///     base and replay that prefix before retrying the failed step.
    /// </summary>
    public bool NoLayers { get; init; }

    /// <summary>Test hook: skip the environment doctor (unit tests run without 50GB free on TEMP).</summary>
    internal bool SkipEnvironmentChecks { get; init; }

    public string? PlansDirectory { get; init; }
    public bool Resume { get; init; }
    public bool OverwriteOutput { get; init; }

    public long BaseVhdxMaximumMb { get; init; } = DefaultBaseVhdxMaximumMb;
}

public sealed record BuildResult {
    public required string BuildId { get; init; }
    public required OutputFormat OutputFormat { get; init; }
    public required string OutputPath { get; init; }
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
///     Orchestrates a build: source prep → base layer (apply) → per-step VHDX diff layers
///     (atomic) → CBS scan → capture WIM/ESD or export debug VHDX → manifest.
/// </summary>
public sealed class BuildEngine(
    IProcessRunner runner,
    ExecuterRegistry executers,
    ILayerBackend layerBackend,
    BuildLog log) {
    private const int ProgressAfterBase = 40;
    private const int ProgressPlanWeight = 40;
    private const int ProgressMedia = 10;
    private const int ProgressCbsScan = 82;
    private const int ProgressCapture = 85;
    private const int ProgressOptimize = 90;
    private const int ProgressPackage = 95;
    private const int ProgressComplete = 100;

    public async Task<BuildResult> BuildAsync(BuildOptions options, CancellationToken ct) {
        var buildId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        log.Phase = BuildPhases.Prepare;
        log.Info(
            $"build {buildId} starting (atomic-plans={!options.NoLayers}, out={options.OutputFormat}, " +
            $"input={options.InputPath}, output={options.OutputPath}, workspace={options.WorkspacePath}, " +
            $"resume={options.Resume}, skip-layer-health-check={options.SkipLayerHealthCheck}, " +
            $"compression={options.Export.DismCompression}, " +
            $"verify={options.Export.VerifyCapture}, integrity={options.Export.CheckIntegrity}, " +
            $"evidence={options.CaptureEvidence})");
        var plan = BuildPlanResolver.Resolve(options.Catalog, options.Selections, executers);
        log.Info($"resolved {plan.PlanIds.Count} plans into {plan.Steps.Count} atomic steps");
        foreach (var step in plan.Steps) {
            var resources = string.Join(", ", step.Plan.Operations.Select(o => o.Resource).Distinct());
            log.Info(
                $"  step {step.Id}: plan '{step.Plan.Definition.Id}' ({resources})");
        }

        ValidateImageIndex(options.ImageIndex);
        ValidateOutputOptions(options);

        if (options.DryRun) {
            log.Info("dry run: no mutations performed");
            return DryRunResult(buildId, options);
        }

        RunDoctor(options);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var workspace = Path.GetFullPath(options.WorkspacePath);
        EnsureWorkspaceState(workspace, options.Resume);
        var resolver = new SourceImageResolver(runner, log);
        var builder = new OutputBuilder(runner, log);
        var assetFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SourceInput? source = null;
        try {
            Directory.CreateDirectory(workspace);
            var stack = VhdLayerStack.Load(workspace, layerBackend, log);
            var resolvedSource = await resolver.ResolveAsync(options.InputPath, ct);
            source = resolvedSource;
            var (stagingWim, sourceIndex) = await PrepareSourceAsync(options, workspace, resolvedSource, resolver, ct);
            var sourceFingerprint = SourceImageResolver.ComputeSourceFingerprint(resolvedSource, options.ImageIndex);
            stack.InitializeOrValidateSource(sourceFingerprint, options.ImageIndex);
            var layerlessCheckpoint = options.NoLayers
                ? await PrepareLayerlessCheckpointAsync(options, plan, workspace, sourceFingerprint,
                    sourceIndex.Index, assetFingerprints, ct)
                : null;
            var baseReady = options.Resume && stack.BaseReady && !options.NoLayers;
            if (options is { NoLayers: true, Resume: true }) {
                await stack.ResetBaseAsync(ct);
                log.Info("resume: rebuilding the single-layer base before replaying the completed prefix");
            }

            if (baseReady) {
                log.Info("resume: reusing the existing base layer (image apply skipped)");
            }
            else {
                if (source.IsEsd) {
                    log.Info("source uses ESD; exporting selected index to WIM first");
                }

                await resolver.StageAsWimAsync(source, options.ImageIndex, stagingWim, options.Export.Compression,
                    options.Export.CheckIntegrity, ct);
                await ApplyBaseAsync(stack, options, buildId, workspace, stagingWim, sourceIndex, ct,
                    options is { CaptureEvidence: true, NoLayers: false });
            }

            List<(string StepId, int LayerIndex, string Error)> failedSteps;
            string? installPath;
            if (options.NoLayers) {
                if (options.Resume && layerlessCheckpoint is not null && layerlessCheckpoint.CompletedCount > 0) {
                    await ReplayLayerlessPrefixAsync(options, plan, stack, layerlessCheckpoint, ct);
                }

                log.Info("no-layers mode: applying every step against the single working mount");
                (failedSteps, installPath) = await RunStepsAndCaptureLayerlessAsync(options, plan, stack, workspace,
                    builder, sourceIndex, layerlessCheckpoint!, ct);
            }
            else {
                failedSteps = await RunStepsAsync(options, plan, stack, workspace, assetFingerprints, ct);
                installPath = await ScanAndCaptureLeafAsync(
                    options, workspace, stack, builder, sourceIndex, ct);
            }

            var artifactPath =
                await PackageOutputAsync(options, outputPath, installPath, stack, ct);
            var manifestPath = await WriteManifestAsync(buildId, options, plan, stack, artifactPath,
                sourceIndex, failedSteps, ct);
            log.Phase = BuildPhases.Done;
            log.Info($"build complete: {artifactPath}", data: new() { ["progress"] = ProgressComplete });

            foreach (var (failedStepId, failedLayerIdx, _) in failedSteps) {
                log.Warn($"completed with skipped failed step '{failedStepId}' (layer {failedLayerIdx:000} discarded)");
            }

            return new() {
                BuildId = buildId,
                OutputFormat = options.OutputFormat,
                OutputPath = artifactPath,
                ManifestPath = manifestPath,
                LayerCount = stack.CommittedDepth,
                Succeeded = failedSteps.Count == 0,
                FailedStepId = failedSteps.Count > 0 ? failedSteps[0].StepId : null
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
                try {
                    await resolver.DismountIsoAsync(source, CancellationToken.None);
                }
                catch (Exception cleanupError) {
                    log.Error($"source ISO cleanup failed: {cleanupError.Message}");
                }
            }
        }
    }

    /// <summary>Reads the source indexes and returns the selected index plus its staging path.</summary>
    private async Task<(string StagingWim, ImageIndexInfo SourceIndex)> PrepareSourceAsync(
        BuildOptions options, string workspace, SourceInput source, SourceImageResolver resolver,
        CancellationToken ct) {
        log.Phase = BuildPhases.Media;
        log.Info($"source input: {source.InputPath} ({source.Kind}, {(source.IsEsd ? "ESD" : "WIM")} install image)",
            data: new() { ["progress"] = ProgressMedia });
        var sourceIndex = await resolver.GetIndexAsync(source.InstallImagePath, options.ImageIndex, ct);
        var stagingWim = Path.Combine(workspace, "install.source.wim");
        return (stagingWim, sourceIndex);
    }

    /// <summary>Creates (or reuses) the base layer and applies the staged image into it.</summary>
    private async Task ApplyBaseAsync(
        VhdLayerStack stack, BuildOptions options, string buildId, string workspace, string stagingWim,
        ImageIndexInfo sourceIndex,
        CancellationToken ct, bool captureEvidence = true) {
        log.Phase = BuildPhases.BaseLayer;
        await stack.EnsureBaseAsync(options.BaseVhdxMaximumMb, $"TinyWin2-{buildId}", ct);
        log.Info($"applying '{sourceIndex.Name}' (index {sourceIndex.Index}) into the base layer");
        await stack.ApplyImageToBaseAsync(async (mount, token) => {
            await OutputBuilder.ApplyImageAsync(runner, stagingWim, options.ImageIndex, mount, token);
            if (captureEvidence) {
                log.Info("capturing base-layer evidence snapshots (file manifest + registry)");
                await LayerEvidence.CaptureAsync(mount, workspace, 0, runner, log, token);
            }
        }, ct);
    }

    /// <summary>
    ///     Layerless fast path: attach the base ONCE, run every step against that single mount with no
    ///     per-step diff layers / evidence snapshots / attach-detach cycles, capture the artifact from the
    ///     live volume, detach. A failed step logs and (without ContinueOnError) aborts; the last completed
    ///     prefix is persisted for a future clean replay.
    /// </summary>
    private async Task<(List<(string StepId, int LayerIndex, string Error)>, string? InstallPath)>
        RunStepsAndCaptureLayerlessAsync(
            BuildOptions options, BuildPlan plan, VhdLayerStack stack, string workspace, OutputBuilder builder,
            ImageIndexInfo sourceIndex, LayerlessCheckpoint checkpoint, CancellationToken ct) {
        var failedSteps = new List<(string, int, string)>();
        return await WithMountedAsync(stack.LeafVhdxPath, async mountPath => {
            var stepNumber = checkpoint.CompletedCount;
            foreach (var step in plan.Steps.Skip(checkpoint.CompletedCount)) {
                ct.ThrowIfCancellationRequested();
                stepNumber++;
                log.Info($"step {stepNumber}/{plan.Steps.Count}: '{step.Title}' (no-layers)",
                    data: new() {
                        ["progress"] = ProgressAfterBase +
                                       (int)(ProgressPlanWeight * stepNumber / (double)plan.Steps.Count)
                    });
                var session = new LayerSession {
                    Record = new() { Index = stepNumber, StepId = step.Id, Title = step.Title },
                    VhdxPath = stack.LeafVhdxPath,
                    MountPath = mountPath
                };
                try {
                    _ = await RunPlanInLayerAsync(step.Plan, session, options, ct);
                    if (failedSteps.Count == 0 && checkpoint.CompletedCount == stepNumber - 1) {
                        checkpoint.CompletedCount = stepNumber;
                        checkpoint.Save(Path.Combine(workspace, "layerless-progress.json"));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) {
                    failedSteps.Add((step.Id, stepNumber, ex.Message));
                    log.Error($"step '{step.Id}' failed (no rollback in no-layers mode): {ex.Message}", step.Id,
                        stepNumber);
                    if (!options.ContinueOnError) {
                        throw new BuildStepFailedException(step.Id, stepNumber, ex);
                    }
                }
            }

            await ScanCbsAsync(mountPath, ct);
            return (failedSteps,
                await CaptureInstallImageFromMountAsync(
                    options, workspace, builder, sourceIndex, mountPath, ct));
        }, ct, "layerless operation");
    }

    private async Task ReplayLayerlessPrefixAsync(
        BuildOptions options, BuildPlan plan, VhdLayerStack stack, LayerlessCheckpoint checkpoint,
        CancellationToken ct) {
        log.Phase = BuildPhases.Plan;
        log.Info($"resume: replaying {checkpoint.CompletedCount} completed layerless step(s)");
        await WithMountedAsync(stack.BaseVhdxPath, async mountPath => {
            for (var index = 0; index < checkpoint.CompletedCount; index++) {
                ct.ThrowIfCancellationRequested();
                var step = plan.Steps[index];
                var session = new LayerSession {
                    Record = new() { Index = index + 1, StepId = step.Id, Title = step.Title },
                    VhdxPath = stack.BaseVhdxPath,
                    MountPath = mountPath
                };
                await RunPlanInLayerAsync(step.Plan, session, options, ct);
            }
        }, ct, "layerless checkpoint replay");
    }

    private async Task<LayerlessCheckpoint> PrepareLayerlessCheckpointAsync(
        BuildOptions options, BuildPlan plan, string workspace, string sourceFingerprint, int sourceIndex,
        IDictionary<string, string> assetFingerprints, CancellationToken ct) {
        var path = Path.Combine(workspace, "layerless-progress.json");
        var fingerprints = new List<(string StepId, string Fingerprint)>(plan.Steps.Count);
        foreach (var step in plan.Steps) {
            fingerprints.Add((step.Id,
                await FingerprintAsync(step, options.PlansDirectory, assetFingerprints, ct)));
        }

        if (!options.Resume) {
            var checkpoint = LayerlessCheckpoint.Create(sourceFingerprint, sourceIndex, fingerprints);
            checkpoint.Save(path);
            return checkpoint;
        }

        if (!File.Exists(path)) {
            throw new IOException(
                $"workspace '{workspace}' has no layerless checkpoint; the previous single-layer run cannot be resumed safely.");
        }

        var existing = LayerlessCheckpoint.Load(path);
        if (!string.Equals(existing.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
            || existing.SourceIndex != sourceIndex
            || existing.Steps.Count != fingerprints.Count) {
            throw new IOException(
                "the layerless resume checkpoint belongs to a different source, index, or plan selection; " +
                "start a clean build.");
        }

        for (var index = 0; index < fingerprints.Count; index++) {
            var expected = fingerprints[index];
            var actual = existing.Steps[index];
            if (!string.Equals(actual.StepId, expected.StepId, StringComparison.Ordinal)
                || !string.Equals(actual.Fingerprint, expected.Fingerprint, StringComparison.Ordinal)) {
                throw new IOException(
                    "the layerless resume checkpoint does not match the current plan selection; start a clean build.");
            }
        }

        return existing;
    }

    /// <summary>Runs every plan step as one atomic layer; returns the failed ones (ContinueOnError).</summary>
    private async Task<List<(string StepId, int LayerIndex, string Error)>> RunStepsAsync(
        BuildOptions options, BuildPlan plan, VhdLayerStack stack, string workspace,
        IDictionary<string, string> assetFingerprints, CancellationToken ct) {
        log.Phase = BuildPhases.Plan;
        var failedSteps = new List<(string, int, string)>();
        var skipCount = 0;
        if (options.Resume) {
            skipCount = await ResumePrefixAsync(plan, stack, options.PlansDirectory, assetFingerprints, ct);
        }

        var stepNumber = skipCount;
        foreach (var step in plan.Steps.Skip(skipCount)) {
            ct.ThrowIfCancellationRequested();
            stepNumber++;
            log.Info($"step {stepNumber}/{plan.Steps.Count}: '{step.Title}'",
                data: new() {
                    ["progress"] =
                        ProgressAfterBase + (int)(ProgressPlanWeight * stepNumber / (double)plan.Steps.Count)
                });
            var session = await stack.BeginLayerAsync(step.Id, step.Title, ct,
                await FingerprintAsync(step, options.PlansDirectory, assetFingerprints, ct));
            try {
                var operationResult = await RunPlanInLayerAsync(step.Plan, session, options, ct);

                if (!options.SkipLayerHealthCheck) {
                    await CheckLayerHealthAsync(session, ct);
                }

                if (options.CaptureEvidence) {
                    await LayerEvidence.CaptureAsync(session.MountPath, workspace, session.Record.Index, runner, log,
                        ct);
                }

                await stack.CommitLayerAsync(session, operationResult, ct);
            }
            catch (Exception ex) {
                var stillPending = stack.Records.Any(r => r.Index == session.Record.Index
                                                          && r.Status == LayerStatus.Pending);
                if (!stillPending) {
                    // CommitLayerAsync persists the committed state before an optional
                    // chain consolidation. A consolidation failure must leave that state
                    // available for resume instead of deleting a possibly merged layer.
                    throw;
                }

                try {
                    await stack.DiscardLayerAsync(session, ex.Message);
                }
                catch (Exception cleanupError) {
                    log.Error($"could not discard failed layer {session.Record.Index:000}: {cleanupError.Message}",
                        layerIndex: session.Record.Index);
                    throw new AggregateException(
                        $"step '{step.Id}' failed and its layer could not be discarded", ex, cleanupError);
                }

                if (ex is OperationCanceledException) {
                    throw;
                }

                failedSteps.Add((step.Id, session.Record.Index, ex.Message));
                log.Error($"step '{step.Id}' failed and its layer was discarded: {ex.Message}", step.Id,
                    session.Record.Index);
                if (!options.ContinueOnError) {
                    throw new BuildStepFailedException(step.Id, session.Record.Index, ex);
                }
            }
        }

        return failedSteps;
    }

    /// <summary>Scans and captures the final leaf while its single attachment is still mounted.</summary>
    private async Task<string?> ScanAndCaptureLeafAsync(
        BuildOptions options, string workspace, VhdLayerStack stack, OutputBuilder builder,
        ImageIndexInfo sourceIndex, CancellationToken ct) {
        return await WithMountedAsync(stack.LeafVhdxPath, async mountPath => {
            await ScanCbsAsync(mountPath, ct);
            return await CaptureInstallImageFromMountAsync(
                options, workspace, builder, sourceIndex, mountPath, ct);
        }, ct, "CBS scan/capture");
    }

    private async Task<T> WithMountedAsync<T>(
        string vhdxPath, Func<string, Task<T>> operation, CancellationToken ct, string operationName) {
        var letter = await layerBackend.AttachAsync(vhdxPath, ct);
        Exception? operationError = null;
        try {
            return await operation($"{letter}:\\");
        }
        catch (Exception ex) {
            operationError = ex;
            throw;
        }
        finally {
            try {
                await layerBackend.DetachAsync(vhdxPath, CancellationToken.None);
            }
            catch (Exception cleanupError) when (operationError is not null) {
                log.Error($"detach failed after {operationName} failure: {cleanupError.Message}");
            }
        }
    }

    private async Task WithMountedAsync(
        string vhdxPath, Func<string, Task> operation, CancellationToken ct, string operationName) {
        await WithMountedAsync<object?>(vhdxPath, async mountPath => {
            await operation(mountPath);
            return null;
        }, ct, operationName);
    }

    private async Task<string?> CaptureInstallImageFromMountAsync(
        BuildOptions options, string workspace, OutputBuilder builder, ImageIndexInfo sourceIndex,
        string mountPath, CancellationToken ct) {
        if (options.OutputFormat == OutputFormat.Vhdx) {
            return null;
        }

        log.Phase = BuildPhases.Capture;
        if (options.OutputFormat == OutputFormat.Esd) {
            log.Info("capturing an uncompressed intermediate WIM",
                data: new() { ["progress"] = ProgressCapture });
            log.Phase = BuildPhases.Optimize;
            log.Info("optimizing the captured image with recovery-compressed ESD export",
                data: new() { ["progress"] = ProgressOptimize });
            return await builder.CaptureInstallImageAsync(mountPath, sourceIndex.Name, sourceIndex.Description,
                Path.Combine(workspace, "install.intermediate.wim"), Path.Combine(workspace, "install.esd"),
                OutputFormat.Esd, options.Export, ct);
        }

        log.Info("capturing the final WIM", data: new() { ["progress"] = ProgressCapture });
        log.Phase = BuildPhases.Optimize;
        log.Info($"final WIM captured with {options.Export.DismCompression} compression",
            data: new() { ["progress"] = ProgressOptimize });
        return await builder.CaptureInstallImageAsync(mountPath, sourceIndex.Name, sourceIndex.Description,
            Path.Combine(workspace, "install.intermediate.wim"), Path.Combine(workspace, "install.captured.wim"),
            OutputFormat.Wim, options.Export, ct);
    }

    private async Task ScanCbsAsync(string mountPath, CancellationToken ct) {
        log.Phase = BuildPhases.CbsScan;
        log.Info($"scanning offline CBS health for {mountPath}",
            data: new() { ["progress"] = ProgressCbsScan });
        await runner.RunAsync("dism.exe",
            ["/English", $"/Image:{mountPath}", "/Cleanup-Image", "/ScanHealth"],
            new() { Timeout = TimeSpan.FromHours(1) }, ct);
    }

    /// <summary>Produces the selected WIM, ESD, or debug VHDX at the explicitly requested path.</summary>
    private async Task<string> PackageOutputAsync(
        BuildOptions options, string outputPath, string? installPath, VhdLayerStack stack,
        CancellationToken ct) {
        log.Phase = BuildPhases.Package;
        log.Info("packaging output", data: new() { ["progress"] = ProgressPackage });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (options.OutputFormat == OutputFormat.Vhdx) {
            await stack.ExportMergedVhdxAsync(outputPath, ct);
            return outputPath;
        }

        var resolvedInstallPath = installPath
                                  ?? throw new InvalidOperationException("WIM/ESD output requires a captured image.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Move(resolvedInstallPath, outputPath, options.OverwriteOutput);
        return outputPath;
    }

    /// <summary>
    ///     Longest common prefix of the new plan against the committed chain, by step id AND exec
    ///     fingerprint. Everything above it is diverged work and gets truncated; the prefix is reused
    ///     byte-identically (that is the whole point of differencing layers acting as checkpoints).
    /// </summary>
    private async Task<int> ResumePrefixAsync(
        BuildPlan plan, VhdLayerStack stack, string? plansDirectory,
        IDictionary<string, string> assetFingerprints, CancellationToken ct) {
        if (stack.ConsolidationPending) {
            throw new InvalidOperationException(
                "the resume workspace was interrupted during layer consolidation; start a clean build.");
        }

        var missingCommitted = stack.Records.Any(r => r.Status == LayerStatus.Committed
                                                      && r.VhdxFileName != "base.vhdx"
                                                      && (r.VhdxPath is null || !File.Exists(r.VhdxPath)));
        if (missingCommitted) {
            throw new InvalidOperationException(
                "the resume workspace has a committed layer whose VHDX is missing; start a clean build.");
        }

        var checkpoints = stack.Records
            .Where(r => (r.Status == LayerStatus.Merged
                         || (r.Status == LayerStatus.Committed && r.VhdxFileName != "base.vhdx"
                                                               && r.VhdxPath is not null && File.Exists(r.VhdxPath)))
                        && r.VhdxFileName != "base.vhdx")
            .OrderBy(r => r.Index)
            .ToList();
        var fingerprints = new string[plan.Steps.Count];
        for (var index = 0; index < plan.Steps.Count; index++) {
            fingerprints[index] = await FingerprintAsync(plan.Steps[index], plansDirectory, assetFingerprints, ct);
        }

        var reused = 0;
        while (reused < checkpoints.Count && reused < plan.Steps.Count
                                          && checkpoints[reused].StepId == plan.Steps[reused].Id
                                          && string.Equals(checkpoints[reused].StepFingerprint, fingerprints[reused],
                                              StringComparison.Ordinal)) {
            reused++;
        }

        var mergedCount = checkpoints.Count(r => r.Status == LayerStatus.Merged);
        if (mergedCount > 0 && (reused < mergedCount || reused < checkpoints.Count)) {
            throw new InvalidOperationException(
                "the resume workspace contains merged layers that do not exactly match the new plan; " +
                "merged content cannot be rolled back, so start a clean build.");
        }

        var diverged = checkpoints.Count - reused;
        log.Info($"resume: {reused} checkpoint layer(s) match the new plan; {diverged} diverged");
        if (reused > 0 || checkpoints.Count > 0) {
            var keepIndex = reused > 0 ? checkpoints[reused - 1].Index : 0;
            await stack.TruncateToAsync(keepIndex, ct);
        }

        return reused;
    }

    /// <summary>Content hash of one step and its file assets.</summary>
    internal static async Task<string> FingerprintAsync(
        PlanStep step, string? plansDirectory, IDictionary<string, string> assetFingerprints,
        CancellationToken ct) {
        var builder = new StringBuilder();
        var resolved = step.Plan;
        builder.Append(resolved.Definition.Id).Append('|')
            .Append(resolved.Definition.Version).Append('|')
            .Append(resolved.Definition.Hash).Append((char)10);
        var copyAssetSources = new List<string>();
        foreach (var operation in resolved.Operations) {
            builder.Append(operation.Resource).Append('|').Append(operation.Action).Append('|')
                .Append(operation.Spec.Spec.ToJsonString()).Append((char)10);
            if (FsPathAssets.GetCopyAssetSource(operation.Spec) is { } assetSource) {
                copyAssetSources.Add(assetSource);
            }
        }

        if (copyAssetSources.Count == 0) {
            return Fingerprinting.Compute(builder.ToString());
        }

        var assetsRoot = PlanAssets.ResolveRoot(plansDirectory, resolved.Definition.Id);
        foreach (var assetSource in copyAssetSources) {
            var assetPath = FsPathAssets.ResolveAssetPath(assetsRoot, assetSource);
            var cacheKey = assetPath ?? $"<missing:{assetSource}>";
            if (!assetFingerprints.TryGetValue(cacheKey, out var assetFingerprint)) {
                assetFingerprint = await Fingerprinting.ComputeTreeAsync(assetPath, ct);
                assetFingerprints[cacheKey] = assetFingerprint;
            }

            builder.Append("asset|").Append(assetFingerprint).Append((char)10);
        }

        return Fingerprinting.Compute(builder.ToString());
    }

    private async Task<JsonObject> RunPlanInLayerAsync(
        ResolvedPlan resolved,
        LayerSession session,
        BuildOptions options,
        CancellationToken ct) {
        var hiveCache = new RegistryHiveCache(session.MountPath, runner);
        hiveCache.SetSessionPrefix($"TinyWin2_L{session.Record.Index:D3}");
        var context = new ExecContext(
            session.MountPath,
            log,
            hiveCache,
            PlanAssets.ResolveRoot(options.PlansDirectory, resolved.Definition.Id));
        log.PlanId = resolved.Definition.Id;
        try {
            var outcomes = new List<OperationOutcome>();
            foreach (var operation in resolved.Operations) {
                ct.ThrowIfCancellationRequested();
                var executer = executers.Get(operation.Resource);
                log.Debug($"operation {operation.Resource} ({operation.Action})", resolved.Definition.Id,
                    session.Record.Index);
                var result = await executer.ApplyAsync(context, operation, ct);
                outcomes.Add(new(operation.Resource, operation.Action, result.IsSkipped, result.Changes,
                    result.SkipReason));
                if (!result.IsSkipped) {
                    log.Info($"{operation.Resource}: {result.Changes.Count} change(s)", resolved.Definition.Id,
                        session.Record.Index);
                }
                else {
                    log.Info($"{operation.Resource}: skipped ({result.SkipReason})", resolved.Definition.Id,
                        session.Record.Index);
                }
            }

            return new PlanExecutionResult(resolved.Definition.Id, outcomes).ToJson();
        }
        finally {
            log.PlanId = null;
            await hiveCache.UnloadAllAsync(log, CancellationToken.None);
        }
    }

    private async Task CheckLayerHealthAsync(LayerSession session, CancellationToken ct) {
        var result = await runner.RunAsync("dism.exe",
            [$"/Image:{session.MountPath}", "/Cleanup-Image", "/CheckHealth"],
            new() { IgnoreExitCode = true }, ct);
        if (result.ExitCode != 0) {
            throw new ExecException(
                $"image health check failed after layer {session.Record.Index:000} (exit {result.ExitCode}).");
        }
    }

    private async Task<string> WriteManifestAsync(
        string buildId,
        BuildOptions options,
        BuildPlan plan,
        VhdLayerStack stack,
        string outputPath,
        ImageIndexInfo sourceIndex,
        List<(string StepId, int LayerIndex, string Error)> failedSteps,
        CancellationToken ct) {
        log.Info("writing build manifest");
        var outputMetadata = await FileMetadataAsync(outputPath, ct);
        var manifest = new JsonObject {
            ["schemaVersion"] = 6,
            ["tool"] = "TinyWin2",
            ["buildId"] = buildId,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["inputPath"] = Path.GetFullPath(options.InputPath),
            ["workspacePath"] = Path.GetFullPath(options.WorkspacePath),
            ["sourceIndex"] = new JsonObject {
                ["index"] = sourceIndex.Index,
                ["name"] = sourceIndex.Name,
                ["editionId"] = sourceIndex.EditionId,
                ["version"] = sourceIndex.Version
            },
            ["outputFormat"] = options.OutputFormat.ToString().ToLowerInvariant(),
            ["export"] = new JsonObject {
                ["wimCompression"] = options.Export.DismCompression,
                ["finalCompression"] = options.OutputFormat switch {
                    OutputFormat.Wim => options.Export.DismCompression,
                    OutputFormat.Esd => "recovery",
                    _ => null
                },
                ["verifyCapture"] = options.Export.VerifyCapture,
                ["checkIntegrity"] = options.Export.CheckIntegrity
            },
            ["atomicPlans"] = !options.NoLayers,
            ["planIds"] = new JsonArray(plan.PlanIds.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
            ["output"] = outputMetadata,
            ["failedSteps"] = new JsonArray(failedSteps.Select(f => (JsonNode)new JsonObject {
                ["stepId"] = f.StepId,
                ["layerIndex"] = f.LayerIndex,
                ["error"] = f.Error
            }).ToArray()),
            ["layers"] = new JsonArray(stack.Records.Select(r => (JsonNode)r.ToJson()).ToArray())
        };
        var manifestDirectory = Path.GetFullPath(options.WorkspacePath);
        Directory.CreateDirectory(manifestDirectory);
        var manifestPath = Path.Combine(manifestDirectory, "tinywin2-manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifest.ToPrettyString(), ct);
        return manifestPath;
    }

    private static async Task<JsonObject> FileMetadataAsync(string path, CancellationToken ct) {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) {
            throw new IOException($"artifact is missing or empty: '{path}'.");
        }

        return new() {
            ["path"] = path,
            ["hashAlgorithm"] = Fingerprinting.Algorithm,
            ["hash"] = await OutputBuilder.ComputeHashAsync(path, ct),
            ["sizeBytes"] = new FileInfo(path).Length
        };
    }

    private static BuildResult DryRunResult(string buildId, BuildOptions options) => new() {
        BuildId = buildId,
        OutputFormat = options.OutputFormat,
        OutputPath = Path.GetFullPath(options.OutputPath),
        ManifestPath = Path.Combine(Path.GetFullPath(options.WorkspacePath), "tinywin2-manifest.json"),
        LayerCount = 0,
        Succeeded = true
    };

    private static void ValidateOutputOptions(BuildOptions options) {
        var outputPath = Path.GetFullPath(options.OutputPath);
        var expectedExtension = options.OutputFormat switch {
            OutputFormat.Wim => ".wim",
            OutputFormat.Esd => ".esd",
            OutputFormat.Vhdx => ".vhdx",
            _ => throw new ArgumentOutOfRangeException(nameof(options.OutputFormat))
        };
        if (!Path.GetExtension(outputPath).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException(
                $"output path must end with '{expectedExtension}' for {options.OutputFormat} output.",
                nameof(options.OutputPath));
        }

        if (File.Exists(outputPath) && !options.OverwriteOutput) {
            throw new OutputExistsException($"output already exists: '{outputPath}' (pass --overwrite to replace it).");
        }
    }

    private static void ValidateImageIndex(int imageIndex) {
        if (imageIndex < 1) {
            throw new ArgumentOutOfRangeException(nameof(imageIndex), imageIndex,
                "image index must be greater than zero.");
        }
    }

    private void RunDoctor(BuildOptions options) {
        if (options.SkipEnvironmentChecks) {
            return;
        }

        if (!OperatingSystem.IsWindows()) {
            throw new PlatformNotSupportedException("TinyWin2 builds are Windows-only (DISM/diskpart/VHDX).");
        }

        var resumableBase = options.Resume
                            && File.Exists(Path.Combine(options.WorkspacePath, "layers.json"))
                            && File.Exists(Path.Combine(options.WorkspacePath, "base.vhdx"));
        var failures = EnvironmentDoctor.Check(
                options.WorkspacePath,
                resumableBase
                    ? 10L * 1024 * 1024 * 1024
                    : options.NoLayers
                        ? 30L * 1024 * 1024 * 1024
                        : null)
            .Where(c => c is { Required: true, Ok: false })
            .ToList();
        if (failures.Count > 0) {
            throw new EnvironmentCheckFailedException(
                "environment checks failed: " + string.Join("; ", failures.Select(f => $"{f.Name}: {f.Detail}")));
        }
    }

    private static void EnsureWorkspaceState(string workspace, bool resume) {
        if (resume) {
            if (!File.Exists(Path.Combine(workspace, "layers.json"))) {
                throw new WorkspaceConflictException(
                    $"workspace '{workspace}' is not resumable; layers.json is missing.");
            }

            return;
        }

        if (Directory.Exists(workspace)
            && Directory.EnumerateFileSystemEntries(workspace)
                .Any(path => !string.Equals(Path.GetFileName(path), "logs", StringComparison.OrdinalIgnoreCase))) {
            throw new WorkspaceConflictException(
                $"workspace '{workspace}' is not empty; choose a new workspace or pass --resume.");
        }
    }
}
