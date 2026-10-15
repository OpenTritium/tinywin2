namespace TinyWin2.Core.Executers.Registry;

/// <summary>
///     Expands one <c>deleteKeys</c> entry into the concrete key paths to remove. Only the final
///     path segment may carry a <see cref="LikePattern" /> wildcard; the parent path stays literal
///     and its direct child keys are the candidates.
/// </summary>
internal static class DeleteKeyExpander {
    /// <summary>True when the entry's final segment is a wildcard pattern; the parent is always literal.</summary>
    public static bool IsPattern(string entry) => HasWildcard(Split(entry).Segment);

    /// <summary>The literal parent path of <paramref name="entry" />; empty for a single-segment entry.</summary>
    public static string ParentKey(string entry) => Split(entry).Parent;

    /// <summary>
    ///     The concrete key paths <paramref name="entry" /> stands for: a literal entry expands to
    ///     itself, a pattern entry to the child names it matches, in enumeration order.
    /// </summary>
    public static IReadOnlyList<string> Expand(string entry, IReadOnlyList<string> childNames) {
        var (parent, segment) = Split(entry);
        if (!HasWildcard(segment)) {
            return [Join(parent, segment)];
        }

        return [.. childNames
            .Where(name => LikePattern.IsMatch(segment, name))
            .Select(name => Join(parent, name))];
    }

    private static bool HasWildcard(string segment) => segment.Contains('*') || segment.Contains('?');

    private static string Join(string parent, string segment) =>
        parent.Length == 0 ? segment : $"{parent}\\{segment}";

    private static (string Parent, string Segment) Split(string entry) {
        var trimmed = entry.Trim('\\');
        var separator = trimmed.LastIndexOf('\\');
        return separator < 0
            ? ("", trimmed)
            : (trimmed[..separator], trimmed[(separator + 1)..]);
    }
}
