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
        await runner.RunAsync("takeown.exe", ["/F", target, "/A", "/R", "/D", "Y"], cancellationToken: ct);
        await runner.RunAsync("icacls.exe", [target, "/grant", "*S-1-5-32-544:F", "/T"], cancellationToken: ct);
    }
}
