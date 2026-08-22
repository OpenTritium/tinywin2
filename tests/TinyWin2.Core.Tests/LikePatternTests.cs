using TinyWin2.Core.Executers;

namespace TinyWin2.Core.Tests;

public sealed class LikePatternTests {
    [Test]
    [Arguments("Service*", "Service", true)]
    [Arguments("Service*", "ServiceHost", true)]
    [Arguments("Service*", "OtherService", false)]
    [Arguments("W?32Time", "W732Time", true)]
    [Arguments("W?32Time", "W7732Time", false)]
    [Arguments("*Network*", "NetworkService", true)]
    [Arguments("*Network*", "Workstation", false)]
    [Arguments("**", "anything", true)]
    [Arguments("", "", true)]
    [Arguments("", "value", false)]
    public async Task MatchesLikeRegexSemantics(string pattern, string value, bool expected) {
        await Assert.That(LikePattern.IsMatch(pattern, value)).IsEqualTo(expected);
        await Assert.That(LikePattern.IsMatch(pattern, value.ToLowerInvariant()))
            .IsEqualTo(expected);
        await Assert.That(LikePattern.ToRegex(pattern).IsMatch(value)).IsEqualTo(expected);
    }
}
