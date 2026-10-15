using TinyWin2.Core.Executers.Registry;

namespace TinyWin2.Core.Tests;

public sealed class DeleteKeyExpanderTests {
    private const string AppxApplications =
        @"Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";

    [Test]
    public async Task ExpandsAWildcardFinalSegmentIntoTheMatchingChildren() {
        string[] children = [
            "Microsoft.SecHealthUI_1000.26100.1.0_x64__8wekyb3d8bbwe",
            "Contoso.Unrelated_1.0_x64__8wekyb3d8bbwe",
            "Microsoft.SecHealthUI_1000.26100.2.0_x64__8wekyb3d8bbwe"
        ];

        var expanded = DeleteKeyExpander.Expand($@"{AppxApplications}\Microsoft.SecHealthUI_*", children);

        await Assert.That(expanded.Count).IsEqualTo(2);
        // enumeration order of the children is preserved
        await Assert.That(expanded[0])
            .IsEqualTo($@"{AppxApplications}\Microsoft.SecHealthUI_1000.26100.1.0_x64__8wekyb3d8bbwe");
        await Assert.That(expanded[1])
            .IsEqualTo($@"{AppxApplications}\Microsoft.SecHealthUI_1000.26100.2.0_x64__8wekyb3d8bbwe");
    }

    [Test]
    public async Task MatchesTheFinalSegmentWithQuestionMarkWildcards() {
        var expanded = DeleteKeyExpander.Expand(@"Services\W?32Time", ["W732Time", "W7732Time", "Other"]);

        await Assert.That(expanded.Count).IsEqualTo(1);
        await Assert.That(expanded[0]).IsEqualTo(@"Services\W732Time");
    }

    [Test]
    public async Task PatternMatchingNothingExpandsToNothing() {
        await Assert.That(DeleteKeyExpander.Expand($@"{AppxApplications}\Microsoft.SecHealthUI_*", ["Contoso.App"]))
            .IsEmpty();
    }

    [Test]
    public async Task LiteralEntryExpandsToItself() {
        await Assert.That(DeleteKeyExpander.IsPattern(@"Policies\Gone")).IsFalse();
        await Assert.That(DeleteKeyExpander.Expand(@"Policies\Gone", ["Ignored"]).Single())
            .IsEqualTo(@"Policies\Gone");
        // backslashes around the declared key are trimmed, matching KeyUnderHive/Apply
        await Assert.That(DeleteKeyExpander.Expand(@"\Policies\Gone\", []).Single())
            .IsEqualTo(@"Policies\Gone");
    }

    [Test]
    public async Task OnlyTheFinalSegmentIsAPattern() {
        await Assert.That(DeleteKeyExpander.IsPattern(@"Users\*\AppData\Local\Microsoft\OneDrive")).IsFalse();
        await Assert.That(DeleteKeyExpander.ParentKey(@"Users\*\AppData\Local\Microsoft\OneDrive"))
            .IsEqualTo(@"Users\*\AppData\Local\Microsoft");
        await Assert.That(DeleteKeyExpander.IsPattern(@"Applications\Microsoft.SecHealthUI_*")).IsTrue();
        await Assert.That(DeleteKeyExpander.ParentKey(@"Applications\Microsoft.SecHealthUI_*"))
            .IsEqualTo("Applications");
        await Assert.That(DeleteKeyExpander.ParentKey("*")).IsEqualTo("");
    }
}
