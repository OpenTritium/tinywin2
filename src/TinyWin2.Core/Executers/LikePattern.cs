using System.Text.RegularExpressions;

namespace TinyWin2.Core.Executers;

/// <summary>PowerShell -like wildcards (* and ?) anchored for full-string matching.</summary>
internal static class LikePattern {
    public static Regex ToRegex(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsMatch(string pattern, string value) {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var starValueIndex = 0;
        while (valueIndex < value.Length) {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?'
                    || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex]))) {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*') {
                starIndex = patternIndex++;
                starValueIndex = valueIndex;
            }
            else if (starIndex >= 0) {
                patternIndex = starIndex + 1;
                valueIndex = ++starValueIndex;
            }
            else {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
