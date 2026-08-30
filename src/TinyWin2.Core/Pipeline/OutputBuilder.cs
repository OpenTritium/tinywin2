using TinyWin2.Core.Hashing;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Pipeline;

/// <summary>Captures the final (or rolled-back) layer into the selected WIM/ESD output.</summary>
public sealed class OutputBuilder(IProcessRunner runner, BuildLog log) {
    /// <summary>Applies a staged WIM index into a mounted target directory (the base-layer step).</summary>
    public static async Task ApplyImageAsync(
        IProcessRunner runner, string stagingWim, int imageIndex, string applyDir, CancellationToken ct) {
        await runner.RunAsync("dism.exe",
            [
                "/English", "/Apply-Image", $"/ImageFile:{stagingWim}", $"/Index:{imageIndex}",
                $"/ApplyDir:{applyDir}"
            ],
            new() { Timeout = TimeSpan.FromHours(2) }, ct);
    }

    /// <summary>
    ///     Captures a mounted layer as the selected WIM or ESD. ESD output stages an
    ///     uncompressed intermediate WIM first so recovery compression runs exactly once;
    ///     the intermediate file is always deleted before returning.
    /// </summary>
    public async Task<string> CaptureInstallImageAsync(
        string mountPath,
        string imageName,
        string? description,
        string intermediateWimPath,
        string destinationPath,
        OutputFormat format,
        ImageExportOptions export,
        CancellationToken ct) {
        try {
            if (format == OutputFormat.Wim) {
                await CaptureWimAsync(mountPath, destinationPath, imageName, description,
                    export.Compression, export.VerifyCapture, export.CheckIntegrity, ct);
                return destinationPath;
            }

            // Uncompressed staging + single compression in the export below avoids re-encoding twice.
            await CaptureWimAsync(mountPath, intermediateWimPath, imageName, description,
                WimCompression.None, verify: false, export.CheckIntegrity, ct);
            await ExportEsdAsync(intermediateWimPath, destinationPath, export.CheckIntegrity, ct);
            return destinationPath;
        }
        finally {
            try {
                File.Delete(intermediateWimPath);
            }
            catch {
                /* best effort */
            }
        }
    }

    /// <summary>Captures a mounted layer directory into a WIM.</summary>
    public async Task CaptureWimAsync(
        string mountPath,
        string targetPath,
        string imageName,
        string? description,
        WimCompression compression,
        bool verify,
        bool checkIntegrity,
        CancellationToken ct) {
        var compress = ImageExportOptions.ToDismCompression(compression);
        var args = new List<string> {
            "/English",
            "/Capture-Image",
            $"/ImageFile:{targetPath}",
            $"/CaptureDir:{mountPath}",
            $"/Name:{imageName}"
        };
        if (!string.IsNullOrEmpty(description)) {
            args.Add($"/Description:{description}");
        }

        args.Add($"/Compress:{compress}");
        if (verify) {
            args.Add("/Verify");
        }

        if (checkIntegrity) {
            args.Add("/CheckIntegrity");
        }

        log.Info($"capturing {mountPath} → {Path.GetFileName(targetPath)} (compress={compress})");
        await runner.RunAsync("dism.exe", args,
            new() { Timeout = TimeSpan.FromHours(3) }, ct);
    }

    /// <summary>Stages a built WIM/ESD into a copy of the source media tree.</summary>
    public async Task<string> StageMediaAsync(
        string sourceRoot,
        string mediaOutputPath,
        string capturedInstallImage,
        OutputFormat format,
        bool overwrite,
        CancellationToken ct) {
        var finalName = format switch {
            OutputFormat.Wim => "install.wim",
            OutputFormat.Esd => "install.esd",
            _ => throw new ArgumentException("VHDX output cannot be placed in installation media.", nameof(format))
        };
        Directory.CreateDirectory(mediaOutputPath);
        // robocopy: 0-7 are success codes (1 = files copied).
        var result = await runner.RunAsync("robocopy.exe",
            [
                sourceRoot, mediaOutputPath, "/E", "/MT:16", "/R:1", "/W:1", "/NFL", "/NDL", "/NJH", "/NJS",
                "/XF", "install.wim", "install.esd"
            ],
            new() { IgnoreExitCode = true }, ct);
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
        File.Copy(capturedInstallImage, finalPath, overwrite);
        log.Info($"media folder rebuilt at {mediaOutputPath}");
        return finalPath;
    }

    /// <summary>Creates a dual BIOS+UEFI bootable ISO with the standard oscdimg flags.</summary>
    public async Task CreateIsoAsync(
        string mediaPath,
        string isoPath,
        string oscdimgPath,
        CancellationToken ct) {
        var (biosBoot, efiBootNoPrompt, efiBoot) = ValidateBootMedia(mediaPath);

        var efisys = File.Exists(efiBootNoPrompt) ? efiBootNoPrompt : efiBoot;
        var bootData = $"2#p0,e,b{biosBoot}#pEF,e,b{efisys}";
        log.Info($"creating bootable ISO {isoPath}");
        await runner.RunAsync(oscdimgPath,
            ["-m", "-o", "-u2", "-udfver102", $"-bootdata:{bootData}", mediaPath, isoPath],
            new() { Timeout = TimeSpan.FromHours(1) }, ct);
        EnsureNonEmptyFile(isoPath, "oscdimg reported success but did not create a non-empty ISO");
    }

    public static (string BiosBoot, string EfiBootNoPrompt, string EfiBoot) ValidateBootMedia(string mediaPath) {
        var bootFolder = Path.Combine(mediaPath, "boot");
        var biosBoot = Path.Combine(bootFolder, "etfsboot.com");
        var efiBootNoPrompt = Path.Combine(mediaPath, "efi", "microsoft", "boot", "efisys_noprompt.bin");
        var efiBoot = Path.Combine(mediaPath, "efi", "microsoft", "boot", "efisys.bin");
        if (!File.Exists(biosBoot) || (!File.Exists(efiBootNoPrompt) && !File.Exists(efiBoot))) {
            throw new FileNotFoundException("boot files (etfsboot.com / efisys*.bin) missing from media folder.");
        }

        return (biosBoot, efiBootNoPrompt, efiBoot);
    }

    /// <summary>ESD output uses one recovery-compression export from an uncompressed intermediate WIM.</summary>
    public async Task ExportEsdAsync(string intermediateWim, string esdPath, bool checkIntegrity,
        CancellationToken ct) {
        var args = new List<string> {
            "/English", "/Export-Image", $"/SourceImageFile:{intermediateWim}", "/SourceIndex:1",
            $"/DestinationImageFile:{esdPath}", "/Compress:recovery"
        };
        if (checkIntegrity) {
            args.Add("/CheckIntegrity");
        }

        await runner.RunAsync("dism.exe", args, new() { Timeout = TimeSpan.FromHours(3) }, ct);
    }

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
