using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Converges optional features. absent = disable (optionally removing payload).</summary>
public sealed class FeatureExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    public const string ResourceId = "dism.feature";
    public override string Resource => ResourceId;

    protected override IReadOnlyList<string> ListArguments => ["/Get-Features", "/Format:List"];

    protected override DismOutcome? DowngradeOutcome => DismOutcome.InvalidInstallState;

    protected override string SatisfiedSkipReason => "features already absent or unavailable";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<Dictionary<string, string>> records,
        ExecContext context,
        ExecSpec spec) {
        var options = FeatureOptions.FromDesired(spec.Desired);
        var states = records.ToDictionary(
            r => DismListParser.Get(r, "Feature Name") ?? "",
            r => DismListParser.Get(r, "State") ?? "",
            StringComparer.OrdinalIgnoreCase);
        foreach (var feature in options.Features) {
            if (!states.TryGetValue(feature, out var state)) {
                context.Log.Info($"feature '{feature}' is not present in this image; skipping.");
                yield return new DismRemovalTarget(feature, SkipReason: "feature not present in image");
            }
            else if (state.Contains("Removed", StringComparison.OrdinalIgnoreCase)
                     || (state.Contains("Disabled", StringComparison.OrdinalIgnoreCase) && !options.RemovePayload)) {
                // Already in the desired state: no difference entry at all.
            }
            else {
                yield return new DismRemovalTarget(feature, Before: state);
            }
        }
    }

    protected override IReadOnlyList<string> RemoveArguments(DismRemovalTarget target, ExecSpec spec) {
        var args = new List<string> { "/Disable-Feature", $"/FeatureName:{target.RemoveKey}", "/NoRestart" };
        if (FeatureOptions.FromDesired(spec.Desired).RemovePayload) {
            args.Add("/Remove");
        }
        return args;
    }
}
