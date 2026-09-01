using System.Collections.Frozen;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Converges optional features: absent = disable (optionally removing payload), apply = enable.</summary>
public sealed class FeatureExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    private const string ResourceId = "dism.feature";
    public override string Resource => ResourceId;

    protected override bool SupportsApply => true;

    /// <summary>Enabling needs the feature payload, which cleanup plans may have stripped.</summary>
    protected override FrozenSet<int> ApplyPayloadMissingExitCodes =>
        FrozenSet.ToFrozenSet([DismErrors.CbsESourceMissing, DismErrors.CbsESourceNotDownloadable]);

    protected override IReadOnlyList<string> ListArguments => ["/Get-Features", "/Format:List"];

    protected override string RecordStartKey => "Feature Name";

    protected override DismOutcome? DowngradeOutcome => DismOutcome.InvalidInstallState;

    protected override string? BatchableTargetSwitch => "/FeatureName";

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
                if (operation.Action == OperationAction.Apply) {
                    // nothing to enable: the feature does not exist in this edition
                    context.Log.Info($"feature '{feature}' is not present in this image; skipping.");
                    yield return new(feature, SkipReason: "feature not present in image");
                    continue;
                }

                if (options.ForceExplicit) {
                    context.Log.Info($"feature '{feature}' is not listed; attempting explicit removal.");
                    yield return new(feature, Before: "feature not listed");
                }
                else {
                    context.Log.Info($"feature '{feature}' is not present in this image; skipping.");
                    yield return new(feature, SkipReason: "feature not present in image");
                }
            }
            else if (operation.Action == OperationAction.Apply) {
                if (state.Contains("Enabled", StringComparison.OrdinalIgnoreCase)) {
                    // already in the desired state: no difference entry at all.
                }
                else {
                    yield return new(feature, state);
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
