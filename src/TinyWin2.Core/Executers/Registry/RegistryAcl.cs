using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
///     Registry-key ACL rescue for TrustedInstaller-owned service keys in a loaded offline hive.
///     regini.exe works for the live registry, but returns error 87 for a reg.exe-loaded offline
///     hive. The native backup/restore registry path is required to obtain WRITE_DAC on that hive.
/// </summary>
[SuppressMessage("Interoperability", "CA1416",
    Justification = "TinyWin executes Windows registry operations only on Windows build hosts.")]
internal static partial class RegistryAcl {
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;
    private const uint KeyRead = 0x20019;
    private const uint WriteDac = 0x40000;
    private const uint WriteOwner = 0x80000;
    private const uint RegOptionBackupRestore = 0x4;
    private const uint DaclSecurityInformation = 0x4;
    private const int ErrorInsufficientBuffer = 122;
    private const int RegistryFullControl = 0xF003F;
    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));

    /// <summary>regini script codes: Administrators Full and SYSTEM Full.</summary>
    private const string GrantCodes = "[1 17]";

    public static async Task RescueAsync(IProcessRunner runner, string hklmSubKeyPath, CancellationToken ct) {
        var subKey = hklmSubKeyPath.Replace("\"", "");
        if (subKey.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)) {
            subKey = subKey["HKLM\\".Length..];
        }

        // regini remains the supported fast path for a live registry. It returns error 87 for a
        // reg.exe-loaded offline hive; only then use the backup/restore handle path below.
        try {
            await RunReginiFallbackAsync(runner, subKey, ct);
            return;
        }
        catch (ProcessRunnerException) {
            // Expected for a locked key in a loaded offline hive. The native path below retries
            // with SeBackup/SeRestore and REG_OPTION_BACKUP_RESTORE.
        }

        if (TryGrantAdministratorsFullControl(subKey) == 0) {
            return;
        }

        throw new ExecException($"could not grant write access to registry key '{hklmSubKeyPath}'.");
    }

    private static async Task RunReginiFallbackAsync(IProcessRunner runner, string subKey, CancellationToken ct) {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"tinywin2-regini-{Guid.NewGuid():N}.txt");
        try {
            await File.WriteAllTextAsync(scriptPath,
                @"\Registry\Machine\" + subKey + " " + GrantCodes, ct);
            await runner.RunAsync("regini.exe", [scriptPath],
                new() { Timeout = TimeSpan.FromSeconds(30) }, ct);
        }
        finally {
            try {
                File.Delete(scriptPath);
            }
            catch {
                /* best effort */
            }
        }
    }

    private static int TryGrantAdministratorsFullControl(string subKey) {
        try {
            if (!EnablePrivilege("SeTakeOwnershipPrivilege")
                || !EnablePrivilege("SeBackupPrivilege")
                || !EnablePrivilege("SeRestorePrivilege")) {
                return 1314; // ERROR_PRIVILEGE_NOT_HELD
            }

            var status = RegCreateKeyEx(HkeyLocalMachine, subKey, 0, "", RegOptionBackupRestore,
                KeyRead | WriteOwner | WriteDac, IntPtr.Zero, out var key, out _);
            if (status != 0) {
                return status;
            }

            try {
                var descriptor = ReadSecurityDescriptor(key);
                var updated = AddAdministrativeAccess(descriptor);
                var buffer = Marshal.AllocHGlobal(updated.Length);
                try {
                    Marshal.Copy(updated, 0, buffer, updated.Length);
                    return RegSetKeySecurity(key, DaclSecurityInformation, buffer);
                }
                finally {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally {
                RegCloseKey(key);
            }
        }
        catch (DllNotFoundException) {
            return 126; // ERROR_MOD_NOT_FOUND
        }
        catch (EntryPointNotFoundException) {
            return 127; // ERROR_PROC_NOT_FOUND
        }
    }

    private static byte[] ReadSecurityDescriptor(IntPtr key) {
        uint size = 0;
        var status = RegGetKeySecurity(key, DaclSecurityInformation, IntPtr.Zero, ref size);
        if (status != ErrorInsufficientBuffer || size == 0) {
            throw new InvalidOperationException($"RegGetKeySecurity size query failed ({status}).");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try {
            status = RegGetKeySecurity(key, DaclSecurityInformation, buffer, ref size);
            if (status != 0) {
                throw new InvalidOperationException($"RegGetKeySecurity failed ({status}).");
            }

            var descriptor = new byte[size];
            Marshal.Copy(buffer, descriptor, 0, checked((int)size));
            return descriptor;
        }
        finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] AddAdministrativeAccess(byte[] descriptorBytes) {
        var current = new RawSecurityDescriptor(descriptorBytes, 0);
        var existing = current.DiscretionaryAcl;
        var entries = new List<GenericAce>(existing?.Count ?? 0);
        if (existing is not null) {
            for (var index = 0; index < existing.Count; index++) {
                entries.Add(existing[index]);
            }
        }

        AddAllowAce(entries, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddAllowAce(entries, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        var dacl = new RawAcl(existing?.Revision ?? 2, entries.Count);
        for (var index = 0; index < entries.Count; index++) {
            dacl.InsertAce(index, entries[index]);
        }

        var flags = current.ControlFlags | ControlFlags.DiscretionaryAclPresent;
        var updated = new RawSecurityDescriptor(flags, current.Owner, current.Group, current.SystemAcl, dacl);
        var result = new byte[updated.BinaryLength];
        updated.GetBinaryForm(result, 0);
        return result;
    }

    private static void AddAllowAce(List<GenericAce> entries, SecurityIdentifier sid) {
        if (entries.OfType<CommonAce>().Any(ace => ace.AceQualifier == AceQualifier.AccessAllowed
                                                   && ace.SecurityIdentifier == sid
                                                   && (ace.AccessMask & RegistryFullControl) == RegistryFullControl)) {
            return;
        }

        // Put the rescue ACE first so a later inherited allow/deny ordering cannot hide it.
        entries.Insert(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
            RegistryFullControl, sid, false, null));
    }

    private static bool EnablePrivilege(string name) {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) {
            return false;
        }

        try {
            if (!LookupPrivilegeValue(null, name, out var luid)) {
                return false;
            }

            var privileges = new TokenPrivileges {
                PrivilegeCount = 1,
                Privileges = new() { Luid = luid, Attributes = SePrivilegeEnabled }
            };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0;
        }
        finally {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(
        IntPtr key, string subKey, uint reserved, string className, uint options, uint samDesired,
        IntPtr securityAttributes, out IntPtr result, out uint disposition);

    [DllImport("advapi32.dll", EntryPoint = "RegSetKeySecurity")]
    private static extern int RegSetKeySecurity(IntPtr key, uint securityInformation, IntPtr descriptor);

    [DllImport("advapi32.dll", EntryPoint = "RegGetKeySecurity")]
    private static extern int RegGetKeySecurity(IntPtr key, uint securityInformation, IntPtr descriptor,
        ref uint descriptorSize);

    [DllImport("advapi32.dll", EntryPoint = "RegCloseKey")]
    private static extern int RegCloseKey(IntPtr key);

    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint access, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
