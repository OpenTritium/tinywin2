namespace TinyWin2.Core.Executers;

/// <summary>Root-containment check shared by every resolver that must stay inside a root.</summary>
internal static class SafePath {
    /// <summary>
    ///     Full path of <paramref name="relative" /> inside <paramref name="root" />, or null
    ///     when it escapes the root. Separators are normalized; the root need not exist.
    /// </summary>
    public static string? TryResolveInside(string root, string relative) {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)));
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }
}
