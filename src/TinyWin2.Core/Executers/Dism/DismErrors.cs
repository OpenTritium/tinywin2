using System.Text.RegularExpressions;

namespace TinyWin2.Core.Executers;

/// <summary>
/// Classifies dism.exe outcomes by exit code (and, where needed, English/Chinese text hints),
/// replacing v1's localized-regex error handling with stable numeric contracts.
/// </summary>
public enum DismOutcome {
    Success,
    SuccessRebootRequired,
    ProviderUnavailable,
    InvalidInstallState,
    CannotUninstall,
    ComponentCleanupUnsupported,
    Fatal,
}

public static partial class DismErrors {
    /// <summary>0x800F0813 — feature is permanent for this edition (v1: CBS_E_INVALID_INSTALL_STATE).</summary>
    public const int CbsEInvalidInstallState = unchecked((int)0x800F0813);

    /// <summary>0x800F0825 — capability-on-demand is permanent for this edition (v1: CBS_E_CANNOT_UNINSTALL).</summary>
    public const int CbsECannotUninstall = unchecked((int)0x800F0825);

    /// <summary>Win32 ERROR_NOT_SUPPORTED — the edition exposes no servicing provider for this operation.</summary>
    public const int ErrorNotSupported = 50;

    /// <summary>DISM error 4350 — some Server 2025 images reject offline StartComponentCleanup.</summary>
    public const int ComponentCleanup4350 = 4350;

    /// <summary>DISM success-with-reboot-required.</summary>
    public const int SuccessRebootRequired = 3010;

    public static DismOutcome Classify(int exitCode, string output) {
        if (exitCode == 0) {
            return DismOutcome.Success;
        }
        if (exitCode == SuccessRebootRequired) {
            return DismOutcome.SuccessRebootRequired;
        }
        if (exitCode == ComponentCleanup4350) {
            return DismOutcome.ComponentCleanupUnsupported;
        }
        if (exitCode == ErrorNotSupported) {
            return DismOutcome.ProviderUnavailable;
        }
        if (exitCode == CbsEInvalidInstallState) {
            return DismOutcome.InvalidInstallState;
        }
        if (exitCode == CbsECannotUninstall) {
            return DismOutcome.CannotUninstall;
        }
        if (ProviderUnavailableText().IsMatch(output)) {
            return DismOutcome.ProviderUnavailable;
        }
        return DismOutcome.Fatal;
    }

    /// <summary>DISM provider-gone message fragments (kept as a last-resort fallback for zh-CN media).</summary>
    [GeneratedRegex("cannot be used on this computer|不能用于此计算机", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderUnavailableText();
}

/// <summary>Parses dism.exe <c>/Format:List</c> output into records of key/value pairs.</summary>
public static class DismListParser {
    public static IReadOnlyList<Dictionary<string, string>> Parse(string output) {
        var records = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var rawLine in output.Split('\n')) {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) {
                continue;
            }
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) {
                continue;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("Feature Name", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Capability Identity", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Package Identity", StringComparison.OrdinalIgnoreCase)
                || key.Equals("DisplayName", StringComparison.OrdinalIgnoreCase)) {
                // These keys start a new record block.
                current = [];
                records.Add(current);
            }
            if (current is null) {
                continue;
            }
            current[key] = value;
        }
        return records;
    }

    public static string? Get(Dictionary<string, string> record, string key) =>
        record.TryGetValue(key, out var value) ? value : null;
}
