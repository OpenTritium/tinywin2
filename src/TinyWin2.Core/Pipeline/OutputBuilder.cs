using TinyWin2.Core.Hashing;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Pipeline;

/// <summary>Captures the final (or rolled-back) layer into the selected WIM/ESD output.</summary>
public sealed class OutputBuilder(IProcessRunner runner, BuildLog log) {
    /// <summary>Captures a mounted layer directory into a WIM or ESD.</summary>
    public Task CaptureAsync(
        string mountPath,
        string targetPath,
        string imageName,
        string? description,
        OutputFormat format,
        bool fast,
        CancellationToken ct) {
        var compress = format switch {
            OutputFormat.Wim => fast ? "fast" : "max",
            OutputFormat.Esd => "recovery",
            _ => throw new ArgumentException("VHDX output cannot be captured with DISM.", nameof(format)),
        };
        return CaptureAsync(mountPath, targetPath, imageName, description, compress, verify: !fast, ct);
    }

    /// <summary>
    /// Staging capture with explicit compression: the ESD pipeline captures an UNCOMPRESSED
    /// intermediate WIM and compresses exactly once in the export step — compressing the
    /// intermediate and then re-compressing to recovery doubles the work for nothing.
    /// </summary>
    public async Task CaptureAsync(
        string mountPath,
        string targetPath,
        string imageName,
        string? description,
        string compress,
        bool verify,
        CancellationToken ct) {
        var args = new List<string> {
            "/English",
            "/Capture-Image",
            $"/ImageFile:{targetPath}",
            $"/CaptureDir:{mountPath}",
            $"/Name:{imageName}",
        };
        if (!string.IsNullOrEmpty(description)) {
            args.Add($"/Description:{description}");
        }

        args.Add($"/Compress:{compress}");
        if (verify) {
            args.Add("/Verify");
        }

        log.Info($"capturing {mountPath} → {Path.GetFileName(targetPath)} (compress={compress})");
        await runner.RunAsync("dism.exe", args,
            new ProcessRunOptions { Timeout = TimeSpan.FromHours(3) }, ct);
    }

    /// <summary>Copies the source media tree into the output folder, replacing install.* with the build result.</summary>
    public async Task<string> RebuildMediaAsync(
        string sourceRoot,
        string mediaOutputPath,
        string capturedInstallImage,
        OutputFormat format,
        CancellationToken ct) {
        var finalName = format switch {
            OutputFormat.Wim => "install.wim",
            OutputFormat.Esd => "install.esd",
            _ => throw new ArgumentException("VHDX output cannot be placed in installation media.", nameof(format)),
        };
        Directory.CreateDirectory(mediaOutputPath);
        // robocopy: 0-7 are success codes (1 = files copied).
        var result = await runner.RunAsync("robocopy.exe",
            [
                sourceRoot, mediaOutputPath, "/E", "/MT:16", "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS",
                "/XF", "install.wim", "install.esd"
            ],
            new ProcessRunOptions { IgnoreExitCode = true }, ct);
        if (result.ExitCode >= 8) {
            throw new IOException($"robocopy failed copying media (exit {result.ExitCode}).");
        }

        var sourcesDir = Path.Combine(mediaOutputPath, "sources");
        Directory.CreateDirectory(sourcesDir);
        foreach (var stale in new[] { "install.wim", "install.esd", "install.staging.wim" }) {
            var stalePath = Path.Combine(sourcesDir, stale);
            if (File.Exists(stalePath)) {
                File.Delete(stalePath);
            }
        }

        var finalPath = Path.Combine(sourcesDir, finalName);
        File.Move(capturedInstallImage, finalPath);
        log.Info($"media folder rebuilt at {mediaOutputPath}");
        return finalPath;
    }

    /// <summary>Creates a dual BIOS+UEFI bootable ISO with the standard oscdimg flags.</summary>
    public async Task CreateIsoAsync(
        string mediaPath,
        string isoPath,
        string oscdimgPath,
        CancellationToken ct) {
        var bootFolder = Path.Combine(mediaPath, "boot");
        var biosBoot = Path.Combine(bootFolder, "etfsboot.com");
        var efiBootNoPrompt = Path.Combine(mediaPath, "efi", "microsoft", "boot", "efisys_noprompt.bin");
        var efiBoot = Path.Combine(mediaPath, "efi", "microsoft", "boot", "efisys.bin");
        if (!File.Exists(biosBoot) || (!File.Exists(efiBootNoPrompt) && !File.Exists(efiBoot))) {
            throw new FileNotFoundException("boot files (etfsboot.com / efisys*.bin) missing from media folder.");
        }

        var efisys = File.Exists(efiBootNoPrompt) ? efiBootNoPrompt : efiBoot;
        var bootData = $"2#p0,e,b{biosBoot}#pEF,e,b{efisys}";
        log.Info($"creating bootable ISO {isoPath}");
        await runner.RunAsync(oscdimgPath,
            ["-m", "-o", "-u2", "-udfver102", $"-bootdata:{bootData}", mediaPath, isoPath],
            new ProcessRunOptions { Timeout = TimeSpan.FromHours(1) }, ct);
        EnsureNonEmptyFile(isoPath, "oscdimg reported success but did not create a non-empty ISO");
    }

    /// <summary>ESD (LZMS) output goes through an intermediate WIM export for reliability.</summary>
    public Task ExportEsdAsync(string intermediateWim, string esdPath, CancellationToken ct) =>
        runner.RunAsync("dism.exe",
            [
                "/English", "/Export-Image", $"/SourceImageFile:{intermediateWim}", "/SourceIndex:1",
                $"/DestinationImageFile:{esdPath}", "/Compress:recovery"
            ],
            new ProcessRunOptions { Timeout = TimeSpan.FromHours(3) }, ct);

    public static Task<string> ComputeHashAsync(string filePath, CancellationToken ct) {
        EnsureNonEmptyFile(filePath, "cannot hash a missing or empty artifact");
        return Fingerprinting.ComputeFileAsync(filePath, ct);
    }

    private static void EnsureNonEmptyFile(string path, string message) {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) {
            throw new IOException($"{message}: '{path}'.");
        }
    }
}
