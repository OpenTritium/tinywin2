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
        var get = (string name) => options.GetValueOrDefault(name)?.FirstOrDefault();
        var sourcePath = get("s") ?? get("source")
            ?? throw new ArgumentException("missing --s <iso|folder>");
        if (!int.TryParse(get("i") ?? get("index"), out var imageIndex)) {
            throw new ArgumentException("missing --i <image index> (see: tinywin2 inspect)");
        }
        var outputRoot = Path.GetFullPath(get("o") ?? get("out-dir") ?? "out");
        var outputMode = (get("out") ?? get("out-mode") ?? "iso").ToLowerInvariant() switch {
            "wim" => OutputMode.Wim,
            "esd" => OutputMode.Esd,
            "iso" => OutputMode.Iso,
            "iso+vhdx" or "iso-vhdx" => OutputMode.IsoAndVhdx,
            var unknown => throw new ArgumentException($"unknown --out-mode '{unknown}' (wim|esd|iso|iso+vhdx)"),
        };
        var granularity = (get("granularity") ?? "group").ToLowerInvariant() switch {
            "group" => LayerGranularity.Group,
            "plan" => LayerGranularity.Plan,
            var unknown => throw new ArgumentException($"unknown --granularity '{unknown}' (group|plan)"),
        };
        var plansDir = Cli.FindPlansDirectory(get("plans"));
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var selections = Cli.BuildSelections(options, catalog);
        var jsonEvents = options.ContainsKey("json-events");
        // Serilog owns console + file output; the JSONL event stream owns stdout in --json-events mode.
        var log = new BuildLog();
        if (jsonEvents) {
            _ = log.Attach(evt => Console.Out.WriteLine(evt.ToJson().ToCompactString()));
        }
        var logDirectory = Path.Combine(outputRoot, "logs");
        Directory.CreateDirectory(logDirectory);
        var logFilePath = Path.Combine(logDirectory, $"tinywin2-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}.log");
        using var serilog = log.UseSerilog(logFilePath, echoConsole: !jsonEvents);
        var (runner, executers, layers) = Cli.CreateEngineParts();
        var engine = new BuildEngine(runner, executers, layers, log);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            log.Warn("cancellation requested; rolling back the current layer…");
            cts.Cancel();
        };
        try {
            var result = await engine.BuildAsync(new BuildOptions {
                SourcePath = sourcePath,
                ImageIndex = imageIndex,
                Selections = selections,
                OutputRoot = outputRoot,
                Catalog = catalog,
                OutputMode = outputMode,
                Granularity = granularity,
                Fast = options.ContainsKey("fast"),
                ContinueOnError = options.ContainsKey("continue-on-error"),
                KeepLayers = options.ContainsKey("keep-layers"),
                DryRun = options.ContainsKey("dry-run"),
                OscdimgPath = get("oscdimg"),
                PlansDirectory = plansDir,
            }, cts.Token);
            if (jsonEvents) {
                Console.Out.WriteLine(new JsonObject {
                    ["seq"] = -1,
                    ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["level"] = "info",
                    ["phase"] = "result",
                    ["message"] = "build finished",
                    ["data"] = new JsonObject {
                        ["succeeded"] = result.Succeeded,
                        ["mediaPath"] = result.MediaPath,
                        ["isoPath"] = result.IsoPath,
                        ["vhdxPath"] = result.VhdxPath,
                        ["manifestPath"] = result.ManifestPath,
                        ["layerCount"] = result.LayerCount,
                        ["failedStepId"] = result.FailedStepId,
                    },
                }.ToJsonString());
            }
            else {
                Console.WriteLine();
                Console.WriteLine($"✔ build {result.BuildId} complete");
                Console.WriteLine($"  media:     {result.MediaPath}");
                if (result.IsoPath is not null) {
                    Console.WriteLine($"  ISO:       {result.IsoPath}");
                }
                if (result.VhdxPath is not null) {
                    Console.WriteLine($"  VHDX:      {result.VhdxPath}");
                }
                Console.WriteLine($"  manifest:  {result.ManifestPath}");
                Console.WriteLine($"  日志:      {logFilePath}");
                Console.WriteLine($"  layers:    {result.LayerCount}");
            }
            return 0;
        }
        catch (BuildStepFailedException ex) {
            if (!jsonEvents) {
                Console.Error.WriteLine($"✘ step '{ex.StepId}' failed at layer {ex.LayerIndex:000}; the layer was discarded and the workspace kept.");
                Console.Error.WriteLine($"  post-mortem: tinywin2 layer diff <workspace> {Math.Max(0, ex.LayerIndex - 1)} {ex.LayerIndex}");
                Console.Error.WriteLine($"  retry without the offender, e.g. add: --plan ... minus {ex.StepId}");
            }
            return 1;
        }
        catch (OperationCanceledException) {
            Console.Error.WriteLine("cancelled.");
            return 130;
        }
    }
}

internal static class PreviewCommand {
    public static async Task<int> RunAsync(List<string> args) {
        var options = Program.ParseOptions(args);
        var get = (string name) => options.GetValueOrDefault(name)?.FirstOrDefault();
        var sourcePath = get("s") ?? get("source") ?? throw new ArgumentException("missing --s <iso|folder>");
        if (!int.TryParse(get("i") ?? get("index"), out var imageIndex)) {
            throw new ArgumentException("missing --i <image index>");
        }
        var plansDir = Cli.FindPlansDirectory(get("plans"));
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var selections = Cli.BuildSelections(options, catalog);
        var json = options.ContainsKey("json");
        var log = new BuildLog();
        var (runner, executers, layers) = Cli.CreateEngineParts();
        var previewer = new PreviewRunner(runner, executers, layers, log);
        var workDirectory = Path.Combine(Path.GetFullPath(get("o") ?? "out"), "work", "preview");
        var previewLogDirectory = Path.Combine(Path.GetDirectoryName(workDirectory)!, "logs");
        Directory.CreateDirectory(previewLogDirectory);
        using var previewSerilog = log.UseSerilog(
            Path.Combine(previewLogDirectory, $"tinywin2-preview-{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}.log"),
            echoConsole: !json);
        var previews = await previewer.RunAsync(new PreviewOptions {
            SourcePath = sourcePath,
            ImageIndex = imageIndex,
            Selections = selections,
            WorkDirectory = workDirectory,
            Catalog = catalog,
            PlansDirectory = plansDir,
        }, CancellationToken.None);
        if (json) {
            Console.WriteLine(new JsonObject {
                ["plans"] = new JsonArray(previews.Select(p => (JsonNode)p.ToJson()).ToArray()),
            }.ToJsonString(DoctorCommand.JsonSerializerOptions));
        }
        else {
            Console.WriteLine();
            Console.WriteLine($"{"plan",-46} {"state",-8} changes");
            foreach (var preview in previews) {
                Console.WriteLine($"{preview.PlanId,-46} {(preview.Satisfied ? "no-op" : "will-do"),-8} {preview.Differences.Count}");
                foreach (var difference in preview.Differences.Take(8)) {
                    Console.WriteLine($"    [{difference.Kind}] {difference.Target}");
                }
                if (preview.Differences.Count > 8) {
                    Console.WriteLine($"    … {preview.Differences.Count - 8} more");
                }
            }
        }
        try { Directory.Delete(workDirectory, recursive: true); }
        catch { /* the preview base layer may be worth keeping; ignore */ }
        return 0;
    }
}

internal static class LayerCommand {
    public static async Task<int> RunAsync(List<string> args) {
        var options = Program.ParseOptions(args);
        var positional = args.Where(a => !a.StartsWith("--")).ToList();
        if (positional.Count == 0) {
            Console.Error.WriteLine("""
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
                        Console.Error.WriteLine("usage: tinywin2 layer diff <workspace> <from> <to> [--json]");
                        return 2;
                    }
                    var report = await inspector.DiffAsync(workDirectory, int.Parse(positional[2]), int.Parse(positional[3]), CancellationToken.None);
                    if (options.ContainsKey("json")) {
                        Console.WriteLine(report.ToJson().ToJsonString(DoctorCommand.JsonSerializerOptions));
                    }
                    else {
                        Console.WriteLine($"layer {report.FromIndex:000} → {report.ToIndex:000}: {report.Files.Count} file changes, {report.Registry.Count} registry changes");
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
                    }
                    return 0;
                }
            case "extract": {
                    if (positional.Count < 5) {
                        Console.Error.WriteLine("usage: tinywin2 layer extract <workspace> <layer> <image-relative-path> <dest>");
                        return 2;
                    }
                    await inspector.ExtractAsync(workDirectory, int.Parse(positional[2]), positional[3], positional[4], CancellationToken.None);
                    Console.WriteLine("extracted.");
                    return 0;
                }
            case "rollback-to": {
                    var output = options.GetValueOrDefault("out")?.FirstOrDefault() ?? options.GetValueOrDefault("o")?.FirstOrDefault();
                    if (positional.Count < 3 || output is null) {
                        Console.Error.WriteLine("usage: tinywin2 layer rollback-to <workspace> <layer> -o <out.wim|esd> [--fast]");
                        return 2;
                    }
                    var format = output.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Esd : ImageFormat.Wim;
                    var captured = await inspector.RollbackCaptureAsync(workDirectory, int.Parse(positional[2]), output, format,
                        options.ContainsKey("fast"), CancellationToken.None);
                    Console.WriteLine($"captured layer state → {captured}");
                    return 0;
                }
            default:
                Console.Error.WriteLine($"unknown layer subcommand: {positional[0]}");
                return 2;
        }
    }

    private static int List(ILayerBackend layers, BuildLog log, string workDirectory, bool json) {
        var stack = VhdLayerStack.Load(workDirectory, layers, log);
        if (json) {
            Console.WriteLine(new JsonObject {
                ["layers"] = new JsonArray(stack.Records.Select(r => (JsonNode)r.ToJson()).ToArray()),
            }.ToJsonString(DoctorCommand.JsonSerializerOptions));
            return 0;
        }
        Console.WriteLine($"layer chain in {workDirectory}");
        Console.WriteLine();
        Console.WriteLine($"{"idx",-5} {"status",-10} {"size",-12} {"step",-28} title");
        foreach (var record in stack.Records) {
            Console.WriteLine($"{record.Index,-5} {record.Status.ToString().ToLowerInvariant(),-10} {record.SizeBytes / 1024.0 / 1024,-12:F1} MB {Cli.Truncate(record.StepId ?? "-", 28),-28} {record.Title}");
            if (!string.IsNullOrEmpty(record.Error)) {
                Console.WriteLine($"       error: {record.Error}");
            }
        }
        return 0;
    }
}
