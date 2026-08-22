using System.Text.Json.Nodes;
using TinyWin2.Core;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;

namespace TinyWin2.Cli.Commands;

internal static class BuildCommand {
    public static async Task<int> RunAsync(List<string> args) {
        var options = Program.ParseOptions(args);

        string? Get(string name) {
            return options.GetValueOrDefault(name)?.FirstOrDefault();
        }

        var sourcePath = Get("s") ?? Get("source")
            ?? throw new ArgumentException("missing --s <iso|folder>");
        if (!int.TryParse(Get("i") ?? Get("index"), out var imageIndex)) {
            throw new ArgumentException("missing --i <image index> (see: tinywin2 inspect)");
        }

        var outputRoot = Path.GetFullPath(Get("o") ?? Get("out-dir") ?? "out");
        var requestedOutput = Get("out") ?? Get("out-mode");
        var outputFormat = (requestedOutput ?? "esd").ToLowerInvariant() switch {
            "wim" => OutputFormat.Wim,
            "esd" => OutputFormat.Esd,
            "vhdx" => OutputFormat.Vhdx,
            // Compatibility alias: the old default was an ESD-backed ISO.
            "iso" => OutputFormat.Esd,
            "iso+vhdx" or "iso-vhdx" => throw new ArgumentException(
                "'iso+vhdx' was replaced by separate outputs; use --out vhdx or --out esd --iso"),
            var unknown => throw new ArgumentException($"unknown --out '{unknown}' (wim|esd|vhdx)")
        };
        var createIso = !options.ContainsKey("no-iso")
                        && (options.ContainsKey("iso")
                            || requestedOutput is null
                            || string.Equals(requestedOutput, "iso", StringComparison.OrdinalIgnoreCase));
        var baseVhdxMaximumMb = ParseBaseVhdxMaximumMb(Get("base-vhdx-mb") ?? Get("base-size"));
        var plansDir = Cli.FindPlansDirectory(Get("plans"));
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var selections = Cli.BuildSelections(options, catalog);
        var jsonEvents = options.ContainsKey("json-events");
        // Serilog owns console + file output; the JSONL event stream owns stdout in --json-events mode.
        var log = new BuildLog();
        if (jsonEvents) {
            _ = log.Attach(evt => Console.Out.WriteLine(evt.ToJson().ToCompactString()));
        }

        var resume = FindResumeWorkspace(options, outputRoot);
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
                OutputFormat = outputFormat,
                CreateIso = createIso,
                Fast = options.ContainsKey("fast"),
                ContinueOnError = options.ContainsKey("continue-on-error"),
                NoLayers = options.ContainsKey("no-layers"),
                KeepLayers = options.ContainsKey("keep-layers"),
                DryRun = options.ContainsKey("dry-run"),
                CaptureEvidence = !options.ContainsKey("no-evidence"),
                ResumeWorkspace = resume,
                OscdimgPath = Get("oscdimg"),
                PlansDirectory = plansDir,
                BaseVhdxMaximumMb = baseVhdxMaximumMb
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
                        ["createIso"] = createIso,
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
    private static string? FindResumeWorkspace(Dictionary<string, List<string>> options, string outputRoot) {
        if (!options.TryGetValue("resume", out var values)) {
            return null;
        }

        var explicitPath = values.FirstOrDefault();
        if (explicitPath is not null && File.Exists(Path.Combine(explicitPath, "layers.json"))) {
            return Path.GetFullPath(explicitPath);
        }

        var workRoot = Path.Combine(outputRoot, "work");
        var candidate = new DirectoryInfo(workRoot)
            .GetDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, "layers.json")))
            .OrderByDescending(d => d.CreationTimeUtc)
            .FirstOrDefault();
        return candidate?.FullName
               ?? throw new DirectoryNotFoundException(
                   $"no resumable workspace under '{workRoot}' (builds must keep layers: add --keep-layers, or pass --resume <workspace>)");
    }

    private static long ParseBaseVhdxMaximumMb(string? value) {
        if (value is null) {
            return BuildOptions.DefaultBaseVhdxMaximumMb;
        }

        return long.TryParse(value, out var size) && size > 0
            ? size
            : throw new ArgumentException($"invalid base VHDX size '{value}' (must be a positive number of MB)");
    }
}

internal static class PreviewCommand {
    public static async Task<int> RunAsync(List<string> args) {
        var options = Program.ParseOptions(args);

        string? Get(string name) {
            return options.GetValueOrDefault(name)?.FirstOrDefault();
        }

        var sourcePath = Get("s") ?? Get("source") ?? throw new ArgumentException("missing --s <iso|folder>");
        if (!int.TryParse(Get("i") ?? Get("index"), out var imageIndex)) {
            throw new ArgumentException("missing --i <image index>");
        }

        var plansDir = Cli.FindPlansDirectory(Get("plans"));
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var selections = Cli.BuildSelections(options, catalog);
        var baseVhdxMaximumMb = ParseBaseVhdxMaximumMb(Get("base-vhdx-mb") ?? Get("base-size"));
        var json = options.ContainsKey("json");
        var log = new BuildLog();
        var (runner, executers, layers) = Cli.CreateEngineParts();
        var previewer = new PreviewRunner(runner, executers, layers, log);
        var workDirectory = Path.Combine(Path.GetFullPath(Get("o") ?? "out"), "work", "preview");
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
                BaseVhdxMaximumMb = baseVhdxMaximumMb,
                PlansDirectory = plansDir
            }, cts.Token);
            if (json) {
                Console.WriteLine(new JsonObject {
                    ["plans"] = new JsonArray(previews.Select(p => (JsonNode)p.ToJson()).ToArray())
                }.ToJsonString(DoctorCommand.JsonSerializerOptions));
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

    private static long ParseBaseVhdxMaximumMb(string? value) {
        if (value is null) {
            return BuildOptions.DefaultBaseVhdxMaximumMb;
        }

        return long.TryParse(value, out var size) && size > 0
            ? size
            : throw new ArgumentException($"invalid base VHDX size '{value}' (must be a positive number of MB)");
    }
}

internal static class LayerCommand {
    public static async Task<int> RunAsync(List<string> args) {
        var options = Program.ParseOptions(args);
        var positional = args.Where(a => !a.StartsWith("--")).ToList();
        if (positional.Count == 0) {
            await Console.Error.WriteLineAsync("""
                                               usage: tinywin2 layer list    <workspace> [--json]
                                                      tinywin2 layer diff    <workspace> <from> <to> [--json]
                                                      tinywin2 layer extract <workspace> <layer> <image-relative-path> <dest>
                                                      tinywin2 layer rollback-to <workspace> <layer> -o <out.wim|esd> [--fast]
                                               """);
            return 2;
        }

        var workDirectory = positional.Count > 1 ? positional[1] : throw new ArgumentException("missing <workspace>");
        var log = new BuildLog();
        var (runner, _, layers) = Cli.CreateEngineParts();
        var inspector = new LayerInspector(runner, layers, log);
        switch (positional[0]) {
            case "list":
                return List(layers, log, workDirectory, options.ContainsKey("json"));
            case "diff": {
                if (positional.Count < 4) {
                    await Console.Error.WriteLineAsync("usage: tinywin2 layer diff <workspace> <from> <to> [--json]");
                    return 2;
                }

                var report =
                    await inspector.DiffAsync(workDirectory, int.Parse(positional[2]), int.Parse(positional[3]));
                if (options.ContainsKey("json")) {
                    Console.WriteLine(report.ToJson().ToJsonString(DoctorCommand.JsonSerializerOptions));
                }
                else {
                    Console.WriteLine(
                        $"layer {report.FromIndex:000} → {report.ToIndex:000}: {report.Files.Count} file changes, {report.Registry.Count} registry changes");
                    foreach (var file in report.Files.Take(40)) {
                        Console.WriteLine(
                            $"  [{file.Kind,-8}] {file.RelativePath}  ({file.OldSize} → {file.NewSize} bytes)");
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
                }

                return 0;
            }
            case "extract": {
                if (positional.Count < 5) {
                    await Console.Error.WriteLineAsync(
                        "usage: tinywin2 layer extract <workspace> <layer> <image-relative-path> <dest>");
                    return 2;
                }

                await inspector.ExtractAsync(workDirectory, int.Parse(positional[2]), positional[3], positional[4],
                    CancellationToken.None);
                Console.WriteLine("extracted.");
                return 0;
            }
            case "rollback-to": {
                var output = options.GetValueOrDefault("out")?.FirstOrDefault() ??
                             options.GetValueOrDefault("o")?.FirstOrDefault();
                if (positional.Count < 3 || output is null) {
                    await Console.Error.WriteLineAsync(
                        "usage: tinywin2 layer rollback-to <workspace> <layer> -o <out.wim|esd> [--fast]");
                    return 2;
                }

                var format = output.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)
                    ? OutputFormat.Esd
                    : OutputFormat.Wim;
                var captured = await inspector.RollbackCaptureAsync(workDirectory, int.Parse(positional[2]), output,
                    format,
                    options.ContainsKey("fast"), CancellationToken.None);
                Console.WriteLine($"captured layer state → {captured}");
                return 0;
            }
            default:
                await Console.Error.WriteLineAsync($"unknown layer subcommand: {positional[0]}");
                return 2;
        }
    }

    private static int List(ILayerBackend layers, BuildLog log, string workDirectory, bool json) {
        var stack = VhdLayerStack.Load(workDirectory, layers, log);
        if (json) {
            Console.WriteLine(new JsonObject {
                ["layers"] = new JsonArray(stack.Records.Select(r => (JsonNode)r.ToJson()).ToArray())
            }.ToJsonString(DoctorCommand.JsonSerializerOptions));
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
