using System.Text;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
/// Registry-key ACL rescue: some offline service keys (e.g. DPS) are owned by TrustedInstaller
/// with Administrators denied write, so <c>reg.exe add</c> fails. Take ownership as the
/// Administrators group and grant FullControl — the offline-hive equivalent of the fs.path
/// takeown/icacls fallback.
/// </summary>
internal static class RegistryAcl {
    /// <summary>The Administrators group, by SID (localized-name-proof).</summary>
    private const string AdministratorsSid = "S-1-5-32-544";

    public static async Task RescueAsync(IProcessRunner runner, string hklmSubKeyPath, CancellationToken ct) {
        // pwsh does the privilege dance (TakeOwnership then ChangePermissions need separate
        // opens); -EncodedCommand avoids every layer of quoting hell.
        var script = $$"""
            $path = '{{hklmSubKeyPath.Replace("'", "''")}}'
            $sid = [System.Security.Principal.SecurityIdentifier]::new('{{AdministratorsSid}}')
            $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]::TakeOwnership)
            $a = $k.GetAccessControl()
            $a.SetOwner($sid)
            $k.SetAccessControl($a)
            $k.Close()
            $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]::ChangePermissions)
            $a = $k.GetAccessControl()
            $a.SetAccessRule([System.Security.AccessControl.RegistryAccessRule]::new($sid, [System.Security.AccessControl.RegistryRights]::FullControl, 'ContainerInherit', 'None', 'Allow'))
            $k.SetAccessControl($a)
            $k.Close()
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        await runner.RunAsync("pwsh.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(60) }, ct);
    }
}
