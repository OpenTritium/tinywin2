using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Cli;

/// <summary>Shared CLI plumbing: repo/plans directory discovery, selection parsing, engine wiring.</summary>
internal static class Cli {
    public static string FindPlansDirectory(string? explicitPath) {
        if (explicitPath is not null) {
            return Directory.Exists(explicitPath)
                ? Path.GetFullPath(explicitPath)
                : throw new DirectoryNotFoundException($"plans directory not found: {explicitPath}");
        }
        return PlansDirectoryLocator.TryLocate()
               ?? throw new DirectoryNotFoundException("could not locate a 'plans' directory (pass --plans <dir>).");
    }

    /// <summary>Clips a string to a table column, marking the cut with an ellipsis.</summary>
    public static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    /// <summary>Builds the effective selection list from --profile, --plan and --set options.</summary>
    public static List<PlanSelection> BuildSelections(Dictionary<string, List<string>> options, PlanCatalog catalog) {
        var selections = new List<PlanSelection>();
        if (options.TryGetValue("profile", out var profilePaths)) {
            foreach (var profilePath in profilePaths) {
                var profile = Core.Profiles.ProfileStore.Load(profilePath);
                var unknown = Core.Profiles.ProfileStore.UnknownPlans(profile, catalog);
                if (unknown.Count > 0) {
                    throw new InvalidOperationException(
                        $"profile '{profilePath}' references unknown plans: {string.Join(", ", unknown)}");
                }
                selections.AddRange(Core.Profiles.ProfileStore.ToPlanSelections(profile).Where(s => s.Enabled));
            }
        }
        void EnsureSelected(string planId) {
            if (!catalog.ById.ContainsKey(planId)) {
                throw new ArgumentException($"unknown plan '{planId}' (see: tinywin2 plan list)");
            }
            if (selections.All(s => s.PlanId != planId)) {
                selections.Add(new PlanSelection(planId));
            }
        }
        if (options.TryGetValue("plan", out var planIds)) {
            foreach (var planId in planIds) {
                EnsureSelected(planId);
            }
        }
        if (options.TryGetValue("set", out var sets)) {
            foreach (var set in sets) {
                var separator = set.IndexOf('=');
                if (separator <= 0) {
                    throw new ArgumentException($"--set expects planId.arg=value, got '{set}'");
                }
                var target = set[..separator];
                var dot = target.LastIndexOf('.');
                if (dot <= 0) {
                    throw new ArgumentException($"--set expects planId.arg=value, got '{set}'");
                }
                var planId = target[..dot];
                var argName = target[(dot + 1)..];
                var value = ParseValue(set[(separator + 1)..]);
                EnsureSelected(planId);
                var index = selections.FindIndex(s => s.PlanId == planId);
                var args = selections[index].Args as IDictionary<string, JsonNode?> ?? new Dictionary<string, JsonNode?>();
                args[argName] = value;
                selections[index] = selections[index] with { Args = (IReadOnlyDictionary<string, JsonNode?>?)args };
            }
        }
        if (selections.Count == 0) {
            throw new ArgumentException("no plans selected: pass --profile, --plan and/or --set.");
        }
        return selections;
    }

    private static JsonNode ParseValue(string text) =>
        text.Trim() switch {
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when int.TryParse(text, out var number) => JsonValue.Create(number),
            _ => JsonValue.Create(text),
        };

    public static (IProcessRunner Runner, ExecuterRegistry Executers, ILayerBackend Layers) CreateEngineParts() {
        var runner = new ProcessRunner();
        return (runner, new ExecuterRegistry(runner), LayerBackendFactory.Create(runner));
    }
}
