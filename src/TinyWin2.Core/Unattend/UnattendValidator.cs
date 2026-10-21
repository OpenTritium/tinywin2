using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace TinyWin2.Core.Unattend;

/// <summary>
///     Structural validation for Autounattend.xml files produced by <see cref="UnattendGenerator" /> or supplied
///     by a caller. It checks well-formedness, the disk layout contract, install targets, EULA acceptance and the
///     kiosk account/auto-logon consistency that Windows Setup silently rejects at different stages.
/// </summary>
public static class UnattendValidator {
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    /// <summary>Returns every violation found; an empty list means the file is structurally valid.</summary>
    public static IReadOnlyList<string> Validate(string xml) {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex) {
            return [$"not well-formed XML: {ex.Message}"];
        }

        var root = document.Root;
        if (root is null || root.Name != Ns + "unattend") {
            return ["root element must be <unattend> in namespace urn:schemas-microsoft-com:unattend."];
        }

        var errors = new List<string>();

        var setup = FindComponent(root, "windowsPE", "Microsoft-Windows-Setup");
        if (setup is null) {
            errors.Add("windowsPE: missing Microsoft-Windows-Setup component.");
        }
        else {
            ValidateSetup(setup, errors);
        }

        var specializeShell = FindComponent(root, "specialize", "Microsoft-Windows-Shell-Setup");
        if (specializeShell?.Element(Ns + "ComputerName")?.Value is { Length: > 0 } computerName
            && computerName.Length > 15) {
            errors.Add($"specialize: ComputerName '{computerName}' exceeds 15 characters.");
        }

        var oobeShell = FindComponent(root, "oobeSystem", "Microsoft-Windows-Shell-Setup");
        if (oobeShell is null) {
            errors.Add("oobeSystem: missing Microsoft-Windows-Shell-Setup component.");
        }
        else {
            ValidateOobeShell(oobeShell, errors);
        }

        return errors;
    }

    /// <summary>Throws <see cref="UnattendValidationException" /> when <see cref="Validate" /> reports any error.</summary>
    public static void EnsureValid(string xml) {
        var errors = Validate(xml);
        if (errors.Count > 0) {
            throw new UnattendValidationException(errors);
        }
    }

    private static void ValidateSetup(XElement setup, List<string> errors) {
        var diskConfiguration = setup.Element(Ns + "DiskConfiguration");
        var modifyPartitions = new List<XElement>();
        if (diskConfiguration is null) {
            errors.Add("windowsPE: DiskConfiguration is missing; setup would stop at the disk page.");
        }
        else {
            var disk = diskConfiguration.Elements(Ns + "Disk")
                .FirstOrDefault(element => (string?)element.Element(Ns + "DiskID") == "0");
            if (disk is null) {
                errors.Add("windowsPE: DiskConfiguration has no <Disk> with DiskID 0.");
            }
            else {
                if (!IsTrue(disk.Element(Ns + "WillWipeDisk"))) {
                    errors.Add("windowsPE: Disk 0 must set <WillWipeDisk>true</WillWipeDisk>.");
                }

                var createPartitions = disk.Element(Ns + "CreatePartitions")
                    ?.Elements(Ns + "CreatePartition").ToList() ?? [];
                modifyPartitions = disk.Element(Ns + "ModifyPartitions")
                    ?.Elements(Ns + "ModifyPartition").ToList() ?? [];
                if (createPartitions.Count == 0) {
                    errors.Add("windowsPE: Disk 0 has no <CreatePartitions> entries.");
                }

                // MSR and Extended partitions cannot be modified; listing one in <ModifyPartitions>
                // makes Setup reject the whole disk layout (0x80042405) before touching the disk.
                var unmodifiableOrders = createPartitions
                    .Where(element => element.Element(Ns + "Type")?.Value is { } type
                                      && (type.Equals("MSR", StringComparison.OrdinalIgnoreCase)
                                          || type.Equals("Extended", StringComparison.OrdinalIgnoreCase)))
                    .Select(element => (string?)element.Element(Ns + "Order"))
                    .OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);
                var modifyIds = modifyPartitions
                    .Select(element => (string?)element.Element(Ns + "PartitionID"))
                    .ToList();
                if (modifyIds.Any(id => id is not null && unmodifiableOrders.Contains(id))) {
                    errors.Add("windowsPE: MSR and Extended partitions cannot be modified.");
                }

                if (modifyIds.Count != createPartitions.Count - unmodifiableOrders.Count) {
                    errors.Add("windowsPE: every modifiable created partition needs exactly one <ModifyPartition>.");
                }

                var types = createPartitions
                    .Select(element => element.Element(Ns + "Type")?.Value ?? "")
                    .ToList();
                var hasEfi = types.Contains("EFI", StringComparer.OrdinalIgnoreCase);
                var hasMsr = types.Contains("MSR", StringComparer.OrdinalIgnoreCase);
                if (hasEfi != hasMsr) {
                    // Setup refuses a GPT disk without an MSR: it tries to fit one between the ESP and
                    // Windows and aborts with DiskLayoutMakeReserved when there is no room.
                    errors.Add("windowsPE: EFI and MSR partitions must be used together.");
                }

                var hasActive = modifyPartitions.Any(element => IsTrue(element.Element(Ns + "Active")));
                if (hasEfi && hasActive) {
                    errors.Add("windowsPE: a UEFI (EFI/MSR) layout must not mark any partition active.");
                }
                else if (!hasEfi && !hasActive) {
                    errors.Add("windowsPE: a BIOS (MBR) layout requires one active system partition.");
                }

                var ids = modifyPartitions
                    .Select(element => element.Element(Ns + "PartitionID")?.Value)
                    .Where(value => value is not null)
                    .ToHashSet(StringComparer.Ordinal);
                if (ids.Count == 0) {
                    errors.Add("windowsPE: <ModifyPartition> entries have no PartitionID.");
                }
            }
        }

        var installTo = setup.Element(Ns + "ImageInstall")?
            .Element(Ns + "OSImage")?
            .Element(Ns + "InstallTo");
        if (installTo is null) {
            errors.Add("windowsPE: ImageInstall/OSImage/InstallTo is missing.");
        }
        else {
            if ((string?)installTo.Element(Ns + "DiskID") != "0") {
                errors.Add("windowsPE: InstallTo must target DiskID 0.");
            }

            var partitionId = (string?)installTo.Element(Ns + "PartitionID");
            if (partitionId is null) {
                errors.Add("windowsPE: InstallTo has no PartitionID.");
            }
            else if (modifyPartitions.Count > 0
                     && !modifyPartitions.Any(element =>
                         (string?)element.Element(Ns + "PartitionID") == partitionId)) {
                errors.Add($"windowsPE: InstallTo references partition {partitionId}, which DiskConfiguration does not create.");
            }
            else {
                var target = modifyPartitions.FirstOrDefault(element =>
                    (string?)element.Element(Ns + "PartitionID") == partitionId);
                var format = (string?)target?.Element(Ns + "Format");
                if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase)) {
                    errors.Add("windowsPE: the Windows partition must be formatted NTFS.");
                }
            }
        }

        var imageIndex = setup.Element(Ns + "ImageInstall")?
            .Element(Ns + "OSImage")?
            .Element(Ns + "InstallFrom")?
            .Elements(Ns + "MetaData")
            .FirstOrDefault(metadata => (string?)metadata.Element(Ns + "Key") == "/IMAGE/INDEX")?
            .Element(Ns + "Value")?.Value;
        if (imageIndex is null
            || !int.TryParse(imageIndex, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 1) {
            errors.Add("windowsPE: InstallFrom must select /IMAGE/INDEX with a positive index.");
        }

        if (!IsTrue(setup.Element(Ns + "UserData")?.Element(Ns + "AcceptEula"))) {
            errors.Add("windowsPE: UserData must set <AcceptEula>true</AcceptEula>.");
        }

        var productKey = setup.Element(Ns + "UserData")?.Element(Ns + "ProductKey");
        if (productKey is null) {
            errors.Add("windowsPE: UserData must contain <ProductKey> (an empty <Key> is fine) " +
                       "so setup does not stop at the product key page.");
        }
        else if (!string.Equals((string?)productKey.Element(Ns + "WillShowUI"), "Never",
                     StringComparison.OrdinalIgnoreCase)) {
            errors.Add("windowsPE: ProductKey must set <WillShowUI>Never</WillShowUI>.");
        }
    }

    private static void ValidateOobeShell(XElement shell, List<string> errors) {
        var localAccount = shell.Element(Ns + "UserAccounts")?
            .Element(Ns + "LocalAccounts")?
            .Elements(Ns + "LocalAccount")
            .FirstOrDefault();

        var autoLogon = shell.Element(Ns + "AutoLogon");
        if (autoLogon is not null && IsTrue(autoLogon.Element(Ns + "Enabled"))) {
            var userName = (string?)autoLogon.Element(Ns + "Username");
            if (string.IsNullOrWhiteSpace(userName)) {
                errors.Add("oobeSystem: AutoLogon is enabled but has no Username.");
            }
            else if (localAccount is not null && (string?)localAccount.Element(Ns + "Name") != userName) {
                errors.Add("oobeSystem: AutoLogon Username does not match the local account Name.");
            }

            if (string.IsNullOrEmpty((string?)autoLogon.Element(Ns + "Password")?.Element(Ns + "Value"))) {
                errors.Add("oobeSystem: AutoLogon is enabled but has no plain-text password value.");
            }
        }

        if (localAccount is null) {
            return;
        }

        var name = (string?)localAccount.Element(Ns + "Name");
        if (string.IsNullOrWhiteSpace(name)) {
            errors.Add("oobeSystem: LocalAccount has no Name.");
        }
        else if (name.Length > 20) {
            errors.Add($"oobeSystem: LocalAccount name '{name}' exceeds 20 characters.");
        }

        if (string.IsNullOrEmpty((string?)localAccount.Element(Ns + "Password")?.Element(Ns + "Value"))) {
            errors.Add("oobeSystem: LocalAccount has no plain-text password value.");
        }
    }

    private static XElement? FindComponent(XElement root, string pass, string name) =>
        root.Elements(Ns + "settings")
            .Where(settings => (string?)settings.Attribute("pass") == pass)
            .SelectMany(settings => settings.Elements(Ns + "component"))
            .FirstOrDefault(component => (string?)component.Attribute("name") == name);

    private static bool IsTrue(XElement? element) =>
        string.Equals(element?.Value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Raised when an Autounattend.xml fails <see cref="UnattendValidator" />.</summary>
public sealed class UnattendValidationException : Exception {
    public UnattendValidationException(IReadOnlyList<string> errors)
        : base("invalid Autounattend.xml: " + string.Join(" ", errors)) {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
