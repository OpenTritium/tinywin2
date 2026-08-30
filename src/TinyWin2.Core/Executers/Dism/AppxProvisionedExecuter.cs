using System.Collections.Frozen;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

public sealed class AppxProvisionedExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    private const string ResourceId = "appx.provisioned";
    public override string Resource => ResourceId;

    protected override IReadOnlyList<string> ListArguments => ["/Get-ProvisionedAppxPackages", "/Format:List"];

    protected override string RecordStartKey => "DisplayName";

    /// <summary>
    ///     Server editions without appx provisioning answer ERROR_INVALID_PARAMETER —
    ///     there is simply nothing provisioned, so the resource is satisfied.
    /// </summary>
    protected override FrozenSet<int> InapplicableExitCodes => [87];

    protected override bool EmptyListingSatisfied => true;

    protected override string SatisfiedSkipReason => "no provisioned appx packages matched";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<IReadOnlyDictionary<string, string>> records,
        ExecContext context,
        OperationSpec spec) {
        var options = AppxOptions.FromDesired(spec.Spec);
        foreach (var record in records) {
            var displayName = DismListParser.Get(record, "DisplayName");
            // RemoveKey is the dism Package Name; DisplayName is what patterns match and logs show.
            if (displayName is null
                || DismListParser.Get(record, "PackageName") is not { } packageName
                || !options.Patterns.Any(p => p.IsMatch(displayName))) {
                continue;
            }

            yield return new(packageName, displayName);
        }
    }

    protected override IReadOnlyList<string> RemoveArguments(DismRemovalTarget target, OperationSpec spec) =>
        ["/Remove-ProvisionedAppxPackage", $"/PackageName:{target.RemoveKey}"];
}
