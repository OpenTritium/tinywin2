namespace TinyWin2.Core.Plans;

/// <summary>Merges explicit plan additions without silently overriding profile safety exclusions.</summary>
public static class PlanSelectionMerger {
    public static List<PlanSelection> Merge(
        IReadOnlyList<PlanSelection> profileSelections,
        IEnumerable<PlanSelection> explicitSelections) {
        var result = profileSelections.ToList();
        foreach (var selection in explicitSelections) {
            var existing = result.FindIndex(item => item.PlanId == selection.PlanId);
            if (existing < 0) {
                result.Add(selection);
                continue;
            }

            if (!result[existing].Enabled && selection.Enabled) {
                throw new InvalidOperationException(
                    $"plan '{selection.PlanId}' is disabled by the selected profile; " +
                    "remove the profile exclusion before enabling it explicitly.");
            }
        }

        return result;
    }
}
