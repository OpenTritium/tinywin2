using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>
/// Converges CBS packages by regex pattern. absent = remove matching installed/staged packages.
/// Desired: <c>{ patterns:[regex...] }</c>. High-risk by nature; keep patterns explicit.
/// </summary>
public sealed class PackageExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    public const string ResourceId = "dism.package";
    public string Resource => ResourceId;

    private static readonly string[] RemovableStates = ["Installed", "Staged", "InstallPending"];

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var options = PackageOptions.FromDesired(spec.Desired);
        var (exitCode, output) = await RunDismAsync(context, ["/Get-Packages", "/Format:List"], ct);
        if (DismErrors.Classify(exitCode, output) == DismOutcome.ProviderUnavailable) {
            return new ResourceDiff(true, []);
        }
        var records = ParseList(output);
        var regexes = options.Patterns;
        var differences = new List<ChangeItem>();
        foreach (var record in records) {
            var identity = DismListParser.Get(record, "Package Identity");
            var state = DismListParser.Get(record, "State") ?? "";
            if (identity is null || !regexes.Any(r => r.IsMatch(identity))) {
                continue;
            }
            if (RemovableStates.Any(s => state.Equals(s, StringComparison.OrdinalIgnoreCase))) {
                differences.Add(new ChangeItem(ChangeKind.Removed, identity, Before: state));
            }
            else {
                context.Log.Info($"skipping non-removable CBS package: {identity} [{state}]");
                differences.Add(new ChangeItem(ChangeKind.Skipped, identity, $"state={state}"));
            }
        }
        return new ResourceDiff(differences.All(d => d.Kind == ChangeKind.Skipped), differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException("dism.package present is not implemented.");
        }
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped("no removable CBS packages matched",
                diff.Differences.Where(d => d.Kind == ChangeKind.Skipped).ToArray());
        }
        var applied = new List<ChangeItem>();
        foreach (var change in diff.Differences.Where(d => d.Kind != ChangeKind.Skipped)) {
            var (exitCode, output) = await RunDismAsync(context, ["/Remove-Package", $"/PackageName:{change.Target}", "/NoRestart"], ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"removed CBS package: {change.Target}");
                applied.Add(change);
            }
            else {
                throw new ExecException($"dism.exe failed to remove package '{change.Target}' (exit {exitCode}).", exitCode);
            }
        }
        return ExecResult.Applied(applied);
    }

}
