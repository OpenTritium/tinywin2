using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

/// <summary>
/// Maintenance resource: StartComponentCleanup (optionally /ResetBase).
/// Idempotent by nature; a known Server-2025 DISM error 4350 downgrades to Skipped.
/// Desired: <c>{ resetBase:bool }</c>.
/// </summary>
public sealed class ComponentStoreExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    internal const string ResourceId = "dism.component-store";
    public string Resource => ResourceId;

    public Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        // Component-store size reduction is a run-once optimization: the diff is the action itself.
        _ = ComponentStoreOptions.FromDesired(spec.Desired);
        return Task.FromResult(new ResourceDiff(false, [new ChangeItem(ChangeKind.Modified, "component-store")]));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var options = ComponentStoreOptions.FromDesired(spec.Desired);
        var resetBase = options.ResetBase;
        if (resetBase) {
            context.Log.Warn("ResetBase is enabled; installed updates cannot be uninstalled from the resulting image.");
        }

        var args = new List<string> { "/Cleanup-Image", "/StartComponentCleanup" };
        if (resetBase) {
            args.Add("/ResetBase");
        }

        var (exitCode, output) = await RunDismAsync(context, args, ct);
        var outcome = DismErrors.Classify(exitCode, output);
        return outcome switch {
            DismOutcome.Success or DismOutcome.SuccessRebootRequired => ExecResult.Applied(
            [
                new(ChangeKind.Modified, "component-store", Before: "uncleaned",
                    After: resetBase ? "cleaned+resetbase" : "cleaned")
            ]),
            DismOutcome.ComponentCleanupUnsupported => ExecResult.Skipped(
                "this image rejects offline StartComponentCleanup (DISM error 4350)",
                [new ChangeItem(ChangeKind.Skipped, "component-store", "DISM error 4350")]),
            _ => throw new ExecException($"dism.exe StartComponentCleanup failed (exit {exitCode})."),
        };
    }

}
