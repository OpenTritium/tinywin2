using System.Text.RegularExpressions;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>One value row from reg.exe query output.</summary>
public sealed record RegQueryValue(string Name, string Type, string Data);

/// <summary>One key entry: the HKLM-normalized key path plus the value rows printed under it.</summary>
public sealed record RegQueryKey(string Key, IReadOnlyList<RegQueryValue> Values);

/// <summary>
///     The single parser for reg.exe query output (plain, /v, and /s forms). Key lines are
///     normalized to the compact HKLM form reg.exe accepts back as input, so callers compare
///     against hive keys without rewriting the HKEY_LOCAL_MACHINE prefix themselves.
/// </summary>
public static partial class RegQuery {
    /// <summary>
    ///     Parses every key entry in print order; value rows before any key line are kept
    ///     under an empty key so line-oriented consumers keep working.
    /// </summary>
    public static IReadOnlyList<RegQueryKey> Parse(string output) {
        var entries = new List<RegQueryKey>();
        var values = new List<RegQueryValue>();
        string? currentKey = null;
        foreach (var rawLine in output.Split('\n')) {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) {
                continue;
            }

            if (KeyLine().IsMatch(line)) {
                if (currentKey is not null || values.Count > 0) {
                    entries.Add(new(currentKey ?? "", [.. values]));
                    values.Clear();
                }

                currentKey = Normalize(line);
                continue;
            }

            var match = ValueLine().Match(line);
            if (match.Success) {
                values.Add(new(match.Groups["name"].Value.Trim(), match.Groups["type"].Value,
                    match.Groups["data"].Value));
            }
        }

        if (currentKey is not null || values.Count > 0) {
            entries.Add(new(currentKey ?? "", [.. values]));
        }

        return entries;
    }

    /// <summary>Ordered (trimmed) data of every value named <paramref name="valueName" />, across all keys.</summary>
    public static IEnumerable<string> NamedValues(string output, string valueName) =>
        Parse(output)
            .SelectMany(entry => entry.Values)
            .Where(value => string.Equals(value.Name, valueName, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Data.Trim());

    /// <summary>All key paths at or below <paramref name="rootKey" />, in print order.</summary>
    public static IEnumerable<string> KeysUnder(string output, string rootKey) {
        var prefix = rootKey.TrimEnd('\\') + "\\";
        return Parse(output)
            .Select(entry => entry.Key)
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Child key names directly below <paramref name="rootKey" />, in print order.</summary>
    public static IEnumerable<string> DirectChildKeys(string output, string rootKey) =>
        KeysUnder(output, rootKey)
            .Select(key => key[(rootKey.TrimEnd('\\').Length + 1)..])
            .Where(suffix => suffix.Length > 0 && !suffix.Contains('\\'));

    /// <summary>Keys whose listing declares a value named <paramref name="valueName" />.</summary>
    public static IEnumerable<string> KeysWithNamedValue(string output, string valueName) =>
        Parse(output)
            .Where(entry => entry.Values.Any(value =>
                string.Equals(value.Name, valueName, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Key);

    private static string Normalize(string key) =>
        key.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
            ? "HKLM\\" + key["HKEY_LOCAL_MACHINE\\".Length..]
            : key;

    [GeneratedRegex(@"^(?:HKEY_LOCAL_MACHINE|HKLM)\\.+$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLine();

    /// <summary>reg query value line: columns of name, type, data separated by runs of spaces.</summary>
    [GeneratedRegex(@"^\s*(?<name>[^\s].*?)\s{2,}(?<type>REG_[A-Z_]+)\s{2,}(?<data>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ValueLine();
}
