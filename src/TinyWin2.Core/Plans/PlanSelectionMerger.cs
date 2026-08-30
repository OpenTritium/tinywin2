namespace TinyWin2.Core.Plans;

/// <summary>Merges explicit plan additions without silently overriding profile safety exclusions.</summary>
public static class PlanSelectionMerger {
    /// <summary>
    ///     Appends explicit selections to the profile's, rejecting any attempt to resurrect a
    ///     plan the profile disabled. Unknown ids fail here so callers share one vocabulary.
    /// </summary>
    public static List<PlanSelection> Merge(
        IReadOnlyList<PlanSelection> profileSelections,
        IEnumerable<PlanSelection> explicitSelections,
        PlanCatalog catalog) {
        var result = profileSelections.ToList();
        foreach (var selection in explicitSelections) {
            EnsureKnown(catalog, selection.PlanId);
            var existing = Find(result, selection.PlanId);
            if (existing < 0) {
                result.Add(selection);
                continue;
            }

            if (!result[existing].Enabled && selection.Enabled) {
                throw ProfileExclusionConflict(selection.PlanId);
            }
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
