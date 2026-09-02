using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Tests;

/// <summary>
///     Exercises the ACL rescue against a genuinely locked key inside a genuinely loaded
///     offline hive: CBS re-creates component registrations with descriptors that leave
///     Administrators without any access, and the rescue must restore delete rights.
/// </summary>
[SuppressMessage("Interoperability", "CA1416",
    Justification = "TinyWin executes Windows registry operations only on Windows build hosts.")]
[ItGate]
public sealed class RegistryAclRescueTests : IDisposable {
    private readonly string _mountPath = TestPlans.CreateTempDirectory();
    private readonly string _hiveName = "TinyWin2_Rescue_" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose() {
        TryReg($"unload HKLM\\{_hiveName}");
        try {
            Directory.Delete(_mountPath, true);
        }
        catch {
            /* best effort */
        }
    }

    [Test]
    public async Task RescueGrantsAdministratorsAndEnablesDeleteOnLockedKey() {
        // build a real loadable hive: save an existing tiny live key → load under a new name →
        // add the victim key through the loaded-hive path (live-HKLM writes can be filtered on
        // managed hosts, but writes into a reg.exe-loaded hive are what the build itself uses)
        var hiveFile = Path.Combine(_mountPath, "rescue.hiv");
        RunOrThrow($"save \"HKLM\\SOFTWARE\\Classes\\.txt\" \"{hiveFile}\" /y");
        RunOrThrow($"load HKLM\\{_hiveName} \"{hiveFile}\"");
        var lockedLoaded = $"HKLM\\{_hiveName}\\.txt\\Custom\\Locked";
        RunOrThrow($"add {lockedLoaded} /ve /d x /f");
        await LockLikeComponentRegistrationAsync(Registry.LocalMachine,
            $"{_hiveName}\\.txt\\Custom\\Locked");

        // sanity: without the rescue, Administrators cannot delete the key
        var denied = Run($"delete {lockedLoaded} /f");
        await Assert.That(denied).IsEqualTo(1);

        // the rescue grants Administrators/SYSTEM FullControl via the backup/restore path
        RegistryAcl.Rescue(lockedLoaded);

        var deleteExit = Run($"delete {lockedLoaded} /f");
        await Assert.That(deleteExit).IsEqualTo(0);
    }

    /// <summary>Applies a CBS-style protected DACL: SYSTEM Full, Everyone read, no Administrators ACE.</summary>
    private static async Task LockLikeComponentRegistrationAsync(RegistryKey root, string relativePath) {
        using var key = root.OpenSubKey(relativePath, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ChangePermissions);
        await Assert.That(key).IsNotNull();
        var acl = key!.GetAccessControl(AccessControlSections.Access);
        acl.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        acl.ResetAccessRule(new RegistryAccessRule(system, RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.ResetAccessRule(new RegistryAccessRule(everyone, RegistryRights.ReadKey,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        key.SetAccessControl(acl);
    }

    private static int Run(string arguments) {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName = "reg.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void RunOrThrow(string arguments) {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName = "reg.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) {
            throw new InvalidOperationException(
                $"reg.exe {arguments} failed ({process.ExitCode}): {error} {output}");
        }
    }

    private static void TryReg(string arguments) {
        try {
            Run(arguments);
        }
        catch {
            /* best-effort cleanup */
        }
    }
}
