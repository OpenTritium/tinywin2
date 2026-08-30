using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
///     Shared reg.exe plumbing for the offline-registry executers: tolerant queries
///     (exit 1 = key or value not found) and mutations that retry once behind an
///     <see cref="RegistryAcl.RescueAsync" /> ownership grant.
/// </summary>
internal static class OfflineReg {
    /// <summary>
    ///     Runs reg.exe query, tolerating exit 1 ("not found"); any other non-zero exit
    ///     throws. Callers branch on <see cref="ProcessRunResult.Success" />.
    /// </summary>
    public static async Task<ProcessRunResult> QueryAsync(
        IProcessRunner runner, string key, CancellationToken ct, params string[] suffix) {
        var result = await runner.RunAsync("reg.exe", ["query", key, .. suffix],
            new() { IgnoreExitCode = true }, ct);
        if (!result.Success && result.ExitCode != 1) {
            throw new ProcessRunnerException("reg.exe", result);
        }

        return result;
    }

    public static Task DeleteKeyAsync(
        IProcessRunner runner, string key, string aclRescueKey, CancellationToken ct) =>
        DeleteAsync(runner, aclRescueKey, ct, ["delete", key, "/f"]);

    public static Task DeleteValueAsync(
        IProcessRunner runner, string key, string valueName, string aclRescueKey, CancellationToken ct) =>
        DeleteAsync(runner, aclRescueKey, ct, ["delete", key, "/v", valueName, "/f"]);

    /// <summary>
    ///     Writes one value; on access failure rescues the ACL and retries once.
    ///     TrustedInstaller-owned service keys (e.g. DPS) deny Administrators write:
    ///     the rescue claims ownership + FullControl for the group before the retry.
    /// </summary>
    public static async Task AddValueAsync(
        IProcessRunner runner, string key, string valueName, string regType, string data,
        string aclRescueKey, CancellationToken ct) {
        var arguments = new[] { "add", key, "/v", valueName, "/t", regType, "/d", data, "/f" };
        try {
            await runner.RunAsync("reg.exe", arguments, cancellationToken: ct);
        }
        catch (ProcessRunnerException) {
            await RegistryAcl.RescueAsync(runner, aclRescueKey, ct);
            await runner.RunAsync("reg.exe", arguments, cancellationToken: ct);
        }
    }

    /// <summary>
    ///     Deletes; a missing target (exit 1) is success, any other failure gets one
    ///     rescue-then-retry pass before surfacing as <see cref="ProcessRunnerException" />.
    /// </summary>
    private static async Task DeleteAsync(
        IProcessRunner runner, string aclRescueKey, CancellationToken ct, string[] arguments) {
        var result = await runner.RunAsync("reg.exe", arguments, new() { IgnoreExitCode = true }, ct);
        if (result.Success || result.ExitCode == 1) {
            return;
        }

        await RegistryAcl.RescueAsync(runner, aclRescueKey, ct);
        result = await runner.RunAsync("reg.exe", arguments, new() { IgnoreExitCode = true }, ct);
        if (!result.Success && result.ExitCode != 1) {
            throw new ProcessRunnerException("reg.exe", result);
        }
    }
}
