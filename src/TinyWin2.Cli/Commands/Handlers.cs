using System.Text.Json.Nodes;
using TinyWin2.Core;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;

namespace TinyWin2.Cli.Commands;

internal static class BuildHandler {
    public static async Task<int> ExecuteAsync(BuildRequest request) {
        if (request.BaseVhdxMaximumMb <= 0) {
            throw new ArgumentException("base VHDX size must be positive");
        }

        var sourcePath = request.Source;
        var imageIndex = request.ImageIndex;
        var outputRoot = Path.GetFullPath(request.OutputDirectory);
        var plansDir = Cli.FindPlansDirectory(request.Selection.PlansDirectory);
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var selections = Cli.BuildSelections(request.Selection, catalog);
        var jsonEvents = request.JsonEvents;
        // Serilog owns console + file output; the JSONL event stream owns stdout in --json-events mode.
        var log = new BuildLog();
        if (jsonEvents) {
            _ = log.Attach(evt => Console.Out.WriteLine(evt.ToJson().ToCompactString()));
        }

        if (request.ResumeLatest && request.ResumeWorkspace is not null) {
            throw new ArgumentException("choose either --resume or --resume-workspace");
        }

        var resume = FindResumeWorkspace(request.ResumeWorkspace, request.ResumeLatest, outputRoot);
        var logDirectory = Path.Combine(outputRoot, "logs");
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
                SourcePath = sourcePath,
                ImageIndex = imageIndex,
                Selections = selections,
                OutputRoot = outputRoot,
                Catalog = catalog,
                OutputFormat = request.Format,
                CreateIso = request.CreateIso,
                Fast = request.Fast,
                ContinueOnError = request.ContinueOnError,
                NoLayers = request.SingleLayer,
                KeepLayers = request.KeepLayers,
                DryRun = request.DryRun,
                CaptureEvidence = !request.SkipEvidence,
                ResumeWorkspace = resume,
                OscdimgPath = request.OscdimgPath,
                PlansDirectory = plansDir,
                BaseVhdxMaximumMb = request.BaseVhdxMaximumMb
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
                        ["createIso"] = request.CreateIso,
                        ["mediaPath"] = result.MediaPath,
                        ["outputPath"] = result.OutputPath,
                        ["isoPath"] = result.IsoPath,
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
                if (result.MediaPath is not null) {
                    Console.WriteLine($"  media:     {result.MediaPath}");
                }

                if (result.IsoPath is not null) {
                    Console.WriteLine($"  ISO:       {result.IsoPath}");
                }

                Console.WriteLine($"  output:    {result.OutputPath}");
                Console.WriteLine($"  manifest:  {result.ManifestPath}");
                Console.WriteLine($"  日志:      {logFilePath}");
                Console.WriteLine($"  layers:    {result.LayerCount}");
            }

            return 0;
        }
        catch (BuildStepFailedException ex) {
            if (!jsonEvents) {
                await Console.Error.WriteLineAsync(
                    $"✘ step '{ex.StepId}' failed at layer {ex.LayerIndex:000}; the layer was discarded and the workspace kept.");
                await Console.Error.WriteLineAsync(
                    $"  post-mortem: tinywin2 layer diff <workspace> {Math.Max(0, ex.LayerIndex - 1)} {ex.LayerIndex}");
                await Console.Error.WriteLineAsync(
                    $"  retry without the offender, e.g. add: --plan ... minus {ex.StepId}");
            }

            return 1;
        }
        catch (OperationCanceledException) {
            await Console.Error.WriteLineAsync("cancelled.");
            return 130;
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

    /// <summary>--resume: reuse the newest workspace whose layer chain survived (--keep-layers), or an explicit path.</summary>
    private static string? FindResumeWorkspace(string? explicitPath, bool latest, string outputRoot) {
        if (!latest && explicitPath is null) {
            return null;
        }

        if (explicitPath is not null) {
            if (File.Exists(Path.Combine(explicitPath, "layers.json"))) {
                return Path.GetFullPath(explicitPath);
            }

            throw new DirectoryNotFoundException($"workspace '{explicitPath}' is not resumable");
        }

        var workRoot = Path.Combine(outputRoot, "work");
        var candidate = new DirectoryInfo(workRoot)
            .GetDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, "layers.json")))
            .OrderByDescending(d => d.CreationTimeUtc)
            .FirstOrDefault();
        return candidate?.FullName
               ?? throw new DirectoryNotFoundException(
                   $"no resumable workspace under '{workRoot}' (builds must keep layers: add --keep-layers, or pass --resume-workspace <workspace>)");
    }
}

internal static class PreviewHandler {
    public static async Task<int> ExecuteAsync(PreviewRequest request) {
        if (request.BaseVhdxMaximumMb <= 0) {
            throw new ArgumentException("base VHDX size must be positive");
        }

        var sourcePath = request.Source;
        var imageIndex = request.ImageIndex;
        var plansDir = Cli.FindPlansDirectory(request.Selection.PlansDirectory);
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var selections = Cli.BuildSelections(request.Selection, catalog);
        var json = request.Json;
        var log = new BuildLog();
        var (runner, executers, layers) = Cli.CreateEngineParts();
        var previewer = new PreviewRunner(runner, executers, layers, log);
        var workDirectory = Path.Combine(Path.GetFullPath(request.OutputDirectory), "work", "preview");
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
                SourcePath = sourcePath,
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

            return 0;
        }
        catch (OperationCanceledException) {
            await Console.Error.WriteLineAsync("preview cancelled.");
            return 130;
        }
        finally {
            Console.CancelKeyPress -= onCancel;
            try {
                Directory.Delete(workDirectory, true);
            }
            catch {
                /* the preview base layer may be worth keeping; ignore */
            }
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
            return 0;
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

        return 0;
    }

    public static async Task<int> Extract(LayerExtractRequest request) {
        var log = new BuildLog();
        var (runner, _, layers) = Cli.CreateEngineParts();
        var inspector = new LayerInspector(runner, layers, log);
        await inspector.ExtractAsync(request.Workspace, request.Layer, request.ImagePath, request.Destination,
            CancellationToken.None);
        Console.WriteLine("extracted.");
        return 0;
    }

    public static async Task<int> Rollback(LayerRollbackRequest request) {
        var log = new BuildLog();
        var (runner, _, layers) = Cli.CreateEngineParts();
        var inspector = new LayerInspector(runner, layers, log);
        var captured = await inspector.RollbackCaptureAsync(request.Workspace, request.Layer, request.Output,
            request.Format, request.Fast, CancellationToken.None);
        Console.WriteLine($"captured layer state → {captured}");
        return 0;
    }

    private static int PrintList(ILayerBackend layers, BuildLog log, string workDirectory, bool json) {
        var stack = VhdLayerStack.Load(workDirectory, layers, log);
        if (json) {
            Console.WriteLine(new JsonObject {
                ["layers"] = new JsonArray(stack.Records.Select(r => (JsonNode)r.ToJson()).ToArray())
            }.ToJsonString(Cli.JsonSerializerOptions));
            return 0;
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

        return 0;
    }
}
