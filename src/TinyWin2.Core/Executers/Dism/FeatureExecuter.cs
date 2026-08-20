using System.Text.Json.Nodes;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>
/// Converges optional features. absent = disable (optionally removing payload); the
/// present direction is reserved for future enable support.
/// Desired: <c>{ features:[...], removePayload:bool }</c>.
/// </summary>
public sealed class FeatureExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter
{
    public const string ResourceId = "dism.feature";
    public string Resource => ResourceId;

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct)
    {
        var (features, removePayload) = Parse(spec);
        var (exitCode, output) = await RunDismAsync(context, ["/Get-Features", "/Format:List"], ct);
        if (DismErrors.Classify(exitCode, output) == DismOutcome.ProviderUnavailable)
        {
            return new ResourceDiff(true, []);
        }

        var states = ParseList(output).ToDictionary(
            r => DismListParser.Get(r, "Feature Name") ?? "",
            r => DismListParser.Get(r, "State") ?? "",
            StringComparer.OrdinalIgnoreCase);

        var differences = new List<ChangeItem>();
        foreach (var feature in features)
        {
            if (!states.TryGetValue(feature, out var state))
            {
                context.Log.Info($"feature '{feature}' is not present in this image; skipping.");
                differences.Add(new ChangeItem(ChangeKind.Skipped, feature, SkipReasonValue()));
                continue;
            }
            var alreadyAbsent = state.Contains("Removed", StringComparison.OrdinalIgnoreCase)
                || (state.Contains("Disabled", StringComparison.OrdinalIgnoreCase) && !removePayload);
            if (!alreadyAbsent && spec.Ensure == Ensure.Absent)
            {
                differences.Add(new ChangeItem(ChangeKind.Removed, feature, Before: state));
            }
        }
        return new ResourceDiff(differences.All(d => d.Kind == ChangeKind.Skipped), differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct)
    {
        if (spec.Ensure == Ensure.Present)
        {
            throw new ExecException("dism.feature present (enable) is not implemented yet.");
        }

        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied)
        {
            return ExecResult.Skipped("features already absent or unavailable",
                diff.Differences.Where(d => d.Kind == ChangeKind.Skipped).ToArray());
        }

        var (features, removePayload) = Parse(spec);
        var applied = new List<ChangeItem>();
        foreach (var change in diff.Differences.Where(d => d.Kind != ChangeKind.Skipped))
        {
            var args = new List<string> { "/Disable-Feature", $"/FeatureName:{change.Target}", "/NoRestart" };
            if (removePayload)
            {
                args.Add("/Remove");
            }
            var (exitCode, output) = await RunDismAsync(context, args, ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (outcome is DismOutcome.InvalidInstallState)
            {
                context.Log.Warn($"skipping unselectable optional feature: {change.Target} (CBS_E_INVALID_INSTALL_STATE)");
                applied.Add(new ChangeItem(ChangeKind.Skipped, change.Target, SkipReasonValue()));
            }
            else if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired)
            {
                context.Log.Info($"disabled optional feature: {change.Target}");
                applied.Add(change);
            }
            else
            {
                throw new ExecException($"dism.exe failed to disable feature '{change.Target}' (exit {exitCode}).", exitCode);
            }
        }
        return ExecResult.Applied(applied);
    }

    private static (List<string> Features, bool RemovePayload) Parse(ExecSpec spec)
    {
        var features = spec.Desired["features"]?.AsArray().OfType<JsonValue>().Select(v => v.GetValue<string>()).ToList()
                       ?? throw new ExecException("dism.feature requires 'features'.");
        if (features.Count == 0)
        {
            throw new ExecException("dism.feature requires at least one feature name.");
        }
        var removePayload = spec.Desired["removePayload"]?.GetValue<bool>() ?? true;
        return (features, removePayload);
    }

    private static string SkipReasonValue() => "feature not present in image";
}
