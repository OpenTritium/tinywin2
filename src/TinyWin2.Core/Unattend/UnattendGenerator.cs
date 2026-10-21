using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TinyWin2.Core.Unattend;

/// <summary>
///     Builds an Autounattend.xml that wipes the target disk, installs the selected index, skips both OOBE
///     phases, creates a local administrator and signs it in automatically. The file is placed at the media
///     root by <c>tinywin2 package iso</c>; a caller-supplied file can still replace it.
/// </summary>
public static class UnattendGenerator {
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private const string ProcessorArchitecture = "amd64";
    private const string PublicKeyToken = "31bf3856ad364e35";

    /// <summary>Generates the answer file text (UTF-8, no BOM, CRLF line endings).</summary>
    public static string Generate(UnattendOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var windowsPartition = options.Firmware == UnattendFirmware.Uefi ? 3 : 2;
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", Wcm),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                Settings("windowsPE",
                    InternationalCoreWinPe(options),
                    Setup(options, windowsPartition)),
                Settings("specialize",
                    SpecializeShell(options),
                    InternationalCore(options)),
                Settings("oobeSystem",
                    OobeShell(options))));

        return Serialize(document);
    }

    private static XElement Settings(string pass, params object?[] components) =>
        new(Ns + "settings", new XAttribute("pass", pass), components);

    private static XElement Component(string name, params object?[] content) =>
        new(Ns + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", ProcessorArchitecture),
            new XAttribute("publicKeyToken", PublicKeyToken),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            content);

    private static XElement InternationalCoreWinPe(UnattendOptions options) =>
        Component("Microsoft-Windows-International-Core-WinPE",
            new XElement(Ns + "SetupUILanguage", new XElement(Ns + "UILanguage", options.Locale)),
            new XElement(Ns + "InputLocale", options.Locale),
            new XElement(Ns + "SystemLocale", options.Locale),
            new XElement(Ns + "UILanguage", options.Locale),
            new XElement(Ns + "UserLocale", options.Locale));

    private static XElement InternationalCore(UnattendOptions options) =>
        Component("Microsoft-Windows-International-Core",
            new XElement(Ns + "InputLocale", options.Locale),
            new XElement(Ns + "SystemLocale", options.Locale),
            new XElement(Ns + "UILanguage", options.Locale),
            new XElement(Ns + "UserLocale", options.Locale));

    private static XElement Setup(UnattendOptions options, int windowsPartition) =>
        Component("Microsoft-Windows-Setup",
            RunSynchronousLabConfig(),
            DiskConfiguration(options.Firmware),
            ImageInstall(options.ImageIndex, windowsPartition),
            UserData(options));

    /// <summary>Windows 11 setup checks TPM/SecureBoot/RAM/CPU before the disk page; VMs without them still install.</summary>
    private static XElement RunSynchronousLabConfig() =>
        new(Ns + "RunSynchronous",
            RunCommand(1, @"reg add HKLM\SYSTEM\Setup\LabConfig /v BypassRAMCheck /t REG_DWORD /d 1 /f"),
            RunCommand(2, @"reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f"),
            RunCommand(3, @"reg add HKLM\SYSTEM\Setup\LabConfig /v BypassSecureBootCheck /t REG_DWORD /d 1 /f"),
            RunCommand(4, @"reg add HKLM\SYSTEM\Setup\LabConfig /v BypassCPUCheck /t REG_DWORD /d 1 /f"));

    private static XElement RunCommand(int order, string path) =>
        new(Ns + "RunSynchronousCommand",
            new XAttribute(Wcm + "action", "add"),
            new XElement(Ns + "Order", order.ToString(CultureInfo.InvariantCulture)),
            new XElement(Ns + "Path", path));

    private static XElement DiskConfiguration(UnattendFirmware firmware) {
        var disk = new XElement(Ns + "Disk",
            new XAttribute(Wcm + "action", "add"),
            new XElement(Ns + "DiskID", "0"),
            new XElement(Ns + "WillWipeDisk", "true"));
        if (firmware == UnattendFirmware.Bios) {
            disk.Add(
                new XElement(Ns + "CreatePartitions",
                    CreatePartition(1, "Primary", size: 100),
                    CreatePartition(2, "Primary", extend: true)),
                new XElement(Ns + "ModifyPartitions",
                    ModifyPartition(1, 1, active: true, format: "NTFS", label: "System"),
                    ModifyPartition(2, 2, format: "NTFS", label: "Windows", letter: "C")));
        }
        else {
            disk.Add(
                new XElement(Ns + "CreatePartitions",
                    CreatePartition(1, "EFI", size: 260),
                    CreatePartition(2, "MSR", size: 128),
                    CreatePartition(3, "Primary", extend: true)),
                new XElement(Ns + "ModifyPartitions",
                    // Order is the modification sequence, PartitionID the created partition: the MSR
                    // (partition 2) cannot be modified, so the Windows entry is second with ID 3.
                    ModifyPartition(1, 1, format: "FAT32", label: "System"),
                    ModifyPartition(2, 3, format: "NTFS", label: "Windows", letter: "C")));
        }

        return new XElement(Ns + "DiskConfiguration",
            new XElement(Ns + "WillShowUI", "Never"),
            disk);
    }

    private static XElement CreatePartition(int order, string type, int? size = null, bool extend = false) {
        var element = new XElement(Ns + "CreatePartition", new XAttribute(Wcm + "action", "add"),
            new XElement(Ns + "Order", order.ToString(CultureInfo.InvariantCulture)));
        if (extend) {
            element.Add(new XElement(Ns + "Extend", "true"));
        }
        else {
            element.Add(new XElement(Ns + "Size", size!.Value.ToString(CultureInfo.InvariantCulture)));
        }

        element.Add(new XElement(Ns + "Type", type));
        return element;
    }

    private static XElement ModifyPartition(
        int order,
        int partitionId,
        bool? active = null,
        string? format = null,
        string? label = null,
        string? letter = null) {
        var element = new XElement(Ns + "ModifyPartition", new XAttribute(Wcm + "action", "add"),
            new XElement(Ns + "Order", order.ToString(CultureInfo.InvariantCulture)),
            new XElement(Ns + "PartitionID", partitionId.ToString(CultureInfo.InvariantCulture)));
        if (active is true) {
            element.Add(new XElement(Ns + "Active", "true"));
        }

        if (format is not null) {
            element.Add(new XElement(Ns + "Format", format));
        }

        if (label is not null) {
            element.Add(new XElement(Ns + "Label", label));
        }

        if (letter is not null) {
            element.Add(new XElement(Ns + "Letter", letter));
        }

        return element;
    }

    private static XElement ImageInstall(int imageIndex, int windowsPartition) =>
        new(Ns + "ImageInstall",
            new XElement(Ns + "OSImage",
                new XElement(Ns + "InstallFrom",
                    new XElement(Ns + "MetaData",
                        new XAttribute(Wcm + "action", "add"),
                        new XElement(Ns + "Key", "/IMAGE/INDEX"),
                        new XElement(Ns + "Value", imageIndex.ToString(CultureInfo.InvariantCulture)))),
                new XElement(Ns + "InstallTo",
                    new XElement(Ns + "DiskID", "0"),
                    new XElement(Ns + "PartitionID", windowsPartition.ToString(CultureInfo.InvariantCulture))),
                new XElement(Ns + "WillShowUI", "Never")));

    private static XElement UserData(UnattendOptions options) =>
        new(Ns + "UserData",
            new XElement(Ns + "AcceptEula", "true"),
            new XElement(Ns + "FullName", options.EffectiveDisplayName),
            new XElement(Ns + "Organization", "TinyWin"),
            // An explicit (possibly empty) ProductKey with WillShowUI=Never is what keeps setup
            // from stopping at the key page on an unattended install.
            new XElement(Ns + "ProductKey",
                new XElement(Ns + "Key", options.ProductKey ?? ""),
                new XElement(Ns + "WillShowUI", "Never")));

    private static XElement SpecializeShell(UnattendOptions options) =>
        Component("Microsoft-Windows-Shell-Setup",
            new XElement(Ns + "ComputerName", options.ComputerName),
            new XElement(Ns + "TimeZone", options.TimeZone));

    private static XElement OobeShell(UnattendOptions options) =>
        Component("Microsoft-Windows-Shell-Setup",
            new XElement(Ns + "AutoLogon",
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "LogonCount", "999"),
                new XElement(Ns + "Username", options.UserName),
                PasswordElement(options.Password)),
            new XElement(Ns + "UserAccounts",
                new XElement(Ns + "LocalAccounts",
                    new XElement(Ns + "LocalAccount",
                        new XAttribute(Wcm + "action", "add"),
                        new XElement(Ns + "Name", options.UserName),
                        new XElement(Ns + "DisplayName", options.EffectiveDisplayName),
                        new XElement(Ns + "Description", "TinyWin kiosk administrator"),
                        new XElement(Ns + "Group", "Administrators"),
                        PasswordElement(options.Password)))),
            options.Layout == UnattendLayout.Minimal ? FirstLogonWinReCommands(options.Firmware) : null,
            new XElement(Ns + "OOBE",
                new XElement(Ns + "SkipMachineOOBE", "true"),
                new XElement(Ns + "SkipUserOOBE", "true"),
                new XElement(Ns + "HideEULAPage", "true"),
                new XElement(Ns + "HideLocalAccountScreen", "true"),
                new XElement(Ns + "HideOEMRegistrationScreen", "true"),
                new XElement(Ns + "HideOnlineAccountScreens", "true"),
                new XElement(Ns + "HideWirelessSetupInOOBE", "true"),
                new XElement(Ns + "NetworkLocation", "Work"),
                new XElement(Ns + "ProtectYourPC", "3")));

    /// <summary>
    ///     Setup always carves a WinRE partition out of the OS volume on NTFS installs. These commands move
    ///     WinRE onto C: and delete that partition, leaving ESP + MSR + Windows (UEFI) or
    ///     System Reserved + Windows (BIOS).
    /// </summary>
    private static XElement FirstLogonWinReCommands(UnattendFirmware firmware) {
        // Setup appends the recovery partition after the OS partition, so its number follows the
        // three-partition UEFI layout and the two-partition BIOS layout.
        var windows = firmware == UnattendFirmware.Uefi ? 3 : 2;
        var recovery = windows + 1;
        return new XElement(Ns + "FirstLogonCommands",
            SynchronousCommand(1, "Move WinRE off the recovery partition",
                "cmd /c reagentc /disable"),
            SynchronousCommand(2, "Delete the setup-created recovery partition and extend Windows",
                $"cmd /c (echo select disk 0& echo select partition {recovery}& echo delete partition override&"
                + $" echo select partition {windows}& echo extend)| diskpart"),
            SynchronousCommand(3, "Register WinRE on C:\\Recovery\\WindowsRE",
                "cmd /c if not exist C:\\Recovery\\WindowsRE mkdir C:\\Recovery\\WindowsRE&"
                + " reagentc /setreimage /path C:\\Recovery\\WindowsRE& reagentc /enable"));
    }

    private static XElement SynchronousCommand(int order, string description, string commandLine) =>
        new(Ns + "SynchronousCommand",
            new XAttribute(Wcm + "action", "add"),
            new XElement(Ns + "Order", order.ToString(CultureInfo.InvariantCulture)),
            new XElement(Ns + "Description", description),
            new XElement(Ns + "CommandLine", commandLine));

    private static XElement PasswordElement(string password) =>
        new(Ns + "Password",
            new XElement(Ns + "Value", password),
            new XElement(Ns + "PlainText", "true"));

    private static string Serialize(XDocument document) {
        var settings = new XmlWriterSettings {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = false
        };
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings)) {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
