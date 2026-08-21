using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
/// Registry-key ACL rescue: fresh-image service keys (e.g. DPS) are TrustedInstaller-locked
/// against reg.exe writes. regini.exe - the native offline-ACL tool - grants Administrators +
/// SYSTEM full control in one shot; no privilege dance, no .NET RegistryKey open (which the
/// loaded-hive path denies even for owners).
/// </summary>
internal static class RegistryAcl {
    /// <summary>regini script codes [1 7 17]: Administrators Full, SYSTEM Full (SYSTEM twice = Full + owner-style).</summary>
    private const string GrantCodes = "[1 7 17]";

    public static async Task RescueAsync(IProcessRunner runner, string hklmSubKeyPath, CancellationToken ct) {
        // regini speaks the NT object namespace: \Registry\Machine\<subkey> [aces].
        // Callers pass full reg.exe-style keys ("HKLM\..."); the HKLM\ prefix must go,
        // or regini resolves \Registry\Machine\HKLM\... and dies with exit 1 / error 87.
        var subKey = hklmSubKeyPath.Replace("\"", "");
        if (subKey.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)) {
            subKey = subKey["HKLM\\".Length..];
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"tinywin2-regini-{Guid.NewGuid():N}.txt");
        try {
            await File.WriteAllTextAsync(scriptPath,
                "\\Registry\\Machine\\" + subKey + " " + GrantCodes, ct);
            await runner.RunAsync("regini.exe", [scriptPath],
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(30) }, ct);
        }
        finally {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }
}
