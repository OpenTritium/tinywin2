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
            Add-Type -Namespace Win32 -Name Priv -MemberDefinition @'
            [DllImport("advapi32.dll", SetLastError=true)]
            public static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
            [DllImport("advapi32.dll", SetLastError=true)]
            public static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
            [DllImport("advapi32.dll", SetLastError=true)]
            public static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES newst, int len, IntPtr prev, IntPtr ret);
            [StructLayout(LayoutKind.Sequential, Pack=1)]
            public struct TOKEN_PRIVILEGES { public int PrivilegeCount; public long Luid; public int Attributes; }
            '@
            # SeTakeOwnershipPrivilege is held but DISABLED in the token: enable it first,
            # otherwise OpenSubKey(..., TakeOwnership) returns null (seen live on Server 2025).
            $proc = [System.Diagnostics.Process]::GetCurrentProcess()
            $tok = [IntPtr]::Zero
            [Win32.Priv]::OpenProcessToken($proc.Handle, 0x28, [ref]$tok) | Out-Null
            foreach ($name in 'SeTakeOwnershipPrivilege', 'SeRestorePrivilege') {
                $tp = New-Object Win32.Priv+TOKEN_PRIVILEGES
                $tp.PrivilegeCount = 1
                $tp.Attributes = 2
                [Win32.Priv]::LookupPrivilegeValue($null, $name, [ref]$tp.Luid) | Out-Null
                [Win32.Priv]::AdjustTokenPrivileges($tok, $false, [ref]$tp, 0, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            }
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
