namespace TinyWin2.Core.Unattend;

/// <summary>
///     Builds the <c>SetupComplete.cmd</c> that removes the setup-created recovery partition for
///     the minimal layout. FirstLogonCommands cannot do this: they run with the user's filtered
///     token, so reagentc and diskpart fail with access denied (verified 2026-10-21). Setup runs
///     this script as SYSTEM after the specialize pass and before OOBE.
/// </summary>
public static class WinReCleanupScript {
    /// <summary>Returns the script text with CRLF line endings, ready for a batch file.</summary>
    public static string Generate(UnattendFirmware firmware) {
        var windows = firmware == UnattendFirmware.Uefi ? 3 : 2;
        var recovery = windows + 1;
        // /disable leaves winre.wim in C:\Windows\System32\Recovery; /enable re-stages it on C:.
        return "@echo off\r\n"
               + "reagentc /disable\r\n"
               + $"(echo select disk 0& echo select partition {recovery}& echo delete partition override&"
               + $" echo select partition {windows}& echo extend)| diskpart\r\n"
               + "reagentc /enable\r\n";
    }
}
