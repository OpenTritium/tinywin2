using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Converges CBS packages by regex pattern. absent = remove matching installed/staged packages.</summary>
public sealed class PackageExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    internal const string ResourceId = "dism.package";
    public override string Resource => ResourceId;

    private static readonly string[] RemovableStates = ["Installed", "Staged", "InstallPending"];

    protected override IReadOnlyList<string> ListArguments => ["/Get-Packages", "/Format:List"];

    protected override DismOutcome? DowngradeOutcome => null;

    protected override string SatisfiedSkipReason => "no removable CBS packages matched";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<Dictionary<string, string>> records,
        ExecContext context,
        ExecSpec spec) {
        var options = PackageOptions.FromDesired(spec.Desired);
        foreach (var record in records) {
            var identity = DismListParser.Get(record, "Package Identity");
            var state = DismListParser.Get(record, "State") ?? "";
            if (identity is null || !options.Patterns.Any(p => p.IsMatch(identity))) {
                continue;
            }

            if (RemovableStates.Any(s => state.Equals(s, StringComparison.OrdinalIgnoreCase))) {
                yield return new DismRemovalTarget(identity, Before: state);
            }
            else {
                context.Log.Info($"skipping non-removable CBS package: {identity} [{state}]");
                yield return new DismRemovalTarget(identity, SkipReason: $"state={state}");
            }
        }
    }

    protected override IReadOnlyList<string> RemoveArguments(DismRemovalTarget target, ExecSpec spec) =>
        ["/Remove-Package", $"/PackageName:{target.RemoveKey}", "/NoRestart"];
}
