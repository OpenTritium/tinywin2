using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Converges CBS packages by regex pattern. absent = remove matching installed/staged packages.</summary>
public sealed class PackageExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    private const string ResourceId = "dism.package";
    public override string Resource => ResourceId;

    private static readonly string[] RemovableStates = ["Installed", "Staged", "Install Pending"];

    protected override IReadOnlyList<string> ListArguments => ["/Get-Packages", "/Format:List"];

    protected override string RecordStartKey => "Package Identity";

    protected override string SatisfiedSkipReason => "no removable CBS packages matched";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<IReadOnlyDictionary<string, string>> records,
        ExecContext context,
        OperationSpec spec) {
        var options = PackageOptions.FromDesired(spec.Spec);
        foreach (var record in records) {
            var identity = DismListParser.Get(record, "Package Identity");
            var state = DismListParser.Get(record, "State") ?? "";
            if (identity is null || !options.Patterns.Any(p => p.IsMatch(identity))) {
                continue;
            }

            if (RemovableStates.Any(s => state.Equals(s, StringComparison.OrdinalIgnoreCase))) {
                yield return new(identity, Before: state);
            }
            else {
                context.Log.Info($"skipping non-removable CBS package: {identity} [{state}]");
                yield return new(identity, SkipReason: $"state={state}");
            }
        }
    }

    protected override IReadOnlyList<string> RemoveArguments(DismRemovalTarget target, OperationSpec spec) =>
        ["/Remove-Package", $"/PackageName:{target.RemoveKey}", "/NoRestart"];
}
