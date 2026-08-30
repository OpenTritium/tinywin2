using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Dism;

public sealed class ComponentStoreExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    private const string ResourceId = "dism.component-store";
    public string Resource => ResourceId;

    public object Bind(OperationSpec spec) {
        if (spec.Action != OperationAction.Cleanup) {
            throw new ExecException($"{ResourceId} supports only action 'cleanup'.");
        }

        return ComponentStoreOptions.FromDesired(spec.Spec);
    }

    public Task<ResourceDiff> InspectAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        // Component-store size reduction is a run-once optimization: the diff is the action itself.
        return Task.FromResult(new ResourceDiff(false, [new(ChangeKind.Modified, "component-store")]));
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var resetBase = ((ComponentStoreOptions)operation.Options).ResetBase;
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
                new(ChangeKind.Modified, "component-store", "uncleaned",
                    resetBase ? "cleaned+resetbase" : "cleaned")
            ]),
            DismOutcome.ComponentCleanupUnsupported => ExecResult.Skipped(
                "this image rejects offline StartComponentCleanup (DISM error 4350)",
                [new(ChangeKind.Skipped, "component-store", "DISM error 4350")]),
            _ => throw new ExecException($"dism.exe StartComponentCleanup failed (exit {exitCode}).")
        };
    }
}
