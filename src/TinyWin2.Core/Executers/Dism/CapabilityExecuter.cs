using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

public sealed class CapabilityExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    private const string ResourceId = "dism.capability";
    public override string Resource => ResourceId;

    protected override IReadOnlyList<string> ListArguments => ["/Get-Capabilities", "/Format:List"];

    protected override string RecordStartKey => "Capability Identity";

    protected override DismOutcome? DowngradeOutcome => DismOutcome.CannotUninstall;

    protected override string? BatchableTargetSwitch => "/CapabilityName";

    protected override object BindOptions(OperationSpec spec) => CapabilityOptions.FromDesired(spec.Spec);

    protected override string SatisfiedSkipReason => "capabilities already absent or unavailable";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<IReadOnlyDictionary<string, string>> records,
        ExecContext context,
        BoundOperation operation) {
        var options = (CapabilityOptions)operation.Options;
        var states = records.ToDictionary(
            r => DismListParser.Get(r, "Capability Identity") ?? "",
            r => DismListParser.Get(r, "State") ?? "",
            StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in options.Capabilities) {
            var matches = states.Keys.Where(identity => LikePattern.IsMatch(pattern, identity)).ToList();
            if (matches.Count == 0) {
                context.Log.Info($"capability '{pattern}' is not present in this image; skipping.");
                yield return new(pattern, SkipReason: "capability not present in image");
                continue;
            }

            foreach (var identity in matches.Where(identity => selected.Add(identity)
                                                               && states[identity].Contains("Installed",
                                                                   StringComparison.OrdinalIgnoreCase))) {
                // DISM receives the concrete identity, never the wildcard pattern.
                yield return new(identity, states[identity]);
            }
        }
    }

    protected override IReadOnlyList<string> RemoveArguments(BoundOperation operation, DismRemovalTarget target) =>
        ["/Remove-Capability", $"/CapabilityName:{target.RemoveKey}"];
}
