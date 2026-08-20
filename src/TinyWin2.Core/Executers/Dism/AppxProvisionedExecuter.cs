using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Converges provisioned Appx packages. absent = remove apps matching wildcards against DisplayName.</summary>
public sealed class AppxProvisionedExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    internal const string ResourceId = "appx.provisioned";
    public override string Resource => ResourceId;

    protected override IReadOnlyList<string> ListArguments => ["/Get-ProvisionedAppxPackages", "/Format:List"];

    protected override DismOutcome? DowngradeOutcome => null;

    protected override string SatisfiedSkipReason => "no provisioned appx packages matched";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<Dictionary<string, string>> records,
        ExecContext context,
        ExecSpec spec) {
        var options = AppxOptions.FromDesired(spec.Desired);
        foreach (var record in records) {
            var displayName = DismListParser.Get(record, "DisplayName");
            // RemoveKey is the dism Package Name; DisplayName is what patterns match and logs show.
            if (displayName is null
                || DismListParser.Get(record, "Package Name") is not { } packageName
                || !options.Patterns.Any(p => LikePattern.ToRegex(p).IsMatch(displayName))) {
                continue;
            }

            yield return new(packageName, Before: displayName);
        }
    }

    protected override IReadOnlyList<string> RemoveArguments(DismRemovalTarget target, ExecSpec spec) =>
        ["/Remove-ProvisionedAppxPackage", $"/PackageName:{target.RemoveKey}"];
}
