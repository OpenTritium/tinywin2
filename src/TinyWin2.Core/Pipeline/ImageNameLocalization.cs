namespace TinyWin2.Core.Pipeline;

/// <summary>
/// Maps localized Windows edition display names (as shipped in zh-cn media) to the
/// canonical English index names that downstream consumers (dockur/windows answer-file
/// lookup, Setup templates) key on. Unknown names are left untouched by callers.
/// </summary>
public static class ImageNameLocalization {
    // Longest suffixes first: "专业教育版" must win over "教育版", "家庭单语言版" over "家庭版".
    private static readonly (string Suffix, string English)[] Editions = [
        ("家庭单语言版", "Home Single Language"),
        ("专业工作站版", "Pro Workstation"),
        ("专业教育版", "Pro Education"),
        ("家庭版", "Home"),
        ("专业版", "Pro"),
        ("教育版", "Education"),
        ("企业版", "Enterprise")
    ];

    /// <summary>
    /// "Windows 11 专业版" -> "Windows 11 Pro". Returns null when the name is already
    /// English or unknown, so callers can leave DISM's default (inherit source name).
    /// </summary>
    public static string? ToEnglish(string? name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        var trimmed = name.Trim();
        foreach (var (suffix, english) in Editions) {
            if (!trimmed.EndsWith(suffix, StringComparison.Ordinal)) {
                continue;
            }

            var prefix = trimmed[..^suffix.Length];
            if (prefix.Length == 0) {
                return null;
            }

            // Keep the "Windows <ver> " prefix as-is; only swap the edition token.
            return prefix + english;
        }

        return null;
    }
}
