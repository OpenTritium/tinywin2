using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Converges optional features. absent = disable (optionally removing payload).</summary>
public sealed class FeatureExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    private const string ResourceId = "dism.feature";
    public override string Resource => ResourceId;

    protected override IReadOnlyList<string> ListArguments => ["/Get-Features", "/Format:List"];

    protected override string RecordStartKey => "Feature Name";

    protected override DismOutcome? DowngradeOutcome => DismOutcome.InvalidInstallState;

    protected override object BindOptions(OperationSpec spec) => FeatureOptions.FromDesired(spec.Spec);

    protected override string SatisfiedSkipReason => "features already absent or unavailable";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<IReadOnlyDictionary<string, string>> records,
        ExecContext context,
        BoundOperation operation) {
        var options = (FeatureOptions)operation.Options;
        var states = records.ToDictionary(
            r => DismListParser.Get(r, "Feature Name") ?? "",
            r => DismListParser.Get(r, "State") ?? "",
            StringComparer.OrdinalIgnoreCase);
        foreach (var feature in options.Features) {
            if (!states.TryGetValue(feature, out var state)) {
                if (options.ForceExplicit) {
                    context.Log.Info($"feature '{feature}' is not listed; attempting explicit removal.");
                    yield return new(feature, Before: "feature not listed");
                }
                else {
                    context.Log.Info($"feature '{feature}' is not present in this image; skipping.");
                    yield return new(feature, SkipReason: "feature not present in image");
                }
            }
            else if (state.Contains("Removed", StringComparison.OrdinalIgnoreCase)
                     || (state.Contains("Disabled", StringComparison.OrdinalIgnoreCase) && !options.RemovePayload)) {
                // Already in the desired state: no difference entry at all.
            }
            else {
                yield return new(feature, state);
            }
        }
    }

    protected override IReadOnlyList<string> RemoveArguments(BoundOperation operation, DismRemovalTarget target) {
        var args = new List<string> { "/Disable-Feature", $"/FeatureName:{target.RemoveKey}", "/NoRestart" };
        if (((FeatureOptions)operation.Options).RemovePayload) {
            args.Add("/Remove");
        }

        return args;
    }
}
