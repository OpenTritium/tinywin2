using System.Xml.Linq;
using TinyWin2.Core.Unattend;

namespace TinyWin2.Core.Tests;

public sealed class UnattendGeneratorTests {
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    private static XDocument Generate(UnattendFirmware firmware) =>
        XDocument.Parse(UnattendGenerator.Generate(new UnattendOptions { Firmware = firmware }));

    private static XElement Setup(XDocument document) =>
        document.Descendants(Ns + "component")
            .Single(element => (string?)element.Attribute("name") == "Microsoft-Windows-Setup");

    private static List<string> PartitionTypes(XDocument document) =>
        Setup(document).Descendants(Ns + "CreatePartition")
            .Select(element => element.Element(Ns + "Type")!.Value)
            .ToList();

    private static string InstallPartition(XDocument document) =>
        Setup(document).Descendants(Ns + "InstallTo").Single()
            .Element(Ns + "PartitionID")!.Value;

    [Test]
    public async Task BiosLayoutCreatesActiveSystemPartitionAndInstallsToSecondPartition() {
        var document = Generate(UnattendFirmware.Bios);
        await Assert.That(PartitionTypes(document)).IsEquivalentTo(new[] { "Primary", "Primary" });
        await Assert.That(InstallPartition(document)).IsEqualTo("2");

        var firstModify = document.Descendants(Ns + "ModifyPartition").First();
        await Assert.That(firstModify.Element(Ns + "Active")?.Value).IsEqualTo("true");
        await Assert.That(document.Descendants(Ns + "CreatePartition")
            .Any(element => element.Element(Ns + "Type")!.Value == "EFI")).IsFalse();
    }

    [Test]
    public async Task UefiLayoutCreatesEfiAndMsrAndInstallsToThirdPartition() {
        var document = Generate(UnattendFirmware.Uefi);
        await Assert.That(PartitionTypes(document)).IsEquivalentTo(new[] { "EFI", "MSR", "Primary" });
        await Assert.That(InstallPartition(document)).IsEqualTo("3");
        await Assert.That(document.Descendants(Ns + "ModifyPartition")
            .Any(element => element.Element(Ns + "Active") is not null)).IsFalse();
    }

    [Test]
    public async Task GeneratedFilesPassStructuralValidation() {
        foreach (var firmware in Enum.GetValues<UnattendFirmware>()) {
            var xml = UnattendGenerator.Generate(new UnattendOptions { Firmware = firmware });
            var errors = UnattendValidator.Validate(xml);
            await Assert.That(errors).IsEmpty();
        }
    }

    [Test]
    public async Task MinimalLayoutKeepsTheMsrAndDeletesTheRecoveryPartition() {
        var document = XDocument.Parse(UnattendGenerator.Generate(new UnattendOptions {
            Firmware = UnattendFirmware.Uefi,
            Layout = UnattendLayout.Minimal
        }));
        await Assert.That(PartitionTypes(document)).IsEquivalentTo(new[] { "EFI", "MSR", "Primary" });
        await Assert.That(InstallPartition(document)).IsEqualTo("3");
        await Assert.That(UnattendValidator.Validate(document.ToString())).IsEmpty();

        var commands = document.Descendants(Ns + "FirstLogonCommands")
            .Descendants(Ns + "CommandLine")
            .Select(element => element.Value)
            .ToList();
        await Assert.That(commands.Count).IsEqualTo(3);
        await Assert.That(commands[0]).Contains("reagentc /disable");
        await Assert.That(commands[1]).Contains("select partition 4");
        await Assert.That(commands[1]).Contains("delete partition override");
        await Assert.That(commands[1]).Contains("select partition 3");
        await Assert.That(commands[2]).Contains("reagentc /enable");
    }

    [Test]
    public async Task MinimalBiosLayoutDeletesThirdPartitionAndExtendsSecond() {
        var document = XDocument.Parse(UnattendGenerator.Generate(new UnattendOptions {
            Firmware = UnattendFirmware.Bios,
            Layout = UnattendLayout.Minimal
        }));
        await Assert.That(PartitionTypes(document)).IsEquivalentTo(new[] { "Primary", "Primary" });
        await Assert.That(InstallPartition(document)).IsEqualTo("2");

        var commands = document.Descendants(Ns + "FirstLogonCommands")
            .Descendants(Ns + "CommandLine")
            .Select(element => element.Value)
            .ToList();
        await Assert.That(commands[1]).Contains("select partition 3");
        await Assert.That(commands[1]).Contains("select partition 2");
    }

    [Test]
    public async Task StandardLayoutLeavesWinReAlone() {
        var document = Generate(UnattendFirmware.Uefi);
        await Assert.That(document.Descendants(Ns + "FirstLogonCommands")).IsEmpty();
    }

    [Test]
    public async Task OobeIsSkippedAndLocalAccountMatchesAutoLogon() {
        var document = Generate(UnattendFirmware.Bios);
        var oobe = document.Descendants(Ns + "OOBE").Single();
        await Assert.That(oobe.Element(Ns + "SkipMachineOOBE")!.Value).IsEqualTo("true");
        await Assert.That(oobe.Element(Ns + "SkipUserOOBE")!.Value).IsEqualTo("true");

        var account = document.Descendants(Ns + "LocalAccount").Single();
        var autoLogon = document.Descendants(Ns + "AutoLogon").Single();
        await Assert.That(account.Element(Ns + "Name")!.Value)
            .IsEqualTo(autoLogon.Element(Ns + "Username")!.Value);
        await Assert.That(account.Element(Ns + "Group")!.Value).IsEqualTo("Administrators");
    }

    [Test]
    public async Task ProductKeyIsEmptyWhenNotSuppliedAndFilledWhenSupplied() {
        var withoutKey = Generate(UnattendFirmware.Bios);
        var emptyKey = withoutKey.Descendants(Ns + "ProductKey").Single();
        await Assert.That(emptyKey.Element(Ns + "Key")!.Value).IsEqualTo("");
        await Assert.That(emptyKey.Element(Ns + "WillShowUI")!.Value).IsEqualTo("Never");

        var withKey = XDocument.Parse(UnattendGenerator.Generate(new UnattendOptions {
            Firmware = UnattendFirmware.Bios,
            ProductKey = "VK7JG-NPHTM-C97JM-9MPGT-3V66T"
        }));
        await Assert.That(withKey.Descendants(Ns + "ProductKey").Single()
            .Element(Ns + "Key")!.Value).IsEqualTo("VK7JG-NPHTM-C97JM-9MPGT-3V66T");
    }

    [Test]
    public async Task InvalidAccountNameIsRejected() {
        var ex = Assert.Throws<ArgumentException>(() => UnattendGenerator.Generate(new UnattendOptions {
            UserName = "bad/name"
        }));
        await Assert.That(ex.Message).Contains("account name");
    }

    [Test]
    public async Task ValidatorRejectsInstallTargetThatWasNotCreated() {
        var document = Generate(UnattendFirmware.Bios);
        document.Descendants(Ns + "InstallTo").Single().Element(Ns + "PartitionID")!.Value = "3";
        var errors = UnattendValidator.Validate(document.ToString());
        await Assert.That(errors.Any(error => error.Contains("does not create"))).IsTrue();
    }

    [Test]
    public async Task ValidatorRejectsMissingEulaAcceptance() {
        var document = Generate(UnattendFirmware.Bios);
        document.Descendants(Ns + "AcceptEula").Single().Value = "false";
        var errors = UnattendValidator.Validate(document.ToString());
        await Assert.That(errors.Any(error => error.Contains("AcceptEula"))).IsTrue();
    }

    [Test]
    public async Task ValidatorRejectsUefiLayoutWithActivePartition() {
        var document = Generate(UnattendFirmware.Uefi);
        var firstModify = document.Descendants(Ns + "ModifyPartition").First();
        firstModify.Add(new XElement(Ns + "Active", "true"));
        var errors = UnattendValidator.Validate(document.ToString());
        await Assert.That(errors.Any(error => error.Contains("must not mark any partition active"))).IsTrue();
    }

    [Test]
    public async Task ValidatorRejectsEfiLayoutWithoutMsr() {
        // The GPT disk without MSR this validator guards against is exactly what made Setup abort
        // with DiskLayoutMakeReserved: Couldn't create MSR partition (0x8007000e).
        var document = Generate(UnattendFirmware.Uefi);
        var setup = Setup(document);
        var msr = setup.Descendants(Ns + "CreatePartition")
            .Single(element => (string?)element.Element(Ns + "Type") == "MSR");
        msr.Remove();

        var errors = UnattendValidator.Validate(document.ToString());
        await Assert.That(errors.Any(error => error.Contains("EFI and MSR"))).IsTrue();
    }
}
