using System.Runtime.Versioning;
using System.Security.Principal;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Env;

public sealed record CheckResult(string Name, bool Ok, bool Required, string Detail);

[SupportedOSPlatform("windows")]
public static class EnvironmentDoctor {
    /// <summary>System tools are resolved from System32 directly (PATH entries can shadow
    /// them with same-named binaries — a hijack surface for an elevated process).</summary>
    private static readonly (string Tool, bool Required, bool IsSystemTool)[] Tools = [
        ("dism.exe", true, true),
        ("reg.exe", true, true),
        ("diskpart.exe", true, true),
        ("robocopy.exe", true, true),
        ("pwsh.exe", true, false), // PowerShell 7 is installed per-machine, not inbox
        ("oscdimg.exe", false, false), // optional: only needed for bootable ISO output
    ];

    private const long MinimumFreeBytes = 50L * 1024 * 1024 * 1024;
    private const double BytesInGb = 1024.0 * 1024 * 1024;

    public static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static IReadOnlyList<CheckResult> Check(string? outputDirectoryHint = null) {
        var elevated = IsAdministrator();
        var results = new List<CheckResult> {
            new("administrator", elevated, true, elevated ? "running elevated" : "must run as administrator"),
        };
        foreach (var tool in Tools) {
            var path = tool.IsSystemTool && File.Exists(Path.Combine(Environment.SystemDirectory, tool.Tool))
                ? Path.Combine(Environment.SystemDirectory, tool.Tool)
                : ToolLocator.Locate(tool.Tool);
            results.Add(new CheckResult(tool.Tool, path is not null, tool.Required,
                path ?? "not found on PATH or System32"));
        }

        if (outputDirectoryHint is not null) {
            results.Add(CheckFreeSpace(outputDirectoryHint, MinimumFreeBytes));
        }

        return results;
    }

    public static CheckResult CheckFreeSpace(string pathHint, long minimumBytes) {
        try {
            var full = Path.GetFullPath(pathHint);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) {
                // The whole pipeline (diskpart VHDX attach, dism apply) needs a local disk.
                return new CheckResult("free-space", false, true,
                    $"'{pathHint}' is a network path; VHDX layers require a local disk");
            }

            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            var free = drive.AvailableFreeSpace;
            return new(
                "free-space",
                free >= minimumBytes,
                true,
                $"{drive.Name} {free / BytesInGb:F1} GB free (need {minimumBytes / BytesInGb:F0} GB)");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) {
            return new CheckResult("free-space", false, true, $"cannot inspect '{pathHint}': {ex.Message}");
        }
    }
}
