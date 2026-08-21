namespace TinyWin2.Core.Executers.Dism;

/// <summary>Parses dism.exe <c>/Format:List</c> output into records of key/value pairs.</summary>
public static class DismListParser {
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(
        string output,
        string recordStartKey) {
        var records = new List<IReadOnlyDictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var rawLine in output.Split('\n')) {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals(recordStartKey, StringComparison.OrdinalIgnoreCase)) {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                records.Add(current);
            }

            current?[key] = value;
        }

        return records;
    }

    public static string? Get(IReadOnlyDictionary<string, string> record, string key) =>
        record.GetValueOrDefault(key);
}
