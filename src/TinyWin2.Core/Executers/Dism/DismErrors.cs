using System.Text.RegularExpressions;

namespace TinyWin2.Core.Executers.Dism;

public enum DismOutcome {
    Success,
    SuccessRebootRequired,
    ProviderUnavailable,
    InvalidInstallState,
    CannotUninstall,
    UnknownTarget,
    ComponentCleanupUnsupported,
    Fatal
}

public static partial class DismErrors {
    /// <summary>0x800F0813 — feature is permanent for this edition (CBS_E_INVALID_INSTALL_STATE).</summary>
    public const int CbsEInvalidInstallState = unchecked((int)0x800F0813);

    /// <summary>0x800F0825 — capability-on-demand is permanent for this edition (CBS_E_CANNOT_UNINSTALL).</summary>
    public const int CbsECannotUninstall = unchecked((int)0x800F0825);

    /// <summary>0x800F0805 — an older/inbox package cannot be removed independently.</summary>
    public const int CbsEInvalidPackage = unchecked((int)0x800F0805);

    /// <summary>0x800F080C — CBS does not know the named feature/capability (CBS_E_UNKNOWN_UPDATE).</summary>
    public const int CbsEUnknownUpdate = unchecked((int)0x800F080C);

    /// <summary>0x800F081F — the files needed to enable the feature are missing (CBS_E_SOURCE_MISSING).</summary>
    public const int CbsESourceMissing = unchecked((int)0x800F081F);

    /// <summary>0x800F0954 — the source could not be downloaded (WSU/Windows Update unreachable offline).</summary>
    public const int CbsESourceNotDownloadable = unchecked((int)0x800F0954);

    /// <summary>Win32 ERROR_NOT_SUPPORTED — the edition exposes no servicing provider for this operation.</summary>
    private const int ErrorNotSupported = 50;

    /// <summary>DISM error 4350 — some Server 2025 images reject offline StartComponentCleanup.</summary>
    private const int ComponentCleanup4350 = 4350;

    /// <summary>DISM success-with-reboot-required.</summary>
    private const int SuccessRebootRequired = 3010;

    public static DismOutcome Classify(int exitCode, string output) {
        return exitCode switch {
            0 => DismOutcome.Success,
            SuccessRebootRequired => DismOutcome.SuccessRebootRequired,
            ComponentCleanup4350 => DismOutcome.ComponentCleanupUnsupported,
            ErrorNotSupported => DismOutcome.ProviderUnavailable,
            CbsEInvalidInstallState => DismOutcome.InvalidInstallState,
            CbsECannotUninstall => DismOutcome.CannotUninstall,
            CbsEInvalidPackage => DismOutcome.CannotUninstall,
            CbsEUnknownUpdate => DismOutcome.UnknownTarget,
            _ => ProviderUnavailableText().IsMatch(output) ? DismOutcome.ProviderUnavailable : DismOutcome.Fatal
        };
    }

    /// <summary>DISM provider-gone message fragments (kept as a last-resort fallback for zh-CN media).</summary>
    [GeneratedRegex("cannot be used on this computer|不能用于此计算机",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderUnavailableText();
}
