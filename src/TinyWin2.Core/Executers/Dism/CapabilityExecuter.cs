using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>Converges capabilities (Features on Demand). absent = remove capability.</summary>
public sealed class CapabilityExecuter(IProcessRunner runner) : DismRemoveExecuterBase(runner) {
    public const string ResourceId = "dism.capability";
    public override string Resource => ResourceId;

    protected override IReadOnlyList<string> ListArguments => ["/Get-Capabilities", "/Format:List"];

    protected override DismOutcome? DowngradeOutcome => DismOutcome.CannotUninstall;

    protected override string SatisfiedSkipReason => "capabilities already absent or unavailable";

    protected override IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<Dictionary<string, string>> records,
        ExecContext context,
        ExecSpec spec) {
        var options = CapabilityOptions.FromDesired(spec.Desired);
        var states = records.ToDictionary(
            r => DismListParser.Get(r, "Capability Identity") ?? "",
            r => DismListParser.Get(r, "State") ?? "",
            StringComparer.OrdinalIgnoreCase);
        foreach (var capability in options.Capabilities) {
            if (!states.TryGetValue(capability, out var state)) {
                context.Log.Info($"capability '{capability}' is not present in this image; skipping.");
                yield return new DismRemovalTarget(capability, SkipReason: "capability not present in image");
            }
            else if (!state.Contains("Installed", StringComparison.OrdinalIgnoreCase)) {
                // Already absent: no difference entry at all.
            }
            else {
                yield return new DismRemovalTarget(capability, Before: state);
            }
        }
    }

    protected override IReadOnlyList<string> RemoveArguments(DismRemovalTarget target, ExecSpec spec) =>
        ["/Remove-Capability", $"/CapabilityName:{target.RemoveKey}"];
}
