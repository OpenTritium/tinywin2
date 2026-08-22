using System.Text;
using System.Text.Json.Nodes;
using TinyWin2.Core.Env;
using TinyWin2.Core.Executers;
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
///     Build phases exactly as they appear in the JSONL event stream and the GUI's routing.
///     Renaming a value here changes the wire contract — the GUI mirrors these strings.
/// </summary>
public static class BuildPhases {
    public const string Prepare = "prepare";
    public const string Media = "media";
    public const string BaseLayer = "base-layer";
    public const string Plan = "plan";
    public const string CbsScan = "cbs-scan";
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
    public OutputFormat OutputFormat { get; init; } = OutputFormat.Esd;
    public bool CreateIso { get; init; }
    public bool Fast { get; init; }
    public bool ContinueOnError { get; init; }
    public bool KeepLayers { get; init; }
    public bool DryRun { get; init; }
    public bool CaptureEvidence { get; init; } = true;

    /// <summary>
    ///     Layerless fast mode: one working mount, steps applied in place. No atomic rollback,
    ///     no layer diff trail, no resume checkpoints — a failed step just leaves the exec's own work undone.
    /// </summary>
    public bool NoLayers { get; init; }

    /// <summary>Test hook: skip the environment doctor (unit tests run without 50GB free on TEMP).</summary>
    internal bool SkipEnvironmentChecks { get; init; }

    public string? OscdimgPath { get; init; }
    public string? PlansDirectory { get; init; }

    /// <summary>Existing workspace to resume from: committed layers whose step fingerprint still matches are reused as-is.</summary>
    public string? ResumeWorkspace { get; init; }

    public long BaseVhdxMaximumMb { get; init; } = DefaultBaseVhdxMaximumMb;
}

public sealed record BuildResult {
    public required string BuildId { get; init; }
    public required OutputFormat OutputFormat { get; init; }
    public string? MediaPath { get; init; }
    public string? IsoPath { get; init; }
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
///     Orchestrates a build: media prep → base layer (apply) → per-step VHDX diff layers
///     (atomic) → capture WIM/ESD or export VHDX → optional ISO packaging → manifest.
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
        var buildId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        log.Phase = BuildPhases.Prepare;
        log.Info(
            $"build {buildId} starting (atomic-plans=true, out={options.OutputFormat}, " +
            $"iso={options.CreateIso}, fast={options.Fast}, evidence={options.CaptureEvidence})");
        var plan = BuildPlanResolver.Resolve(options.Catalog, options.Selections);
        executers.ValidateBuildPlan(plan);
        log.Info($"resolved {plan.PlanIds.Count} plans into {plan.Steps.Count} atomic steps");
        foreach (var step in plan.Steps) {
            log.Info(
                $"  step {step.Id}: plan '{step.Plan.Definition.Id}' ({step.Plan.Operation.Resource})");
        }

        ValidateOutputOptions(options);

        if (options.DryRun) {
            log.Info("dry run: no mutations performed");
            return DryRunResult(buildId, options.OutputFormat);
        }

        if (options is { NoLayers: true, ResumeWorkspace: not null }) {
            throw new InvalidOperationException("--single-layer cannot resume: no layer chain is kept to reuse.");
        }

        RunDoctor(options);
        var outputRoot = Path.GetFullPath(options.OutputRoot);
        var workspace = options.ResumeWorkspace ?? Path.Combine(outputRoot, "work", buildId);
        var mediaPath = options.OutputFormat == OutputFormat.Vhdx
            ? null
            : Path.Combine(outputRoot, $"TinyWin2-{buildId}");
        var resolver = new SourceImageResolver(runner, log);
        var builder = new OutputBuilder(runner, log);
        var assetFingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SourceMedia? source = null;
        try {
            Directory.CreateDirectory(workspace);
            var stack = VhdLayerStack.Load(workspace, layerBackend, log);
            var resolvedSource = await resolver.ResolveAsync(options.SourcePath, ct);
            source = resolvedSource;
            var (stagingWim, sourceIndex) = await PrepareSourceAsync(options, workspace, resolvedSource, resolver, ct);
            stack.InitializeOrValidateSource(
                SourceImageResolver.ComputeSourceFingerprint(resolvedSource, options.ImageIndex),
                options.ImageIndex);
            var baseReady = options.ResumeWorkspace is not null && stack.BaseReady;
            if (baseReady) {
                log.Info("resume: reusing the existing base layer (image apply skipped)");
            }
            else {
                if (source.IsEsd) {
                    log.Info("source uses ESD; exporting selected index to WIM first");
                }

                await resolver.StageAsWimAsync(source, options.ImageIndex, stagingWim, options.Fast, ct);
                await ApplyBaseAsync(stack, options, buildId, workspace, stagingWim, sourceIndex, ct,
                    options is { CaptureEvidence: true, NoLayers: false });
            }

            List<(string StepId, int LayerIndex, string Error)> failedSteps;
            string? installPath;
            if (options.NoLayers) {
                log.Info("no-layers mode: applying every step against the single working mount");
                (failedSteps, installPath) = await RunStepsAndCaptureLayerlessAsync(options, plan, stack, workspace,
                    builder, sourceIndex, ct);
            }
            else {
                failedSteps = await RunStepsAsync(options, plan, stack, workspace, assetFingerprints, ct);
                installPath = await ScanAndCaptureLeafAsync(
                    options, workspace, stack, builder, sourceIndex, ct);
            }

            var (outputPath, isoPath) =
                await PackageOutputAsync(options, buildId, resolvedSource, mediaPath, installPath, stack, builder, ct);
            var manifestPath = await WriteManifestAsync(buildId, options, plan, stack, mediaPath, outputPath,
                isoPath, sourceIndex, failedSteps, ct);
            log.Phase = BuildPhases.Done;
            log.Info($"build complete: {outputPath}", data: new() { ["progress"] = ProgressComplete });
            if (isoPath is not null) {
                log.Info($"ISO: {isoPath}");
            }

            foreach (var (failedStepId, failedLayerIdx, _) in failedSteps) {
                log.Warn($"completed with skipped failed step '{failedStepId}' (layer {failedLayerIdx:000} discarded)");
            }

            if (!options.KeepLayers) {
                await TryDeleteDirectoryAsync(workspace);
            }

            return new() {
                BuildId = buildId,
                OutputFormat = options.OutputFormat,
                MediaPath = mediaPath,
                IsoPath = isoPath,
                OutputPath = outputPath,
                ManifestPath = manifestPath,
                LayerCount = stack.CommittedDepth,
                Succeeded = true,
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
        BuildOptions options, string workspace, SourceMedia source, SourceImageResolver resolver,
        CancellationToken ct) {
        log.Phase = BuildPhases.Media;
        log.Info($"source media: {source.RootPath} ({(source.IsEsd ? "ESD" : "WIM")} install image)",
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
            await runner.RunAsync("dism.exe",
                [
                    "/English", "/Apply-Image", $"/ImageFile:{stagingWim}", $"/Index:{options.ImageIndex}",
                    $"/ApplyDir:{mount}"
                ],
                new() { Timeout = TimeSpan.FromHours(2) }, token);
            if (captureEvidence) {
                log.Info("capturing base-layer evidence snapshots (file manifest + registry)");
                await LayerEvidence.CaptureAsync(mount, workspace, 0, runner, log, token);
            }
        }, ct);
    }

    /// <summary>
    ///     Layerless fast path: attach the base ONCE, run every step against that single mount with no
    ///     per-step diff layers / evidence snapshots / attach-detach cycles, capture the artifact from the
    ///     live volume, detach. A failed step logs and (without ContinueOnError) aborts; nothing is rolled
    ///     back — the operation's own plan granularity is all the atomicity there is.
    /// </summary>
    private async Task<(List<(string StepId, int LayerIndex, string Error)>, string? InstallPath)>
        RunStepsAndCaptureLayerlessAsync(
            BuildOptions options, BuildPlan plan, VhdLayerStack stack, string workspace, OutputBuilder builder,
            ImageIndexInfo sourceIndex, CancellationToken ct) {
        var failedSteps = new List<(string, int, string)>();
        return await WithMountedAsync(stack.LeafVhdxPath, async mountPath => {
            var stepNumber = 0;
            foreach (var step in plan.Steps) {
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

    /// <summary>Runs every plan step as one atomic layer; returns the failed ones (ContinueOnError).</summary>
    private async Task<List<(string StepId, int LayerIndex, string Error)>> RunStepsAsync(
        BuildOptions options, BuildPlan plan, VhdLayerStack stack, string workspace,
        IDictionary<string, string> assetFingerprints, CancellationToken ct) {
        log.Phase = BuildPhases.Plan;
        var failedSteps = new List<(string, int, string)>();
        var skipCount = 0;
        if (options.ResumeWorkspace is not null) {
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

                if (!options.Fast) {
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

    private async Task<string?> CaptureInstallImageFromMountAsync(
        BuildOptions options, string workspace, OutputBuilder builder, ImageIndexInfo sourceIndex,
        string mountPath, CancellationToken ct) {
        if (options.OutputFormat == OutputFormat.Vhdx) {
            return null;
        }

        log.Phase = BuildPhases.Capture;
        if (options.OutputFormat == OutputFormat.Esd) {
            // Uncompressed staging + single compression in the export below avoids re-encoding twice.
            var intermediate = Path.Combine(workspace, "install.intermediate.wim");
            await builder.CaptureAsync(mountPath, intermediate, sourceIndex.Name, sourceIndex.Description,
                "none", false, ct);
            var esdPath = Path.Combine(workspace, "install.esd");
            await builder.ExportEsdAsync(intermediate, esdPath, ct);
            return esdPath;
        }

        var capturedWim = Path.Combine(workspace, "install.captured.wim");
        await builder.CaptureAsync(mountPath, capturedWim, sourceIndex.Name, sourceIndex.Description,
            OutputFormat.Wim, options.Fast, ct);
        return capturedWim;
    }

    private async Task ScanCbsAsync(string mountPath, CancellationToken ct) {
        log.Phase = BuildPhases.CbsScan;
        log.Info($"scanning offline CBS health for {mountPath}");
        await runner.RunAsync("dism.exe",
            ["/English", $"/Image:{mountPath}", "/Cleanup-Image", "/ScanHealth"],
            new() { Timeout = TimeSpan.FromHours(1) }, ct);
    }

    /// <summary>Produces the selected image artifact and optionally packages WIM/ESD media as an ISO.</summary>
    private async Task<(string OutputPath, string? IsoPath)> PackageOutputAsync(
        BuildOptions options, string buildId, SourceMedia source, string? mediaPath, string? installPath,
        VhdLayerStack stack, OutputBuilder builder, CancellationToken ct) {
        log.Phase = BuildPhases.Package;
        log.Info("packaging output", data: new() { ["progress"] = ProgressPackage });

        if (options.OutputFormat == OutputFormat.Vhdx) {
            var vhdxPath = Path.Combine(Path.GetFullPath(options.OutputRoot), $"TinyWin2-{buildId}.vhdx");
            await stack.ExportMergedVhdxAsync(vhdxPath, ct);
            return (vhdxPath, null);
        }

        var resolvedMediaPath = mediaPath
                                ?? throw new InvalidOperationException("WIM/ESD output requires a media output path.");
        var resolvedInstallPath = installPath
                                  ?? throw new InvalidOperationException("WIM/ESD output requires a captured image.");
        var finalInstall = await builder.RebuildMediaAsync(
            source.RootPath, resolvedMediaPath, resolvedInstallPath, options.OutputFormat, ct);
        string? isoPath = null;
        if (options.CreateIso) {
            var oscdimg = options.OscdimgPath
                          ?? ToolLocator.Locate("oscdimg.exe")
                          ?? throw new FileNotFoundException(
                              "oscdimg.exe not found (pass --oscdimg or install Windows ADK).");
            isoPath = Path.Combine(Path.GetFullPath(options.OutputRoot), $"TinyWin2-{buildId}.iso");
            await builder.CreateIsoAsync(resolvedMediaPath, isoPath, oscdimg, ct);
        }

        return (finalInstall, isoPath);
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
    private static async Task<string> FingerprintAsync(
        PlanStep step, string? plansDirectory, IDictionary<string, string> assetFingerprints,
        CancellationToken ct) {
        var builder = new StringBuilder();
        var resolved = step.Plan;
        builder.Append(resolved.Definition.Id).Append('|')
            .Append(resolved.Definition.Version).Append('|')
            .Append(resolved.Definition.Hash).Append((char)10);
        var operation = resolved.Operation;
        builder.Append(operation.Resource).Append('|').Append(operation.Action).Append('|')
            .Append(operation.Spec.ToJsonString()).Append((char)10);
        if (operation is { Resource: "fs.path", Action: OperationAction.Copy }) {
            var source = operation.Spec["source"]?.GetValue<string>();
            var assetsRoot = ResolveAssetsRoot(plansDirectory, resolved.Definition.Id);
            var assetPath = ResolveAssetPathForFingerprint(assetsRoot, source);
            var cacheKey = assetPath ?? $"<missing:{source}>";
            if (!assetFingerprints.TryGetValue(cacheKey, out var assetFingerprint)) {
                assetFingerprint = await AssetFingerprintAsync(assetPath, ct);
                assetFingerprints[cacheKey] = assetFingerprint;
            }

            builder.Append("asset|").Append(assetFingerprint).Append((char)10);
        }

        return Fingerprinting.Compute(builder.ToString());
    }

    private static string? ResolveAssetPathForFingerprint(string? assetsRoot, string? source) {
        if (assetsRoot is null || source is null) {
            return null;
        }

        var root = Path.GetFullPath(assetsRoot).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root,
            source.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static async Task<string> AssetFingerprintAsync(string? assetPath, CancellationToken ct) {
        if (assetPath is null || (!File.Exists(assetPath) && !Directory.Exists(assetPath))) {
            return "missing";
        }

        if (File.Exists(assetPath)) {
            return await Fingerprinting.ComputeFileAsync(assetPath, ct);
        }

        var entries = new List<string>();
        var pending = new Stack<string>();
        pending.Push(assetPath);
        while (pending.TryPop(out var currentPath)) {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(currentPath)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) {
                var relative = Path.GetRelativePath(assetPath, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    entries.Add("reparse:" + relative);
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0) {
                    entries.Add("directory:" + relative);
                    pending.Push(entry);
                }
                else {
                    var hash = await Fingerprinting.ComputeFileAsync(entry, ct);
                    entries.Add($"file:{relative}:{hash}");
                }
            }
        }

        var canonical = string.Join('\n', entries.OrderBy(entry => entry, StringComparer.Ordinal));
        return Fingerprinting.Compute(canonical);
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
            ResolveAssetsRoot(options.PlansDirectory, resolved.Definition.Id));
        log.PlanId = resolved.Definition.Id;
        try {
            ct.ThrowIfCancellationRequested();
            var operation = resolved.Operation;
            var executer = executers.Get(operation.Resource);
            log.Debug($"operation {operation.Resource} ({operation.Action})", resolved.Definition.Id,
                session.Record.Index);
            var result = await executer.ApplyAsync(context, operation, ct);
            var operationResult = new JsonObject {
                ["planId"] = resolved.Definition.Id,
                ["resource"] = operation.Resource,
                ["action"] = operation.Action.ToString().ToLowerInvariant(),
                ["status"] = result.Status.ToString().ToLowerInvariant(),
                ["changes"] = new JsonArray(result.Changes.Select(c => (JsonNode)c.ToJson()).ToArray()),
                ["skipReason"] = result.SkipReason
            };
            if (result.Status == ExecStatus.Applied) {
                log.Info($"{operation.Resource}: {result.Changes.Count} change(s)", resolved.Definition.Id,
                    session.Record.Index);
            }
            else if (result.Status == ExecStatus.Skipped) {
                log.Info($"{operation.Resource}: skipped ({result.SkipReason})", resolved.Definition.Id,
                    session.Record.Index);
            }

            return operationResult;
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
        string? mediaPath,
        string outputPath,
        string? isoPath,
        ImageIndexInfo sourceIndex,
        List<(string StepId, int LayerIndex, string Error)> failedSteps,
        CancellationToken ct) {
        log.Info("writing build manifest");
        var outputMetadata = await FileMetadataAsync(outputPath, ct);
        var isoMetadata = isoPath is null ? null : await FileMetadataAsync(isoPath, ct);
        var manifest = new JsonObject {
            ["schemaVersion"] = 5,
            ["tool"] = "TinyWin2",
            ["buildId"] = buildId,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sourcePath"] = Path.GetFullPath(options.SourcePath),
            ["sourceIndex"] = new JsonObject {
                ["index"] = sourceIndex.Index,
                ["name"] = sourceIndex.Name,
                ["editionId"] = sourceIndex.EditionId,
                ["version"] = sourceIndex.Version
            },
            ["outputFormat"] = options.OutputFormat.ToString().ToLowerInvariant(),
            ["createIso"] = options.CreateIso,
            ["atomicPlans"] = true,
            ["planIds"] = new JsonArray(plan.PlanIds.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
            ["mediaPath"] = mediaPath,
            ["output"] = outputMetadata,
            ["isoPath"] = isoPath,
            ["iso"] = isoMetadata,
            ["failedSteps"] = new JsonArray(failedSteps.Select(f => (JsonNode)new JsonObject {
                ["stepId"] = f.StepId,
                ["layerIndex"] = f.LayerIndex,
                ["error"] = f.Error
            }).ToArray()),
            ["layers"] = new JsonArray(stack.Records.Select(r => (JsonNode)r.ToJson()).ToArray())
        };
        var manifestDirectory = mediaPath ?? Path.GetFullPath(options.OutputRoot);
        Directory.CreateDirectory(manifestDirectory);
        var manifestFileName = mediaPath is null
            ? $"TinyWin2-{buildId}-manifest.json"
            : "tinywin2-manifest.json";
        var manifestPath = Path.Combine(manifestDirectory, manifestFileName);
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

    private static BuildResult DryRunResult(string buildId, OutputFormat outputFormat) => new() {
        BuildId = buildId,
        OutputFormat = outputFormat,
        MediaPath = "",
        OutputPath = "",
        ManifestPath = "",
        LayerCount = 0,
        Succeeded = true
    };

    private static void ValidateOutputOptions(BuildOptions options) {
        if (options is { CreateIso: true, OutputFormat: OutputFormat.Vhdx }) {
            throw new ArgumentException(
                "ISO packaging requires WIM or ESD output; use --output-format esd --iso or --output-format wim --iso.");
        }
    }

    private void RunDoctor(BuildOptions options) {
        if (options.SkipEnvironmentChecks) {
            return;
        }

        if (!OperatingSystem.IsWindows()) {
            throw new PlatformNotSupportedException("TinyWin2 builds are Windows-only (DISM/diskpart/VHDX).");
        }

        var failures = EnvironmentDoctor.Check(options.OutputRoot)
            .Where(c => c is { Required: true, Ok: false })
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
                    Directory.Delete(path, true);
                }

                return;
            }
            catch (Exception ex) when (attempt < 2 && ex is IOException or UnauthorizedAccessException) {
                await Task.Delay(1000);
            }
            catch (Exception ex) {
                // Leftover work dirs are annoying but harmless; the next build uses a new id.
                await Console.Error.WriteLineAsync($"warning: could not clean workspace '{path}': {ex.Message}");
            }
        }
    }
}
