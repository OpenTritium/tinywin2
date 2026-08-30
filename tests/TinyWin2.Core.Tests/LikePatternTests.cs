using System.Text.RegularExpressions;
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
        await Assert.That(RegexOracle(pattern).IsMatch(value)).IsEqualTo(expected);
    }

    /// <summary>The regex form of the wildcard dialect — the oracle IsMatch was validated against.</summary>
    private static Regex RegexOracle(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
