using TinyWin2.Core.Executers.Registry;

namespace TinyWin2.Core.Tests;

/// <summary>Locks in the canonical reg.exe output parsing that every registry executer shares.</summary>
public sealed class RegQueryTests {
    private const string Listing =
        "\r\n"
        + "HKEY_LOCAL_MACHINE\\TinyWin2_system\\ControlSet001\\Services\r\n"
        + "HKEY_LOCAL_MACHINE\\TinyWin2_system\\ControlSet001\\Services\\W32Time\r\n"
        + "    Start    REG_DWORD    0x3\r\n"
        + "HKEY_LOCAL_MACHINE\\TinyWin2_system\\ControlSet001\\Services\\LanmanServer\r\n"
        + "    Start    REG_DWORD    0x2\r\n"
        + "    ImagePath    REG_EXPAND_SZ    C:\\Windows\\system32\\svchost.exe -k netsvcs -p\r\n";

    [Test]
    public async Task ParsesKeysInPrintOrderAndNormalizesToHklm() {
        var entries = RegQuery.Parse(Listing);

        await Assert.That(entries).Count().IsEqualTo(3);
        await Assert.That(entries[0].Key).IsEqualTo("HKLM\\TinyWin2_system\\ControlSet001\\Services");
        await Assert.That(entries[0].Values).Count().IsEqualTo(0);
        await Assert.That(entries[1].Key).IsEqualTo("HKLM\\TinyWin2_system\\ControlSet001\\Services\\W32Time");
        await Assert.That(entries[1].Values[0].Name).IsEqualTo("Start");
        await Assert.That(entries[1].Values[0].Type).IsEqualTo("REG_DWORD");
        await Assert.That(entries[1].Values[0].Data).IsEqualTo("0x3");
    }

    [Test]
    public async Task ParsesHklmFormInputWithoutRewriting() {
        var entries = RegQuery.Parse("HKLM\\TinyWin2_software\\K\r\n    Name    REG_SZ    value\r\n");

        await Assert.That(entries[0].Key).IsEqualTo("HKLM\\TinyWin2_software\\K");
        await Assert.That(entries[0].Values[0].Data).IsEqualTo("value");
    }

    [Test]
    public async Task ValueDataMayContainSpaces() {
        var entries = RegQuery.Parse(Listing);

        await Assert.That(entries[2].Values[1].Data)
            .IsEqualTo("C:\\Windows\\system32\\svchost.exe -k netsvcs -p");
    }

    [Test]
    public async Task NamedValuesSpansKeysInOrder() {
        var starts = RegQuery.NamedValues(Listing, "Start").ToList();

        await Assert.That(starts).Count().IsEqualTo(2);
        await Assert.That(starts[0]).IsEqualTo("0x3");
        await Assert.That(starts[1]).IsEqualTo("0x2");
        await Assert.That(RegQuery.NamedValues(Listing, "missing").ToList()).Count().IsEqualTo(0);
    }

    [Test]
    public async Task DirectChildKeysExcludeRootAndNestedKeys() {
        var root = "HKLM\\TinyWin2_system\\ControlSet001\\Services";
        var children = RegQuery.DirectChildKeys(Listing, root).ToList();

        await Assert.That(children).Count().IsEqualTo(2);
        await Assert.That(children[0]).IsEqualTo("W32Time");
        await Assert.That(children[1]).IsEqualTo("LanmanServer");
    }

    [Test]
    public async Task KeysUnderIncludesNestedKeys() {
        var root = "HKLM\\TinyWin2_system\\ControlSet001";

        await Assert.That(RegQuery.KeysUnder(Listing, root).ToList()).Count().IsEqualTo(3);
    }

    [Test]
    public async Task KeysWithNamedValueReturnsOnlyKeysDeclaringTheValue() {
        var keys = RegQuery.KeysWithNamedValue(Listing, "ImagePath").ToList();

        await Assert.That(keys).Count().IsEqualTo(1);
        await Assert.That(keys[0]).IsEqualTo("HKLM\\TinyWin2_system\\ControlSet001\\Services\\LanmanServer");
    }

    [Test]
    public async Task ValuesBeforeAnyKeyLineAreKeptUnderAnEmptyKey() {
        var entries = RegQuery.Parse("    Current    REG_DWORD    0x1\r\n");

        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Key).IsEqualTo("");
        await Assert.That(entries[0].Values[0].Name).IsEqualTo("Current");
    }

    [Test]
    public async Task ParseQueryValueKeepsFirstMatch() {
        await Assert.That(RegValues.ParseQueryValue(Listing, "Start")?.Data).IsEqualTo("0x3");
        await Assert.That(RegValues.ParseQueryValue(Listing, "Absent")).IsNull();
    }
}
