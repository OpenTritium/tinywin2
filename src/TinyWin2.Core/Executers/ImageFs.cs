using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers;

/// <summary>Filesystem mutations against the mounted image, with a privilege-rescue fallback.</summary>
internal static class ImageFs {
    /// <summary>Deletes a file or directory (recursively) when it exists; a no-op otherwise.</summary>
    internal static void DeleteIfExists(string target) {
        if (Directory.Exists(target)) {
            Directory.Delete(target, true);
        }
        else if (File.Exists(target)) {
            File.Delete(target);
        }
    }

    /// <summary>Protected paths need ownership + Administrators full control before they can be deleted.</summary>
    internal static async Task GrantDeleteAccessAsync(IProcessRunner runner, string target, CancellationToken ct) {
        var recursive = Directory.Exists(target);
        // .NET returns extended-length paths for mounted WIM volumes. The legacy
        // command-line ACL tools do not consistently accept the \\?\ prefix.
        var nativeTarget = ToNativeToolPath(target);
        var attribArguments = new List<string> { "-R", "-S", "-H", nativeTarget };
        if (recursive) {
            attribArguments.AddRange(["/S", "/D"]);
        }

        // Mounted WIM trees can contain mixed attributes and ACLs. These tools are
        // deliberately best-effort: one protected child must not prevent the rest
        // of the tree from being rescued.
        await runner.RunAsync("attrib.exe", attribArguments,
            new() { IgnoreExitCode = true }, ct);

        var takeOwnArguments = new List<string> { "/F", nativeTarget, "/A" };
        if (recursive) {
            takeOwnArguments.AddRange(["/R", "/D", "Y"]);
        }

        await runner.RunAsync("takeown.exe", takeOwnArguments,
            new() { IgnoreExitCode = true }, ct);
        var icaclsArguments = new List<string> {
            nativeTarget, "/grant", recursive ? "*S-1-5-32-544:(OI)(CI)F" : "*S-1-5-32-544:F"
        };
        if (recursive) {
            icaclsArguments.AddRange(["/T", "/C"]);
        }

        await runner.RunAsync("icacls.exe", icaclsArguments,
            new() { IgnoreExitCode = true }, ct);
    }

    /// <summary>
    ///     Deletes a path with ACL rescue and a native fallback. A successful native
    ///     command is not sufficient by itself: the path is checked after every attempt.
    /// </summary>
    internal static async Task DeleteWithRescueAsync(
        IProcessRunner runner, string target, CancellationToken ct) {
        try {
            DeleteIfExists(target);
            return;
        }
        catch (Exception ex) when (IsDeleteRescueCandidate(ex)) {
            await GrantDeleteAccessAsync(runner, target, ct);
        }

        await TryNativeDeleteAsync(runner, target, ct);
        if (!File.Exists(target) && !Directory.Exists(target)) {
            return;
        }

        try {
            DeleteIfExists(target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException) {
            await TryNativeDeleteAsync(runner, target, ct);
        }

        if (File.Exists(target) || Directory.Exists(target)) {
            throw new IOException($"Unable to delete protected image path '{target}'.");
        }
    }

    private static async Task TryNativeDeleteAsync(
        IProcessRunner runner, string target, CancellationToken ct) {
        var nativeTarget = ToNativeToolPath(target);
        var recursive = Directory.Exists(target);
        var command = recursive
            ? $"rmdir /s /q {ProcessRunner.QuoteIfNeeded(nativeTarget)}"
            : $"del /f /q {ProcessRunner.QuoteIfNeeded(nativeTarget)}";
        await runner.RunAsync("cmd.exe", ["/d", "/c", command],
            new() { IgnoreExitCode = true }, ct);
        if (!File.Exists(target) && !Directory.Exists(target)) {
            return;
        }

        // A protected directory may have DELETE but its parent may deny
        // DELETE_CHILD. Grant only that parent right before one final retry.
        var parent = Directory.GetParent(target)?.FullName;
        if (parent is null) {
            return;
        }

        await runner.RunAsync("icacls.exe",
            [ToNativeToolPath(parent), "/grant", "*S-1-5-32-544:(DC)", "/C"],
            new() { IgnoreExitCode = true }, ct);
        await runner.RunAsync("cmd.exe", ["/d", "/c", command],
            new() { IgnoreExitCode = true }, ct);
    }

    private static bool IsDeleteRescueCandidate(Exception exception) =>
        exception is UnauthorizedAccessException || exception is IOException;

    private static string ToNativeToolPath(string target) =>
        target.StartsWith(@"\\?\", StringComparison.Ordinal) ? target[4..] : target;
}
