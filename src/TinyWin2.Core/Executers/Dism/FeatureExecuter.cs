using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>
/// Converges optional features. absent = disable (optionally removing payload); the
/// present direction is reserved for future enable support.
/// </summary>
public sealed class FeatureExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    public const string ResourceId = "dism.feature";
    public string Resource => ResourceId;

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException("dism.feature present (enable) is not implemented yet.");
        }
        return InspectAbsentAsync(context, spec, ct);
    }

    private async Task<ResourceDiff> InspectAbsentAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var options = FeatureOptions.FromDesired(spec.Desired);
        var (exitCode, output) = await RunDismAsync(context, ["/Get-Features", "/Format:List"], ct);
        if (DismErrors.Classify(exitCode, output) == DismOutcome.ProviderUnavailable) {
            return new ResourceDiff(true, []);
        }

        var states = ParseList(output).ToDictionary(
            r => DismListParser.Get(r, "Feature Name") ?? "",
            r => DismListParser.Get(r, "State") ?? "",
            StringComparer.OrdinalIgnoreCase);

        var differences = new List<ChangeItem>();
        foreach (var feature in options.Features) {
            if (!states.TryGetValue(feature, out var state)) {
                context.Log.Info($"feature '{feature}' is not present in this image; skipping.");
                differences.Add(new ChangeItem(ChangeKind.Skipped, feature, "feature not present in image"));
                continue;
            }
            var alreadyAbsent = state.Contains("Removed", StringComparison.OrdinalIgnoreCase)
                || (state.Contains("Disabled", StringComparison.OrdinalIgnoreCase) && !options.RemovePayload);
            if (!alreadyAbsent) {
                differences.Add(new ChangeItem(ChangeKind.Removed, feature, Before: state));
            }
        }
        return new ResourceDiff(differences.All(d => d.Kind == ChangeKind.Skipped), differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException("dism.feature present (enable) is not implemented yet.");
        }

        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped("features already absent or unavailable",
                diff.Differences.Where(d => d.Kind == ChangeKind.Skipped).ToArray());
        }

        var options = FeatureOptions.FromDesired(spec.Desired);
        var applied = new List<ChangeItem>();
        foreach (var change in diff.Differences.Where(d => d.Kind != ChangeKind.Skipped)) {
            var args = new List<string> { "/Disable-Feature", $"/FeatureName:{change.Target}", "/NoRestart" };
            if (options.RemovePayload) {
                args.Add("/Remove");
            }
            var (exitCode, output) = await RunDismAsync(context, args, ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (outcome is DismOutcome.InvalidInstallState) {
                context.Log.Warn($"skipping unselectable optional feature: {change.Target} (CBS_E_INVALID_INSTALL_STATE)");
                applied.Add(new ChangeItem(ChangeKind.Skipped, change.Target, "feature not present in image"));
            }
            else if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"disabled optional feature: {change.Target}");
                applied.Add(change);
            }
            else {
                throw new ExecException($"dism.exe failed to disable feature '{change.Target}' (exit {exitCode}).", exitCode);
            }
        }
        return ExecResult.Applied(applied);
    }
}
