namespace TinyWin2.Core.Executers.Dism;

public static class DismListParser {
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(
        string output,
        string recordStartKey) {
        var records = new List<IReadOnlyDictionary<string, string>>();
        Dictionary<string, string>? current = null;
        var remaining = output.AsSpan();
        while (!remaining.IsEmpty) {
            var newline = remaining.IndexOf('\n');
            var rawLine = newline >= 0 ? remaining[..newline] : remaining;
            remaining = newline >= 0 ? remaining[(newline + 1)..] : [];
            if (rawLine.EndsWith("\r", StringComparison.Ordinal)) {
                rawLine = rawLine[..^1];
            }

            var line = rawLine.Trim();
            if (line.Length == 0) {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0) {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals(recordStartKey, StringComparison.OrdinalIgnoreCase)) {
                current = new(StringComparer.OrdinalIgnoreCase);
                records.Add(current);
            }

            current?[key.ToString()] = value.ToString();
        }

        return records;
    }

    public static string? Get(IReadOnlyDictionary<string, string> record, string key) =>
        record.GetValueOrDefault(key);

    /// <summary>Maps each record's identity field to its State, the common shape of list-then-converge executers.</summary>
    public static IReadOnlyDictionary<string, string> BuildStateMap(
        IReadOnlyList<IReadOnlyDictionary<string, string>> records, string identityKey) =>
        records.ToDictionary(
            r => Get(r, identityKey) ?? "",
            r => Get(r, "State") ?? "",
            StringComparer.OrdinalIgnoreCase);
}
