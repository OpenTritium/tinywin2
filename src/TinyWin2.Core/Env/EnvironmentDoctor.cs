using System.Security.Principal;

namespace TinyWin2.Core.Env;

/// <summary>One environment check outcome.</summary>
public sealed record CheckResult(string Name, bool Ok, bool Required, string Detail)
{
    public System.Text.Json.Nodes.JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["ok"] = Ok,
        ["required"] = Required,
        ["detail"] = Detail,
    };
}

/// <summary>Probes the host for everything the build pipeline needs. Read-only; never mutates.</summary>
public static class EnvironmentDoctor
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static IReadOnlyList<CheckResult> Check(string? outputDirectoryHint = null)
    {
        var results = new List<CheckResult>();

        results.Add(new CheckResult(
            "administrator",
            IsAdministrator(),
            Required: true,
            Detail: IsAdministrator() ? "running elevated" : "must run as administrator"));

        foreach (var (tool, required) in new[]
                 {
                     ("dism.exe", true),
                     ("reg.exe", true),
                     ("diskpart.exe", true),
                     ("robocopy.exe", true),
                     ("pwsh.exe", true),
                     ("oscdimg.exe", false),
                 })
        {
            var path = Native.ToolLocator.Locate(tool, System32Directory());
            results.Add(new CheckResult(
                tool,
                path is not null,
                required,
                path ?? "not found on PATH or in System32"));
        }

        if (outputDirectoryHint is not null)
        {
            results.Add(CheckFreeSpace(outputDirectoryHint, minimumBytes: 50L * 1024 * 1024 * 1024));
        }

        return results;
    }

    public static CheckResult CheckFreeSpace(string pathHint, long minimumBytes)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(pathHint, "."));
            var drive = new DriveInfo(Path.GetPathRoot(root) ?? Path.GetPathRoot(AppContext.BaseDirectory)!);
            var free = drive.AvailableFreeSpace;
            return new CheckResult(
                "free-space",
                free >= minimumBytes,
                Required: true,
                $"{drive.Name} {free / 1024.0 / 1024 / 1024:F1} GB free (need {minimumBytes / 1024.0 / 1024 / 1024:F0} GB)");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new CheckResult("free-space", false, true, $"cannot inspect '{pathHint}': {ex.Message}");
        }
    }

    private static string System32Directory() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "..")
            : "";
}
