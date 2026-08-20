using TinyWin2.Core.Pipeline;

namespace TinyWin2.Core.Tests;

public sealed class RegTextDiffTests
{
    private const string Before = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\TinyWin2Diff_abc\Services\LanmanWorkstation]
        "Start"=dword:00000003
        "DelayedAutoStart"=dword:00000000
        "Keep"="value"

        [HKEY_LOCAL_MACHINE\TinyWin2Diff_abc\Policies]
        "Policy"=dword:00000001

        """;

    private const string After = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\TinyWin2Diff_abc\Services\LanmanWorkstation]
        "Start"=dword:00000004
        "Keep"="value"
        "New"="added"

        [HKEY_LOCAL_MACHINE\TinyWin2Diff_abc\Policies\Extra]
        @="default value"

        """;

    [Test]
    public async Task ParsesKeysAndValues()
    {
        var parsed = LayerInspector.ParseRegText(Before);

        await Assert.That(parsed.ContainsKey("Services\\LanmanWorkstation")).IsTrue();
        await Assert.That(parsed["Services\\LanmanWorkstation"]["Start"]).IsEqualTo("\"Start\"=dword:00000003");
        await Assert.That(parsed.ContainsKey("Policies")).IsTrue();
    }

    [Test]
    public async Task DiffsModifiedRemovedAdded()
    {
        var diff = LayerInspector.RegTextDiff("system", Before, After);

        var start = diff.Single(d => d.ValueName == "Start");
        await Assert.That(start.Kind).IsEqualTo("modified");
        await Assert.That(start.Before).Contains("00000003");
        await Assert.That(start.After).Contains("00000004");

        var delayed = diff.Single(d => d.ValueName == "DelayedAutoStart");
        await Assert.That(delayed.Kind).IsEqualTo("removed");

        var added = diff.Single(d => d.ValueName == "New");
        await Assert.That(added.Kind).IsEqualTo("added");

        var policy = diff.Single(d => d.ValueName == "Policy");
        await Assert.That(policy.Kind).IsEqualTo("removed");

        var defaultValue = diff.Single(d => d.ValueName == "(Default)");
        await Assert.That(defaultValue.Kind).IsEqualTo("added");
    }

    [Test]
    public async Task IdenticalTextsProduceEmptyDiff()
    {
        var diff = LayerInspector.RegTextDiff("software", Before, Before);

        await Assert.That(diff.Count).IsEqualTo(0);
    }
}
