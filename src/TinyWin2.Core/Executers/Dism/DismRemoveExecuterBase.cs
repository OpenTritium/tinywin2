using System.Collections.Frozen;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>One candidate for removal, as selected from a dism /Format:List listing.</summary>
/// <param name="RemoveKey">Identity passed to the remove command (also the ChangeItem target).</param>
/// <param name="Before">Human-readable current state (or backing identity) recorded in the change log.</param>
/// <param name="SkipReason">When set, the target is skipped instead of removed (absent/non-removable).</param>
public sealed record DismRemovalTarget(string RemoveKey, string? Before = null, string? SkipReason = null);

/// <summary>
///     Template-method base for the four dism "remove-by-listing" executers
///     (feature / capability / package / provisioned appx): list targets, diff against
///     desired-absent, then run the per-target remove command and classify the outcome.
///     Subclasses only describe WHAT to list/remove; the convergence loop lives here once.
/// </summary>
public abstract class DismRemoveExecuterBase(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    protected abstract IReadOnlyList<string> ListArguments { get; }

    protected abstract string RecordStartKey { get; }

    /// <summary>Outcome that downgrades a failed removal to Skipped (e.g. CBS_E_CANNOT_UNINSTALL); null for none.</summary>
    protected virtual DismOutcome? DowngradeOutcome => null;

    /// <summary>Whether this executer also converges features to enabled (action apply).</summary>
    protected virtual bool SupportsApply => false;

    /// <summary>
    ///     Exit codes meaning "the feature's payload is not in this image" during an apply —
    ///     enabling cannot proceed, but the desired state is left unchanged rather than failing.
    ///     CBS_E_SOURCE_MISSING (0x800F081F), CBS_E_SOURCE_NOT_FOUND (0x800F0954).
    /// </summary>
    protected virtual FrozenSet<int> ApplyPayloadMissingExitCodes => FrozenSet<int>.Empty;

    /// <summary>
    ///     Target switch the remove command accepts repeatedly (e.g. "/FeatureName"), letting
    ///     several targets share one DISM/CBS session; null when targets must be removed one by one.
    ///     A failed batch falls back to per-target removal so a single bad target cannot mask others.
    /// </summary>
    protected virtual string? BatchableTargetSwitch => null;

    /// <summary>
    ///     Exit codes meaning "this listing does not apply to the image" — treated as
    ///     provider-unavailable (satisfied no-op) instead of a hard failure. Server without
    ///     provisioning (appx) answers 87, ERROR_INVALID_PARAMETER.
    /// </summary>
    protected virtual FrozenSet<int> InapplicableExitCodes => FrozenSet<int>.Empty;

    /// <summary>Whether a successful empty listing means this resource is already absent.</summary>
    protected virtual bool EmptyListingSatisfied => false;

    protected abstract string SatisfiedSkipReason { get; }
    public abstract string Resource { get; }

    public object Bind(OperationSpec spec) {
        var valid = spec.Action == OperationAction.Remove || (SupportsApply && spec.Action == OperationAction.Apply);
        if (!valid) {
            throw new ExecException($"{Resource} supports actions 'remove'{(SupportsApply ? " and 'apply'" : "")}.");
        }

        return BindOptions(spec);
    }

    /// <summary>Parses the validated remove spec into this resource's typed options.</summary>
    protected abstract object BindOptions(OperationSpec spec);

    public async Task<ResourceDiff> InspectAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var (exitCode, output) = await RunDismAsync(context, [.. ListArguments], ct);
        var outcome = DismErrors.Classify(exitCode, output);
        if (outcome == DismOutcome.ProviderUnavailable || InapplicableExitCodes.Contains(exitCode)) {
            return new(true, []);
        }

        if (outcome is not (DismOutcome.Success or DismOutcome.SuccessRebootRequired)) {
            throw new ExecException($"dism.exe failed to list {Resource} targets (exit {exitCode}).");
        }

        var records = DismListParser.Parse(output, RecordStartKey);
        if (records.Count == 0) {
            if (EmptyListingSatisfied) {
                return new(true, []);
            }

            throw new ExecException(
                $"dism.exe returned no {Resource} records despite a successful listing (exit {exitCode}).");
        }

        var differences = SelectTargets(records, context, operation)
            .Select(t => t.SkipReason is not null
                ? new(ChangeKind.Skipped, t.RemoveKey, t.SkipReason)
                : operation.Action == OperationAction.Apply
                    ? new ChangeItem(ChangeKind.Modified, t.RemoveKey, t.Before, After: "Enabled")
                    : new ChangeItem(ChangeKind.Removed, t.RemoveKey, t.Before))
            .ToList();
        return new(differences.All(d => d.Kind == ChangeKind.Skipped), differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var diff = await InspectAsync(context, operation, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped(SatisfiedSkipReason,
                [.. diff.Differences.Where(d => d.Kind == ChangeKind.Skipped)]);
        }

        var applied = diff.Differences
            .Where(d => d.Kind == ChangeKind.Skipped)
            .ToList();
        var pending = diff.Differences
            .Where(d => d.Kind != ChangeKind.Skipped)
            .ToList();
        var apply = operation.Action == OperationAction.Apply;
        var removedCount = 0;

        var batchSwitch = BatchableTargetSwitch;
        if (batchSwitch is not null && pending.Count > 1) {
            var (batchExit, batchOutput) = await RunDismAsync(context,
                BatchArguments(operation, batchSwitch, pending), ct);
            if (DismErrors.Classify(batchExit, batchOutput) is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"{Resource}: {pending.Count} targets converged in one batch");
                applied.AddRange(pending);
                removedCount = pending.Count;
                pending = [];
            }
            else {
                context.Log.Warn(
                    $"batched {Resource} {(apply ? "enable" : "removal")} failed (exit {batchExit}); retrying targets one by one.");
            }
        }

        foreach (var change in pending) {
            var (exitCode, output) = await RunDismAsync(context,
                TargetArguments(operation, new(change.Target, change.Before)), ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (apply && ApplyPayloadMissingExitCodes.Contains(exitCode)) {
                context.Log.Warn(
                    $"skipping {Resource} target without payload: {change.Target} (exit 0x{exitCode:X8}); feature left disabled");
                applied.Add(new(ChangeKind.Skipped, change.Target, "payload not present in this edition"));
            }
            else if (outcome is DismOutcome.UnknownTarget) {
                // CBS rejects the name outright: the target does not exist in this edition,
                // so the desired state (absent) already holds.
                context.Log.Info($"{Resource}: {change.Target} is not known to CBS; treating as absent.");
                applied.Add(new(ChangeKind.Skipped, change.Target, "not present in this edition"));
            }
            else if (outcome == DowngradeOutcome) {
                context.Log.Warn($"skipping unremovable {Resource} target: {change.Target} ({outcome})");
                applied.Add(new(ChangeKind.Skipped, change.Target, "not removable in this edition"));
            }
            else if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"{Resource}: {change.Target} {(apply ? "enabled" : "removed")}");
                applied.Add(change);
                removedCount++;
            }
            else {
                throw new ExecException(
                    $"dism.exe failed to {(apply ? "enable" : "remove")} {Resource} target '{change.Target}' (exit {exitCode}).");
            }
        }

        return removedCount == 0
            ? ExecResult.Skipped("every target was absent or not removable in this edition", applied)
            : ExecResult.Applied(applied);
    }

    /// <summary>One combined remove command carrying every pending target switch.</summary>
    private IReadOnlyList<string> BatchArguments(
        BoundOperation operation, string targetSwitch, IReadOnlyList<ChangeItem> targets) {
        var args = TargetArguments(operation, new(targets[0].Target, targets[0].Before)).ToList();
        var insertAt = args.FindIndex(a => a.StartsWith(targetSwitch, StringComparison.Ordinal)) + 1;
        for (var i = 1; i < targets.Count; i++) {
            args.Insert(insertAt + i - 1, targetSwitch + ":" + targets[i].Target);
        }

        return args;
    }

    /// <summary>Arguments for one target, chosen by the operation's action (enable vs remove).</summary>
    protected IReadOnlyList<string> TargetArguments(BoundOperation operation, DismRemovalTarget target) {
        return operation.Action == OperationAction.Apply
            ? EnableArguments(operation, target)
            : RemoveArguments(operation, target);
    }

    /// <summary>Default enable command; subclasses with apply support may adjust switches.</summary>
    protected virtual IReadOnlyList<string> EnableArguments(BoundOperation operation, DismRemovalTarget target) {
        return ["/Enable-Feature", $"/FeatureName:{target.RemoveKey}", "/All", "/NoRestart"];
    }

    /// <summary>Maps one /Format:List record to a removal target; may log skips.</summary>
    protected abstract IEnumerable<DismRemovalTarget> SelectTargets(
        IReadOnlyList<IReadOnlyDictionary<string, string>> records,
        ExecContext context,
        BoundOperation operation);

    protected abstract IReadOnlyList<string> RemoveArguments(BoundOperation operation, DismRemovalTarget target);
}
