using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Native;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;
using TinyWin2.Core.Profiles;

namespace TinyWin2.Cli;

/// <summary>Shared CLI services: catalog discovery, selection resolution, and engine composition.</summary>
internal static class Cli {
    internal static readonly JsonSerializerOptions JsonSerializerOptions = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FindPlansDirectory(string? explicitPath) {
        if (explicitPath is not null) {
            return Directory.Exists(explicitPath)
                ? Path.GetFullPath(explicitPath)
                : throw new DirectoryNotFoundException($"plans directory not found: {explicitPath}");
        }

        return PlansDirectoryLocator.TryLocate()
               ?? throw new DirectoryNotFoundException(
                   "could not locate a 'plans' directory (pass --plans-dir <dir>).");
    }

    public static List<PlanSelection> BuildSelections(
        SelectionRequest request,
        PlanCatalog catalog) {
        var profileSelections = new List<PlanSelection>();
        foreach (var profilePath in request.Profiles) {
            var profile = ProfileStore.Load(profilePath);
            var unknown = ProfileStore.UnknownPlans(profile, catalog);
            if (unknown.Count > 0) {
                throw new InvalidOperationException(
                    $"profile '{profilePath}' references unknown plans: {string.Join(", ", unknown)}");
            }

            profileSelections.AddRange(profile.Selections);
        }

        var selections = PlanSelectionMerger.Merge(
            profileSelections,
            request.Plans.Select(planId => new PlanSelection(planId)),
            catalog);

        foreach (var assignment in request.Sets) {
            var separator = assignment.IndexOf('=');
            if (separator <= 0) {
                throw new ArgumentException($"--set expects plan.parameter=value, got '{assignment}'");
            }

            var target = assignment[..separator];
            var dot = target.LastIndexOf('.');
            if (dot <= 0) {
                throw new ArgumentException($"--set expects plan.parameter=value, got '{assignment}'");
            }

            var planId = target[..dot];
            var parameterName = target[(dot + 1)..];
            PlanSelectionMerger.EnsureSelected(selections, catalog, planId);
            var index = selections.FindIndex(s => s.PlanId == planId);
            var parameters = selections[index].Parameters as IDictionary<string, JsonNode?>
                             ?? new Dictionary<string, JsonNode?>();
            parameters[parameterName] = ParseValue(assignment[(separator + 1)..]);
            selections[index] = selections[index] with {
                Parameters = (IReadOnlyDictionary<string, JsonNode?>?)parameters
            };
        }

        if (selections.Count == 0 || selections.All(s => !s.Enabled)) {
            throw new ArgumentException("no plans selected: pass --profile, --plan and/or --set.");
        }

        return selections;
    }

    public static OutputFormat ParseOutputFormat(string value) =>
        value.ToLowerInvariant() switch {
            "wim" => OutputFormat.Wim,
            "esd" => OutputFormat.Esd,
            "vhdx" => OutputFormat.Vhdx,
            _ => throw new ArgumentException($"unknown output format '{value}' (wim|esd|vhdx)")
        };

    public static OutputFormat ParseCaptureFormat(string value) =>
        value.ToLowerInvariant() switch {
            "wim" => OutputFormat.Wim,
            "esd" => OutputFormat.Esd,
            _ => throw new ArgumentException($"unknown capture format '{value}' (wim|esd)")
        };

    public static ImageExportOptions ResolveExportOptions(bool fast, string? compression, bool verify,
        bool noVerify, bool checkIntegrity) {
        if (verify && noVerify) {
            throw new ArgumentException("--verify and --no-verify cannot be used together.");
        }

        var options = new ImageExportOptions {
            Compression = fast ? WimCompression.Fast : WimCompression.Max,
            VerifyCapture = !fast,
            CheckIntegrity = checkIntegrity
        };
        if (compression is not null) {
            options = options with { Compression = ParseWimCompression(compression) };
        }

        if (verify) {
            options = options with { VerifyCapture = true };
        }
        else if (noVerify) {
            options = options with { VerifyCapture = false };
        }

        return options;
    }

    private static WimCompression ParseWimCompression(string value) =>
        value.ToLowerInvariant() switch {
            "none" => WimCompression.None,
            "fast" => WimCompression.Fast,
            "max" => WimCompression.Max,
            _ => throw new ArgumentException($"unknown WIM compression '{value}' (none|fast|max)")
        };

    public static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    public static (IProcessRunner Runner, ExecuterRegistry Executers, ILayerBackend Layers) CreateEngineParts() {
        var runner = new ProcessRunner();
        return (runner, new(runner), LayerBackendFactory.Create(runner));
    }

    private static JsonNode ParseValue(string text) =>
        text.Trim() switch {
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when int.TryParse(text, out var number) => JsonValue.Create(number),
            _ => JsonValue.Create(text)
        };
}
