using System.Text.RegularExpressions;

namespace TinyWin2.Core.Executers;

/// <summary>PowerShell -like wildcards (* and ?) anchored for full-string matching.</summary>
internal static class LikePattern {
    public static Regex ToRegex(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
