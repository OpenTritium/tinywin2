using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
///     Shared reg.exe plumbing for the offline-registry executers: tolerant queries
///     (exit 1 = key or value not found) and mutations that retry once behind an
///     <see cref="RegistryAcl.Rescue" /> ownership grant.
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

    /// <summary>
    ///     Deletes a whole key. Returns true when the key is gone; false when it survives the
    ///     rescue (CBS locks component registrations with descriptors that deny Administrators).
    ///     Callers decide whether a surviving key is acceptable. The ACL rescue targets
    ///     <paramref name="key" /> itself unless another (parent) key is given.
    /// </summary>
    public static Task<bool> DeleteKeyAsync(
        IProcessRunner runner, string key, CancellationToken ct, string? aclRescueKey = null) =>
        DeleteAsync(runner, aclRescueKey ?? key,
            async () => (await QueryAsync(runner, key, ct)).Success,
            ct, "delete", key, "/f");

    /// <summary>
    ///     Deletes one value. Returns true when the value is gone; false when it survives the
    ///     rescue (see <see cref="DeleteKeyAsync" />).
    /// </summary>
    public static Task<bool> DeleteValueAsync(
        IProcessRunner runner, string key, string valueName, CancellationToken ct,
        string? aclRescueKey = null) {
        var arguments = string.IsNullOrEmpty(valueName)
            ? new[] { "delete", key, "/ve", "/f" }
            : new[] { "delete", key, "/v", valueName, "/f" };
        return DeleteAsync(runner, aclRescueKey ?? key,
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
            RegistryAcl.Rescue(aclRescueKey);
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
    ///     is re-queried: a genuinely missing target counts as deleted, an existing one gets one
    ///     rescue-then-retry pass (RegistryAcl ownership grant). Returns false only when the
    ///     target survives the rescue.
    /// </summary>
    private static async Task<bool> DeleteAsync(
        IProcessRunner runner, string aclRescueKey, Func<Task<bool>> stillExists,
        CancellationToken ct, params string[] arguments) {
        var result = await runner.RunAsync("reg.exe", arguments, new() { IgnoreExitCode = true }, ct);
        if (result.Success || !await stillExists()) {
            return true;
        }

        try {
            RegistryAcl.Rescue(aclRescueKey);
        }
        catch (ExecException) {
            // the ownership grant itself could not reach the key: fall through to the
            // ownership-takeover deletion below
        }

        result = await runner.RunAsync("reg.exe", arguments, new() { IgnoreExitCode = true }, ct);
        if (result.Success || !await stillExists()) {
            return true;
        }

        // final fallback: claim ownership of the key with SeTakeOwnershipPrivilege and delete
        // the subtree through the .NET registry API — some CBS descriptors cannot be DACL-edited
        // in place at all
        return RegistryAcl.TryForceDeleteSubKeyTree(aclRescueKey) || !await stillExists();
    }
}
