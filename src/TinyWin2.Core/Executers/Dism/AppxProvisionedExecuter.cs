using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>
/// Converges provisioned Appx packages. absent = remove provisioned apps matching wildcards.
/// Desired: <c>{ patterns:["Microsoft.Xbox*"] }</c> matched against DisplayName.
/// </summary>
public sealed class AppxProvisionedExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    public const string ResourceId = "appx.provisioned";
    public string Resource => ResourceId;

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var options = AppxOptions.FromDesired(spec.Desired);
        var (exitCode, output) = await RunDismAsync(context, ["/Get-ProvisionedAppxPackages", "/Format:List"], ct);
        if (DismErrors.Classify(exitCode, output) == DismOutcome.ProviderUnavailable) {
            context.Log.Warn("this edition exposes no AppX servicing provider; skipping appx targets.");
            return new ResourceDiff(true, []);
        }
        if (exitCode != 0 && exitCode != DismErrors.SuccessRebootRequired) {
            throw new ExecException($"dism.exe failed to list provisioned appx packages (exit {exitCode}).", exitCode);
        }
        var differences = new List<ChangeItem>();
        foreach (var record in ParseList(output)) {
            var displayName = DismListParser.Get(record, "DisplayName");
            var packageName = DismListParser.Get(record, "Package Name");
            if (displayName is null || packageName is null) {
                continue;
            }
            if (options.Patterns.Any(p => Registry.RegistryServiceExecuter.LikeToRegex(p).IsMatch(displayName))) {
                differences.Add(new ChangeItem(ChangeKind.Removed, displayName, Before: packageName));
            }
        }
        return new ResourceDiff(differences.Count == 0, differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException("appx.provisioned present is not implemented.");
        }
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped("no provisioned appx packages matched");
        }
        var applied = new List<ChangeItem>();
        foreach (var change in diff.Differences) {
            var packageName = change.Before!;
            var (exitCode, output) = await RunDismAsync(context, ["/Remove-ProvisionedAppxPackage", $"/PackageName:{packageName}"], ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"removed provisioned appx: {change.Target}");
                applied.Add(change);
            }
            else if (outcome == DismOutcome.ProviderUnavailable) {
                context.Log.Warn("AppX servicing provider disappeared mid-run; skipping remaining packages.");
                break;
            }
            else {
                throw new ExecException($"dism.exe failed to remove provisioned appx '{change.Target}' (exit {exitCode}).", exitCode);
            }
        }
        return ExecResult.Applied(applied);
    }

}
