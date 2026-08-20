using System.Security.Principal;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Env;

public sealed record CheckResult(string Name, bool Ok, bool Required, string Detail);

public static class EnvironmentDoctor {
    private static readonly (string Tool, bool Required)[] Tools = [
        ("dism.exe", true),
        ("reg.exe", true),
        ("diskpart.exe", true),
        ("robocopy.exe", true),
        ("pwsh.exe", true),
        ("oscdimg.exe", false), // optional: only needed for bootable ISO output
    ];

    private const long MinimumFreeBytes = 50L * 1024 * 1024 * 1024;

    public static bool IsAdministrator() =>
        OperatingSystem.IsWindows()
        && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static IReadOnlyList<CheckResult> Check(string? outputDirectoryHint = null) {
        var elevated = IsAdministrator();
        var results = new List<CheckResult> {
            new("administrator", elevated, true, elevated ? "running elevated" : "must run as administrator"),
        };
        results.AddRange(Tools.Select(t => {
            var path = ToolLocator.Locate(t.Tool);
            return new CheckResult(t.Tool, path is not null, t.Required, path ?? "not found on PATH");
        }));
        if (outputDirectoryHint is not null) {
            results.Add(CheckFreeSpace(outputDirectoryHint, MinimumFreeBytes));
        }

        return results;
    }

    public static CheckResult CheckFreeSpace(string pathHint, long minimumBytes) {
        try {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pathHint))!);
            var free = drive.AvailableFreeSpace;
            return new CheckResult(
                "free-space",
                free >= minimumBytes,
                true,
                $"{drive.Name} {free / 1024.0 / 1024 / 1024:F1} GB free (need {minimumBytes / 1024.0 / 1024 / 1024:F0} GB)");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) {
            return new CheckResult("free-space", false, true, $"cannot inspect '{pathHint}': {ex.Message}");
        }
    }
}
