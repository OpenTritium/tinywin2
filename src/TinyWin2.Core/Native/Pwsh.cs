namespace TinyWin2.Core.Native;

/// <summary>pwsh.exe invocation helpers shared by the ISO and VHD backends.</summary>
internal static class Pwsh {
    /// <summary>Arguments that run a script non-interactively with stable output.</summary>
    public static IReadOnlyList<string> Args(string script) =>
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script];

    /// <summary>PowerShell single-quoted literal — apostrophes in paths are doubled.</summary>
    public static string Quote(string value) => $"'{value.Replace("'", "''")}'";
}
