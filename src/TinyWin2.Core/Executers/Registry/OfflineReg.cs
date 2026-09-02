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
        DeleteAsync(runner, aclRescueKey,
            async () => (await QueryAsync(runner, key, ct)).Success,
            ct, "delete", key, "/f");

    public static Task DeleteValueAsync(
        IProcessRunner runner, string key, string valueName, string aclRescueKey, CancellationToken ct) {
        var arguments = string.IsNullOrEmpty(valueName)
            ? new[] { "delete", key, "/ve", "/f" }
            : new[] { "delete", key, "/v", valueName, "/f" };
        return DeleteAsync(runner, aclRescueKey,
            () => ValueExistsAsync(runner, key, valueName, ct), ct, arguments);
    }

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

    private static async Task<bool> ValueExistsAsync(
        IProcessRunner runner, string key, string valueName, CancellationToken ct) {
        var result = await QueryAsync(runner, key, ct);
        if (!result.Success) {
            return false;
        }

        var displayName = string.IsNullOrEmpty(valueName) ? "(Default)" : valueName;
        return RegValues.ParseQueryValue(result.Output, displayName) is not null;
    }

    /// <summary>
    ///     Deletes; reg.exe exits 1 for "not found" and "access denied" alike, so a failed delete
    ///     is re-queried: a genuinely missing target is success, an existing one gets one
    ///     rescue-then-retry pass (RegistryAcl ownership grant) before surfacing as
    ///     <see cref="ProcessRunnerException" />.
    /// </summary>
    private static async Task DeleteAsync(
        IProcessRunner runner, string aclRescueKey, Func<Task<bool>> stillExists,
        CancellationToken ct, params string[] arguments) {
        var result = await runner.RunAsync("reg.exe", arguments, new() { IgnoreExitCode = true }, ct);
        if (result.Success || !await stillExists()) {
            return;
        }

        await RegistryAcl.RescueAsync(runner, aclRescueKey, ct);
        result = await runner.RunAsync("reg.exe", arguments, new() { IgnoreExitCode = true }, ct);
        if (result.Success || !await stillExists()) {
            return;
        }

        throw new ProcessRunnerException("reg.exe", result);
    }
}
