using System.Text.Json.Nodes;
using TinyWin2.Core;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;

namespace TinyWin2.Cli.Commands;

internal static class BuildHandler {
    public static async Task<int> ExecuteAsync(BuildRequest request) {
        if (request.Switches.BaseVhdxMaximumMb <= 0) {
            throw new ArgumentException("base VHDX size must be positive");
        }

        var inputPath = Path.GetFullPath(request.Input);
        var imageIndex = request.ImageIndex;
        var outputPath = Path.GetFullPath(request.Output);
        var workspacePath = Path.GetFullPath(request.Workspace);
        var plansDir = Cli.FindPlansDirectory(request.Selection.PlansDirectory);
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var selections = Cli.BuildSelections(request.Selection, catalog);
        var jsonEvents = request.Switches.JsonEvents;
        // Serilog owns console + file output; the JSONL event stream owns stdout in --json-events mode.
        var log = new BuildLog();
        if (jsonEvents) {
            _ = log.Attach(evt => Console.Out.WriteLine(evt.ToJson().ToCompactString()));
        }

        var logDirectory = Path.Combine(workspacePath, "logs");
        Directory.CreateDirectory(logDirectory);
        var logFilePath = Path.Combine(logDirectory,
            $"tinywin2-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.log");
        using var serilog = log.UseSerilog(logFilePath, !jsonEvents);
        var (runner, executers, layers) = Cli.CreateEngineParts();
        var engine = new BuildEngine(runner, executers, layers, log);
        using var cts = new CancellationTokenSource();
        var ctsReference = new WeakReference<CancellationTokenSource>(cts);
        Console.CancelKeyPress += OnCancel;
        try {
            var result = await engine.BuildAsync(new() {
                InputPath = inputPath,
                ImageIndex = imageIndex,
                Selections = selections,
                OutputPath = outputPath,
                WorkspacePath = workspacePath,
                Catalog = catalog,
                OutputFormat = request.Format,
                SkipLayerHealthCheck = request.Switches.SkipLayerHealthChecks,
                Export = request.Export,
                ContinueOnError = request.Switches.ContinueOnError,
                NoLayers = request.Switches.SingleLayer,
                DryRun = request.Switches.DryRun,
                CaptureEvidence = !request.Switches.SkipEvidence,
                Resume = request.Switches.Resume,
                OverwriteOutput = request.Switches.Overwrite,
                PlansDirectory = plansDir,
                BaseVhdxMaximumMb = request.Switches.BaseVhdxMaximumMb
            }, cts.Token);
            if (jsonEvents) {
                await Console.Out.WriteLineAsync(new JsonObject {
                    ["seq"] = -1,
                    ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["level"] = "info",
                    ["phase"] = BuildPhases.Result,
                    ["message"] = "build finished",
                    ["data"] = new JsonObject {
                        ["succeeded"] = result.Succeeded,
                        ["outputFormat"] = result.OutputFormat.ToString().ToLowerInvariant(),
                        ["workspacePath"] = workspacePath,
                        ["outputPath"] = result.OutputPath,
                        ["manifestPath"] = result.ManifestPath,
                        ["layerCount"] = result.LayerCount,
                        ["failedStepId"] = result.FailedStepId
                    }
                }.ToJsonString());
            }
            else {
                Console.WriteLine();
                Console.WriteLine($"✔ build {result.BuildId} complete");
                Console.WriteLine($"  format:    {result.OutputFormat.ToString().ToLowerInvariant()}");
                Console.WriteLine($"  output:    {result.OutputPath}");
                Console.WriteLine($"  manifest:  {result.ManifestPath}");
                Console.WriteLine($"  workspace: {workspacePath}");
                Console.WriteLine($"  log:       {logFilePath}");
                Console.WriteLine($"  layers:    {result.LayerCount}");
            }

            return result.Succeeded ? 0 : 1;
        }
        catch (BuildStepFailedException ex) {
            if (jsonEvents) {
                CliErrors.Emit(ex, json: true);
            }
            else {
                await Console.Error.WriteLineAsync(
                    request.Switches.SingleLayer
                        ? $"✘ step '{ex.StepId}' failed at step {ex.LayerIndex:000}; the single-layer workspace was kept for checkpointed recovery."
                        : $"✘ step '{ex.StepId}' failed at layer {ex.LayerIndex:000}; the failed layer was discarded and the previous layer was kept.");
                await Console.Error.WriteLineAsync(
                    $"  recovery: rerun the same build command with --resume to replay from the previous completed step and retry '{ex.StepId}'.");
                if (!request.Switches.SingleLayer) {
                    await Console.Error.WriteLineAsync(
                        $"  post-mortem: tinywin2 layer diff --workspace \"{request.Workspace}\" " +
                        $"--from {Math.Max(0, ex.LayerIndex - 1)} --to {ex.LayerIndex}");
                }
            }

            return ExitCodes.Failure;
        }
        catch (OperationCanceledException ex) {
            if (jsonEvents) {
                CliErrors.Emit(ex, json: true);
            }
            else {
                await Console.Error.WriteLineAsync("cancelled.");
            }

            return ExitCodes.Canceled;
        }
        finally {
            Console.CancelKeyPress -= OnCancel; // the handler outlives the disposed cts otherwise
        }

        void OnCancel(object? sender, ConsoleCancelEventArgs e) {
            e.Cancel = true;
            log.Warn("cancellation requested; rolling back the current layer…");
            try {
                if (ctsReference.TryGetTarget(out var source)) {
                    source.Cancel();
                }
            }
            catch (ObjectDisposedException) {
                // the build finished between the keypress and the handler deregistration
            }
        }
    }
}

internal static class PreviewHandler {
    public static async Task<int> ExecuteAsync(PreviewRequest request) {
        if (request.BaseVhdxMaximumMb <= 0) {
            throw new ArgumentException("base VHDX size must be positive");
        }

        var inputPath = Path.GetFullPath(request.Input);
        var imageIndex = request.ImageIndex;
        var plansDir = Cli.FindPlansDirectory(request.Selection.PlansDirectory);
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var selections = Cli.BuildSelections(request.Selection, catalog);
        var json = request.Json;
        var log = new BuildLog();
        var (runner, executers, layers) = Cli.CreateEngineParts();
        var previewer = new PreviewRunner(runner, executers, layers, log);
        var workDirectory = Path.GetFullPath(request.Workspace);
        var previewLogDirectory = Path.Combine(Path.GetDirectoryName(workDirectory)!, "logs");
        Directory.CreateDirectory(previewLogDirectory);
        using var previewSerilog = log.UseSerilog(
            Path.Combine(previewLogDirectory,
                $"tinywin2-preview-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.log"),
            !json);
        using var cts = new CancellationTokenSource();
        var ctsReference = new WeakReference<CancellationTokenSource>(cts);
        ConsoleCancelEventHandler onCancel = (_, e) => {
            e.Cancel = true;
            log.Warn("cancellation requested; stopping preview…");
            if (ctsReference.TryGetTarget(out var source)) {
                source.Cancel();
            }
        };
        Console.CancelKeyPress += onCancel;
        try {
            var previews = await previewer.RunAsync(new() {
                InputPath = inputPath,
                ImageIndex = imageIndex,
                Selections = selections,
                WorkDirectory = workDirectory,
                Catalog = catalog,
                BaseVhdxMaximumMb = request.BaseVhdxMaximumMb,
                PlansDirectory = plansDir
            }, cts.Token);
            if (json) {
                Console.WriteLine(new JsonObject {
                    ["plans"] = new JsonArray(previews.Select(p => (JsonNode)p.ToJson()).ToArray())
                }.ToJsonString(Cli.JsonSerializerOptions));
            }
            else {
                Console.WriteLine();
                Console.WriteLine($"{"plan",-46} {"state",-8} changes");
                foreach (var preview in previews) {
                    Console.WriteLine(
                        $"{preview.PlanId,-46} {(preview.Satisfied ? "no-op" : "will-do"),-8} {preview.Differences.Count}");
                    foreach (var difference in preview.Differences.Take(8)) {
                        Console.WriteLine($"    [{difference.Kind}] {difference.Target}");
                    }

                    if (preview.Differences.Count > 8) {
                        Console.WriteLine($"    … {preview.Differences.Count - 8} more");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException) {
            await Console.Error.WriteLineAsync("preview cancelled.");
            return ExitCodes.Canceled;
        }
        finally {
            Console.CancelKeyPress -= onCancel;
            // The preview workspace is explicit and remains available for inspection.
        }
    }
}

internal static class SourceValidateHandler {
    public static async Task<int> ExecuteAsync(ValidateRequest request) {
        var input = Path.GetFullPath(request.Input);
        SourceInputKind? assertedKind = request.Kind?.ToLowerInvariant() switch {
            null => null,
            "image" => SourceInputKind.Image,
            "media" => SourceInputKind.Media,
            "iso" => SourceInputKind.Iso,
            _ => throw new ArgumentException("validation kind must be image, media, or iso", nameof(request.Kind))
        };
        var resolver = new SourceImageResolver(new ProcessRunner(), new());
        var source = await resolver.ResolveAsync(input, CancellationToken.None);
        try {
            // An explicit --kind is an assertion the input must satisfy; otherwise it is inferred.
            if (assertedKind is { } expected && source.Kind != expected) {
                throw new ArgumentException(
                    $"input '{input}' is {source.Kind.ToString().ToLowerInvariant()}, not {request.Kind}");
            }

            var kind = source.Kind;
            if (kind != SourceInputKind.Image) {
                OutputBuilder.ValidateBootMedia(source.MediaRootPath!);
                await new SetupImageContractValidator(new ProcessRunner(), new())
                    .ValidateAsync(source.InstallImagePath, 1, CancellationToken.None);
            }

            var indexes = await resolver.GetIndexesAsync(source.InstallImagePath, CancellationToken.None);
            if (request.Json) {
                Console.WriteLine(new JsonObject {
                    ["input"] = input,
                    ["kind"] = kind.ToString().ToLowerInvariant(),
                    ["installImage"] = source.InstallImagePath,
                    ["bootable"] = kind != SourceInputKind.Image,
                    ["indexes"] = new JsonArray(indexes.Select(index => (JsonNode)new JsonObject {
                        ["index"] = index.Index,
                        ["name"] = index.Name,
                        ["editionId"] = index.EditionId,
                        ["version"] = index.Version
                    }).ToArray())
                }.ToJsonString(Cli.JsonSerializerOptions));
            }
            else {
                Console.WriteLine(
                    $"valid {kind.ToString().ToLowerInvariant()}: {indexes.Count} index(es), install image {source.InstallImagePath}");
            }

            return ExitCodes.Success;
        }
        finally {
            await resolver.DismountIsoAsync(source, CancellationToken.None);
        }
    }
}

internal static class PackageHandler {
    public static async Task<int> CreateIsoAsync(PackageIsoRequest request) {
        var input = Path.GetFullPath(request.Input);
        var image = Path.GetFullPath(request.InstallImage);
        var output = Path.GetFullPath(request.Output);
        var workspace = Path.GetFullPath(request.Workspace);
        var oscdimg = Path.GetFullPath(request.Oscdimg);
        var unattended = request.Unattended is null ? null : Path.GetFullPath(request.Unattended);
        if (!File.Exists(image) || new FileInfo(image).Length == 0) {
            throw new FileNotFoundException($"built image is missing or empty: '{image}'.");
        }

        var format = image.EndsWith(".wim", StringComparison.OrdinalIgnoreCase)
            ? OutputFormat.Wim
            : image.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)
                ? OutputFormat.Esd
                : throw new ArgumentException("--install-image must be a .wim or .esd file",
                    nameof(request.InstallImage));
        if (!output.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("--output must end with .iso", nameof(request.Output));
        }

        if (File.Exists(output) && !request.Overwrite) {
            throw new OutputExistsException($"ISO already exists: '{output}' (pass --overwrite to replace it).");
        }

        if (!File.Exists(oscdimg)) {
            throw new FileNotFoundException($"oscdimg.exe not found: '{oscdimg}'.");
        }

        if (unattended is not null && !File.Exists(unattended)) {
            throw new FileNotFoundException($"unattended file not found: '{unattended}'.");
        }

        if (Directory.Exists(workspace) && Directory.EnumerateFileSystemEntries(workspace).Any()) {
            throw new WorkspaceConflictException($"package workspace '{workspace}' is not empty; choose a new directory.");
        }

        var resolver = new SourceImageResolver(new ProcessRunner(), new());
        var source = await resolver.ResolveAsync(input, CancellationToken.None);
        try {
            if (!source.HasMediaTree) {
                throw new ArgumentException("--input for package iso must be an ISO or media directory.",
                    nameof(request.Input));
            }

            await new SetupImageContractValidator(new ProcessRunner(), new())
                .ValidateAsync(image, 1, CancellationToken.None);

            Directory.CreateDirectory(workspace);
            var media = Path.Combine(workspace, "media");
            var builder = new OutputBuilder(new ProcessRunner(), new());
            await builder.StageMediaAsync(source.MediaRootPath!, media, image, format, request.Overwrite,
                CancellationToken.None);
            if (unattended is not null) {
                File.Copy(unattended, Path.Combine(media, "Autounattend.xml"), true);
                Console.WriteLine($"unattended: {unattended}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await builder.CreateIsoAsync(media, output, oscdimg, CancellationToken.None);
            Console.WriteLine($"ISO: {output}");
            Console.WriteLine($"media workspace: {workspace}");
            return ExitCodes.Success;
        }
        finally {
            await resolver.DismountIsoAsync(source, CancellationToken.None);
        }
    }
}

internal static class LayerHandler {
    public static int List(LayerListRequest request) {
        var log = new BuildLog();
        var (_, _, layers) = Cli.CreateEngineParts();
        return PrintList(layers, log, request.Workspace, request.Json);
    }

    public static async Task<int> Diff(LayerDiffRequest request) {
        var log = new BuildLog();
        var (runner, _, layers) = Cli.CreateEngineParts();
        var inspector = new LayerInspector(runner, layers, log);
        var report = await inspector.DiffAsync(request.Workspace, request.From, request.To);
        if (request.Json) {
            Console.WriteLine(report.ToJson().ToJsonString(Cli.JsonSerializerOptions));
            return ExitCodes.Success;
        }

        Console.WriteLine(
            $"layer {report.FromIndex:000} → {report.ToIndex:000}: {report.Files.Count} file changes, {report.Registry.Count} registry changes");
        foreach (var file in report.Files.Take(40)) {
            Console.WriteLine($"  [{file.Kind,-8}] {file.RelativePath}  ({file.OldSize} → {file.NewSize} bytes)");
        }

        if (report.Files.Count > 40) {
            Console.WriteLine($"  … {report.Files.Count - 40} more");
        }

        foreach (var entry in report.Registry.Take(40)) {
            Console.WriteLine($"  [{entry.Kind,-8}] {entry.Hive}\\{entry.Key}\\{entry.ValueName}");
            if (entry.Before is not null) {
                Console.WriteLine($"              - {entry.Before}");
            }

            if (entry.After is not null) {
                Console.WriteLine($"              + {entry.After}");
            }
        }

        if (report.Registry.Count > 40) {
            Console.WriteLine($"  … {report.Registry.Count - 40} more");
        }

        return ExitCodes.Success;
    }

    public static async Task<int> Extract(LayerExtractRequest request) {
        var log = new BuildLog();
        var (runner, _, layers) = Cli.CreateEngineParts();
        var inspector = new LayerInspector(runner, layers, log);
        await inspector.ExtractAsync(request.Workspace, request.Layer, request.ImagePath, request.Destination,
            CancellationToken.None);
        Console.WriteLine("extracted.");
        return ExitCodes.Success;
    }

    public static async Task<int> Capture(LayerCaptureRequest request) {
        var log = new BuildLog();
        var (runner, _, layers) = Cli.CreateEngineParts();
        var inspector = new LayerInspector(runner, layers, log);
        var captured = await inspector.RollbackCaptureAsync(request.Workspace, request.Layer, request.Output,
            request.Format, request.Export, CancellationToken.None);
        Console.WriteLine($"captured layer state → {captured}");
        return ExitCodes.Success;
    }

    private static int PrintList(ILayerBackend layers, BuildLog log, string workDirectory, bool json) {
        var stack = VhdLayerStack.Load(workDirectory, layers, log);
        if (json) {
            Console.WriteLine(new JsonObject {
                ["layers"] = new JsonArray(stack.Records.Select(r => (JsonNode)r.ToJson()).ToArray())
            }.ToJsonString(Cli.JsonSerializerOptions));
            return ExitCodes.Success;
        }

        Console.WriteLine($"layer chain in {workDirectory}");
        Console.WriteLine();
        Console.WriteLine($"{"idx",-5} {"status",-10} {"size",-12} {"step",-28} title");
        foreach (var record in stack.Records) {
            Console.WriteLine(
                $"{record.Index,-5} {record.Status.ToString().ToLowerInvariant(),-10} {record.SizeBytes / 1024.0 / 1024,-12:F1} MB {Cli.Truncate(record.StepId ?? "-", 28),-28} {record.Title}");
            if (!string.IsNullOrEmpty(record.Error)) {
                Console.WriteLine($"       error: {record.Error}");
            }
        }

        return ExitCodes.Success;
    }
}
