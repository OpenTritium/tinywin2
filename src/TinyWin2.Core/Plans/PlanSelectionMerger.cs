using System.Text.Json.Nodes;

namespace TinyWin2.Core.Plans;

/// <summary>Merges layered profile selections without silently overriding safety exclusions.</summary>
public static class PlanSelectionMerger {
    /// <summary>
    ///     Folds profile layers (later files override earlier ones) and explicit CLI selections
    ///     into one selection set: absent ids are appended, later entries override earlier
    ///     enabled state and parameters, and any attempt to resurrect a plan a layer disabled
    ///     is rejected. Unknown ids fail here so callers share one vocabulary.
    /// </summary>
    public static List<PlanSelection> Merge(
        IReadOnlyList<PlanSelection> profileSelections,
        IEnumerable<PlanSelection> explicitSelections,
        PlanCatalog catalog) {
        var result = new List<PlanSelection>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var selection in profileSelections.Concat(explicitSelections)) {
            EnsureKnown(catalog, selection.PlanId);
            if (index.TryGetValue(selection.PlanId, out var existing)) {
                if (!result[existing].Enabled && selection.Enabled) {
                    throw ProfileExclusionConflict(selection.PlanId);
                }

                result[existing] = selection.Parameters is null
                    ? selection with { Parameters = result[existing].Parameters }
                    : selection;
                continue;
            }

            index[selection.PlanId] = result.Count;
            result.Add(selection);
        }

        return result;
    }

    /// <summary>
    ///     Ensures one explicitly requested plan (e.g. the target of a --set assignment)
    ///     participates in the selection set: appended when absent, rejected when disabled.
    /// </summary>
    public static void EnsureSelected(List<PlanSelection> selections, PlanCatalog catalog, string planId) {
        EnsureKnown(catalog, planId);
        var existing = Find(selections, planId);
        if (existing < 0) {
            selections.Add(new(planId));
            return;
        }

        if (!selections[existing].Enabled) {
            throw ProfileExclusionConflict(planId);
        }
    }

    /// <summary>
    ///     Applies one <c>plan.parameter=value</c> assignment in place: the target plan is
    ///     ensured to participate, the raw CLI text is parsed into a JSON value, and the
    ///     selection's parameter map is replaced (the map handed in by a profile is copied,
    ///     never mutated through the record).
    /// </summary>
    public static void WithParameter(List<PlanSelection> selections, PlanCatalog catalog, string assignment) {
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
        EnsureSelected(selections, catalog, planId);
        var index = Find(selections, planId);
        var parameters = selections[index].Parameters is { } existing
            ? new Dictionary<string, JsonNode?>(existing, StringComparer.Ordinal)
            : new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        parameters[parameterName] = ParseValue(assignment[(separator + 1)..]);
        selections[index] = selections[index] with { Parameters = parameters };
    }

    private static JsonNode ParseValue(string text) =>
        text.Trim() switch {
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ when int.TryParse(text, out var number) => JsonValue.Create(number),
            _ => JsonValue.Create(text)
        };

    private static int Find(List<PlanSelection> selections, string planId) =>
        selections.FindIndex(item => item.PlanId == planId);

    private static void EnsureKnown(PlanCatalog catalog, string planId) {
        if (!catalog.ById.ContainsKey(planId)) {
            throw new ArgumentException($"unknown plan '{planId}' (see: tinywin2 plan list)");
        }
    }

    private static InvalidOperationException ProfileExclusionConflict(string planId) =>
        new($"plan '{planId}' is disabled by the selected profile; " +
            "remove the profile exclusion before enabling it explicitly.");
}
