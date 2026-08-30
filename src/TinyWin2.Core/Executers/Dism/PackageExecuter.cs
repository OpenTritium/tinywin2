using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Converges CBS packages by regex pattern. absent = remove matching installed/staged packages.</summary>
public sealed class PackageExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    private const string ResourceId = "dism.package";

    private static readonly string[] RemovableStates = ["Installed", "Staged", "Install Pending"];
    public override string Resource => ResourceId;

    protected override IReadOnlyList<string> ListArguments => ["/Get-Packages", "/Format:List"];

    protected override string RecordStartKey => "Package Identity";

    protected override DismOutcome? DowngradeOutcome => DismOutcome.CannotUninstall;

    protected override string SatisfiedSkipReason => "no removable CBS packages matched";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<IReadOnlyDictionary<string, string>> records,
        ExecContext context,
        OperationSpec spec) {
        var options = PackageOptions.FromDesired(spec.Spec);
        var matched = records
            .Select(record => new {
                Record = record,
                Identity = DismListParser.Get(record, "Package Identity"),
                State = DismListParser.Get(record, "State") ?? ""
            })
            .Where(item => item.Identity is not null && options.Patterns.Any(p => p.IsMatch(item.Identity)))
            .ToList();

        // DISM commonly lists both a superseded staged package and its newer
        // installed replacement. Removing the staged predecessor directly can
        // return CBS_E_INVALID_PACKAGE (0x800F0805); the component cleanup pass
        // removes it after the active package family has been serviced.
        var activeFamilies = matched
            .Where(item => item.State.Equals("Installed", StringComparison.OrdinalIgnoreCase)
                           || item.State.Equals("Install Pending", StringComparison.OrdinalIgnoreCase))
            .Select(item => PackageFamily(item.Identity!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in matched) {
            var identity = item.Identity!;
            var state = item.State;

            if (RemovableStates.Any(s => state.Equals(s, StringComparison.OrdinalIgnoreCase))) {
                if (state.Equals("Staged", StringComparison.OrdinalIgnoreCase)
                    && activeFamilies.Contains(PackageFamily(identity))) {
                    context.Log.Info($"skipping superseded staged CBS package: {identity}");
                    yield return new(identity, state, SkipReason: "superseded staged package");
                    continue;
                }

                yield return new(identity, state);
            }
            else {
                context.Log.Info($"skipping non-removable CBS package: {identity} [{state}]");
                yield return new(identity, SkipReason: $"state={state}");
            }
        }
    }

    private static string PackageFamily(string identity) {
        var separator = identity.LastIndexOf('~');
        return separator > 0 ? identity[..separator] : identity;
    }

    protected override IReadOnlyList<string> RemoveArguments(DismRemovalTarget target, OperationSpec spec) =>
        ["/Remove-Package", $"/PackageName:{target.RemoveKey}", "/NoRestart"];
}
