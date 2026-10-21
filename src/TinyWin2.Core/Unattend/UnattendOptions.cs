namespace TinyWin2.Core.Unattend;

/// <summary>Firmware layout the generated answer file prepares the target disk for.</summary>
public enum UnattendFirmware {
    /// <summary>Legacy BIOS/CSM: MBR disk with a 100 MB System Reserved partition and the Windows partition.</summary>
    Bios,

    /// <summary>UEFI: GPT disk with EFI System, MSR and Windows partitions.</summary>
    Uefi
}

/// <summary>Partition layout the answer file creates.</summary>
public enum UnattendLayout {
    /// <summary>Microsoft's recommended layout, including the setup-created WinRE partition.</summary>
    Standard,

    /// <summary>
    ///     Smallest layout Windows Setup accepts: EFI + MSR + Windows on UEFI (a GPT disk without MSR
    ///     fails at the disk page), System Reserved + Windows on BIOS. Setup still carves a WinRE
    ///     partition out of the OS volume, so first-logon commands move WinRE onto C: and delete it.
    /// </summary>
    Minimal
}

/// <summary>
///     Inputs for <see cref="UnattendGenerator" />. Every value is written verbatim into Autounattend.xml,
///     including the password, because Windows Setup accepts only plain-text local account credentials here.
/// </summary>
public sealed record UnattendOptions {
    public string UserName { get; init; } = "TinyWin";
    public string Password { get; init; } = "TinyWin2026Temp";
    public string DisplayName { get; init; } = "TinyWin";
    public string ComputerName { get; init; } = "TINYWIN";
    public string Locale { get; init; } = "zh-CN";
    public string TimeZone { get; init; } = "China Standard Time";
    public int ImageIndex { get; init; } = 1;
    public UnattendFirmware Firmware { get; init; } = UnattendFirmware.Bios;
    public UnattendLayout Layout { get; init; } = UnattendLayout.Standard;
    public string? ProductKey { get; init; }

    /// <summary>Returns the display name to emit, falling back to <see cref="UserName" /> when blank.</summary>
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;

    /// <summary>Throws <see cref="ArgumentException" /> when a value cannot be represented in a valid answer file.</summary>
    public UnattendOptions Validate() {
        if (string.IsNullOrWhiteSpace(UserName)) {
            throw new ArgumentException("account name must not be empty.", nameof(UserName));
        }

        if (UserName.Length > 20) {
            throw new ArgumentException("account name must be at most 20 characters.", nameof(UserName));
        }

        if (UserName.IndexOfAny(['"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>']) >= 0) {
            throw new ArgumentException("account name contains a character Windows rejects.", nameof(UserName));
        }

        if (string.IsNullOrEmpty(Password)) {
            throw new ArgumentException("password must not be empty.", nameof(Password));
        }

        if (string.IsNullOrWhiteSpace(ComputerName)) {
            throw new ArgumentException("computer name must not be empty.", nameof(ComputerName));
        }

        if (ComputerName.Length > 15) {
            throw new ArgumentException("computer name must be at most 15 characters.", nameof(ComputerName));
        }

        if (ComputerName.IndexOfAny([' ', '\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0) {
            throw new ArgumentException("computer name contains a character Windows rejects.", nameof(ComputerName));
        }

        if (string.IsNullOrWhiteSpace(Locale)) {
            throw new ArgumentException("locale must not be empty.", nameof(Locale));
        }

        if (string.IsNullOrWhiteSpace(TimeZone)) {
            throw new ArgumentException("time zone must not be empty.", nameof(TimeZone));
        }

        if (ImageIndex < 1) {
            throw new ArgumentException("image index must be at least 1.", nameof(ImageIndex));
        }

        if (ProductKey is not null && !IsProductKey(ProductKey)) {
            throw new ArgumentException("product key must be five groups of five alphanumeric characters.",
                nameof(ProductKey));
        }

        return this;
    }

    private static bool IsProductKey(string value) {
        var groups = value.Split('-');
        return groups.Length == 5 && groups.All(group => group.Length == 5 && group.All(char.IsAsciiLetterOrDigit));
    }
}
