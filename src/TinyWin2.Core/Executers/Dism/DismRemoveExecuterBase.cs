using System.Collections.Frozen;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>One candidate for removal, as selected from a dism /Format:List listing.</summary>
/// <param name="RemoveKey">Identity passed to the remove command (also the ChangeItem target).</param>
/// <param name="Before">Human-readable current state (or backing identity) recorded in the change log.</param>
/// <param name="SkipReason">When set, the target is skipped instead of removed (absent/non-removable).</param>
public sealed record DismRemovalTarget(string RemoveKey, string? Before = null, string? SkipReason = null);

/// <summary>
/// Template-method base for the four dism "remove-by-listing" executers
/// (feature / capability / package / provisioned appx): list targets, diff against
/// desired-absent, then run the per-target remove command and classify the outcome.
/// Subclasses only describe WHAT to list/remove; the convergence loop lives here once.
/// </summary>
public abstract class DismRemoveExecuterBase(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    public abstract string Resource { get; }

    protected abstract IReadOnlyList<string> ListArguments { get; }

    /// <summary>Maps one /Format:List record to a removal target; may log skips.</summary>
    protected abstract IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<Dictionary<string, string>> records,
        ExecContext context,
        ExecSpec spec);

    protected abstract IReadOnlyList<string> RemoveArguments(DismRemovalTarget target, ExecSpec spec);

    /// <summary>Outcome that downgrades a failed removal to Skipped (e.g. CBS_E_CANNOT_UNINSTALL); null for none.</summary>
    protected abstract DismOutcome? DowngradeOutcome { get; }

    /// <summary>Exit codes meaning "this listing does not apply to the image" — treated as
    /// provider-unavailable (satisfied no-op) instead of a hard failure. Server without
    /// provisioning (appx) answers 87, ERROR_INVALID_PARAMETER.</summary>
    protected virtual FrozenSet<int> InapplicableExitCodes => FrozenSet<int>.Empty;

    protected abstract string SatisfiedSkipReason { get; }

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        if (spec.Ensure == Ensure.Present) {
            throw new ExecException($"{Resource} present is not implemented yet.");
        }

        var (exitCode, output) = await RunDismAsync(context, [.. ListArguments], ct);
        var outcome = DismErrors.Classify(exitCode, output);
        if (outcome == DismOutcome.ProviderUnavailable || InapplicableExitCodes.Contains(exitCode)) {
            return new(true, []);
        }

        if (outcome is not (DismOutcome.Success or DismOutcome.SuccessRebootRequired)) {
            throw new ExecException($"dism.exe failed to list {Resource} targets (exit {exitCode}).");
        }

        var differences = SelectTargets(ParseList(output), context, spec)
            .Select(t => t.SkipReason is not null
                ? new ChangeItem(ChangeKind.Skipped, t.RemoveKey, t.SkipReason)
                : new ChangeItem(ChangeKind.Removed, t.RemoveKey, Before: t.Before))
            .ToList();
        return new ResourceDiff(differences.All(d => d.Kind == ChangeKind.Skipped), differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped(SatisfiedSkipReason,
                [.. diff.Differences.Where(d => d.Kind == ChangeKind.Skipped)]);
        }

        var applied = new List<ChangeItem>();
        foreach (var change in diff.Differences.Where(d => d.Kind != ChangeKind.Skipped)) {
            var (exitCode, output) = await RunDismAsync(context,
                RemoveArguments(new DismRemovalTarget(change.Target, change.Before), spec), ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (outcome == DowngradeOutcome) {
                context.Log.Warn($"skipping unremovable {Resource} target: {change.Target} ({outcome})");
                applied.Add(new ChangeItem(ChangeKind.Skipped, change.Target, "not removable in this edition"));
            }
            else if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"{Resource}: {change.Target} removed");
                applied.Add(change);
            }
            else {
                throw new ExecException(
                    $"dism.exe failed to remove {Resource} target '{change.Target}' (exit {exitCode}).");
            }
        }

        return ExecResult.Applied(applied);
    }
}
