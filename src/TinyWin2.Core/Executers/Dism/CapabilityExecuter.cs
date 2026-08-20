using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>
/// Converges capabilities (Features on Demand). absent = remove capability.
/// Desired: <c>{ capabilities:[...] }</c>.
/// </summary>
public sealed class CapabilityExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    public const string ResourceId = "dism.capability";
    public string Resource => ResourceId;

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var options = CapabilityOptions.FromDesired(spec.Desired);
        var (exitCode, output) = await RunDismAsync(context, ["/Get-Capabilities", "/Format:List"], ct);
        if (DismErrors.Classify(exitCode, output) == DismOutcome.ProviderUnavailable) {
            return new ResourceDiff(true, []);
        }

        var states = ParseList(output).ToDictionary(
            r => DismListParser.Get(r, "Capability Identity") ?? "",
            r => DismListParser.Get(r, "State") ?? "",
            StringComparer.OrdinalIgnoreCase);

        var differences = new List<ChangeItem>();
        foreach (var capability in options.Capabilities) {
            if (!states.TryGetValue(capability, out var state)) {
                context.Log.Info($"capability '{capability}' is not present in this image; skipping.");
                differences.Add(new ChangeItem(ChangeKind.Skipped, capability, SkipReasonValue()));
                continue;
            }
            if (state.Contains("Installed", StringComparison.OrdinalIgnoreCase) && spec.Ensure == Ensure.Absent) {
                differences.Add(new ChangeItem(ChangeKind.Removed, capability, Before: state));
            }
        }
        return new ResourceDiff(differences.All(d => d.Kind == ChangeKind.Skipped), differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException("dism.capability present is not implemented yet.");
        }

        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped("capabilities already absent or unavailable",
                diff.Differences.Where(d => d.Kind == ChangeKind.Skipped).ToArray());
        }

        var applied = new List<ChangeItem>();
        foreach (var change in diff.Differences.Where(d => d.Kind != ChangeKind.Skipped)) {
            var (exitCode, output) = await RunDismAsync(context, ["/Remove-Capability", $"/CapabilityName:{change.Target}"], ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (outcome is DismOutcome.CannotUninstall) {
                context.Log.Warn($"skipping permanent capability: {change.Target} (CBS_E_CANNOT_UNINSTALL)");
                applied.Add(new ChangeItem(ChangeKind.Skipped, change.Target, SkipReasonValue()));
            }
            else if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"removed capability: {change.Target}");
                applied.Add(change);
            }
            else {
                throw new ExecException($"dism.exe failed to remove capability '{change.Target}' (exit {exitCode}).", exitCode);
            }
        }
        return ExecResult.Applied(applied);
    }

    private static string SkipReasonValue() => "capability not present in image";
}
