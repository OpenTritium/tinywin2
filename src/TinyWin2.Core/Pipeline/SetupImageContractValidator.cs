using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Pipeline;

/// <summary>Checks the files Windows Setup needs from an install image's selected index.</summary>
public sealed class SetupImageContractValidator(IProcessRunner runner, BuildLog log) {
    private static readonly string[] RequiredRecoveryFiles = [
        @"Windows\System32\Recovery\Winre.wim",
        @"Windows\System32\Recovery\ReAgent.xml"
    ];

    public async Task ValidateAsync(string imagePath, int index, CancellationToken ct) {
        var fullImagePath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullImagePath) || new FileInfo(fullImagePath).Length == 0) {
            throw new FileNotFoundException($"install image is missing or empty: '{fullImagePath}'.");
        }

        if (index < 1) {
            throw new ArgumentOutOfRangeException(nameof(index), index,
                "image index must be greater than zero.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"tinywin2-setup-contract-{Guid.NewGuid():N}");
        var mountPath = Path.Combine(root, "mount");
        var wimPath = fullImagePath;
        var mountIndex = index;
        var mounted = false;
        var unmounted = false;
        Directory.CreateDirectory(mountPath);
        try {
            if (fullImagePath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)) {
                wimPath = Path.Combine(root, "install.contract.wim");
                await runner.RunAsync("dism.exe", [
                    "/English", "/Export-Image", $"/SourceImageFile:{fullImagePath}",
                    $"/SourceIndex:{index}", $"/DestinationImageFile:{wimPath}", "/Compress:none"
                ], new() { Timeout = TimeSpan.FromHours(3) }, ct);
                mountIndex = 1;
            }

            await runner.RunAsync("dism.exe", [
                "/English", "/Mount-Wim", $"/WimFile:{wimPath}", $"/Index:{mountIndex}",
                $"/MountDir:{mountPath}", "/ReadOnly"
            ], new() { Timeout = TimeSpan.FromHours(1) }, ct);
            mounted = true;
            ValidateMountedImage(mountPath, fullImagePath, index, log);
            log.Info($"Setup image contract passed for {Path.GetFileName(fullImagePath)} index {index}");
        }
        finally {
            if (mounted) {
                try {
                    var result = await runner.RunAsync("dism.exe", [
                            "/English", "/Unmount-Wim", $"/MountDir:{mountPath}", "/Discard"
                        ], new() { IgnoreExitCode = true, Timeout = TimeSpan.FromHours(1) },
                        CancellationToken.None);
                    unmounted = result.Success;
                    if (!unmounted) {
                        log.Warn($"could not unmount Setup contract image '{mountPath}' " +
                                 $"(exit {result.ExitCode}); retaining the mount directory.");
                    }
                }
                catch (Exception ex) {
                    log.Warn($"could not unmount Setup contract image '{mountPath}': {ex.Message}");
                }
            }

            if (!mounted || unmounted) {
                try {
                    Directory.Delete(root, true);
                }
                catch (Exception ex) {
                    log.Warn($"could not clean Setup contract temporary directory '{root}': {ex.Message}");
                }
            }
        }
    }

    internal static void ValidateMountedImage(string mountPath, string imagePath, int index, BuildLog log) {
        var missing = RequiredRecoveryFiles
            .Where(path => !File.Exists(Path.Combine(mountPath, path)))
            .ToArray();
        if (missing.Length == 0) {
            return;
        }

        // A WinRE-less image installs and runs fine; it simply has no recovery environment.
        // Profiles enable fs.recovery-environment deliberately, so this is a heads-up, not a block.
        log.Warn($"setup image contract: '{Path.GetFileName(imagePath)}' index {index} is missing " +
                 $"{string.Join(", ", missing.Select(path => $"'{path}'"))} - the installed system " +
                 "will have no Windows recovery environment (reagentc reports no WinRE).");
    }
}
