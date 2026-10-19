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
    ///     Materializes files that the 24H2+ chunked WIM format ("__chunk_data" media) stores
    ///     out-of-band. Older ADK/system DISM versions apply such images without writing those
    ///     file payloads (they surface as missing/empty files), which silently strips the OOBE
    ///     UI stack (CloudExperienceHost web assets, parts of System32\oobe) and breaks every
    ///     real-setup installation with "Windows 无法完成安装" OOBE fast-fail loops.
    ///     Detection: the CloudExperienceHost AppxManifest.xml sentinel missing from the mount.
    ///     Recovery: 7-Zip reads chunked WIMs correctly - list the image, diff against the
    ///     mounted tree, extract the missing set, and move it into place.
    /// </summary>
    public static async Task ReconcileChunkedFilesAsync(
        IProcessRunner runner, string stagingWim, string applyDir, string workDir, int stagedIndex,
        Action<string> info, Action<string> warn, CancellationToken ct) {
        var sentinel = Path.Combine(applyDir,
            "Windows", "SystemApps", "MicrosoftWindows.CloudExperienceHost_cw5n1h2txyewy", "AppxManifest.xml");
        if (File.Exists(sentinel)) {
            info("chunk reconciliation: sentinel present, nothing to do");
            return;
        }

        var sevenZip = ResolveSevenZip();
        info("chunk reconciliation: OOBE sentinel missing - reconciling chunk-stored files with 7-Zip");

        // 1. list the staged WIM (one line per entry; the image index prefixes each path)
        var listOut = Path.Combine(workDir, "chunk-list.txt");
        var listRes = await runner.RunAsync(sevenZip,
            ["l", "-ba", stagingWim],
            new() { Timeout = TimeSpan.FromMinutes(20), MaxOutputCharacters = 256 * 1024 * 1024 },
            ct);
        await File.WriteAllTextAsync(listOut, listRes.Output, ct);

        // 2. diff: file entries of the selected index that are absent or empty on the mount.
        //    7z -ba WIM lines: "date time attr<5> size<right-aligned> <index>\<path...>" -
        //    the separator inside WIM entry names is the backslash.
        var prefix = $@"{stagedIndex}\";
        var missing = new List<string>();
        foreach (var line in File.ReadLines(listOut)) {
            if (line.Length < 26) { continue; }
            if (line[20] == 'D') { continue; } // directories
            var rest = line[25..].TrimStart();
            // consume the one or two numeric size tokens (logical size, packed size)
            long size = 0;
            for (var t = 0; t < 2; t++) {
                var sp = rest.IndexOf(' ');
                if (sp < 0) { break; }
                var tok = rest[..sp];
                if (tok.Length == 0 || !tok.All(char.IsDigit)) { break; }
                if (t == 0) { long.TryParse(tok, out size); }
                rest = rest[sp..].TrimStart();
            }
            var name = rest.TrimEnd('\r', '\n');
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            var rel = name[prefix.Length..];
            var full = Path.Combine(applyDir, rel);
            if (!File.Exists(full)) {
                missing.Add(name);
            } else if (new FileInfo(full).Length == 0 && size > 0) {
                missing.Add(name);
            }
        }

        if (missing.Count == 0) {
            info("chunk reconciliation: no missing files detected");
            return;
        }
        warn($"chunk reconciliation: {missing.Count} files missing from the applied image - extracting");

        // 3. extract the missing set with a list file (7z exits 1 on warnings - tolerate it)
        var listFile = Path.Combine(workDir, "chunk-missing.txt");
        await File.WriteAllLinesAsync(listFile, missing, ct);
        var tempOut = Path.Combine(workDir, "chunk-extract");
        Directory.CreateDirectory(tempOut);
        try {
            await runner.RunAsync(sevenZip,
                ["x", stagingWim, $"-o{tempOut}", $@"@{listFile}", "-scsUTF-8", "-y"],
                new() { Timeout = TimeSpan.FromMinutes(60), MaxOutputCharacters = 16 * 1024 * 1024 }, ct);
        } catch (ProcessRunnerException e) when (e.Result.ExitCode == 1) {
            warn("chunk reconciliation: extraction completed with warnings");
        }

        // 4. move into the applied image (mount-relative paths under <tempOut>\<index>\)
        var moved = 0;
        var srcRoot = Path.Combine(tempOut, prefix.TrimEnd('\\'));
        foreach (var rel in missing) {
            var relNoPrefix = rel[prefix.Length..];
            var src = Path.Combine(srcRoot, relNoPrefix.Replace('/', '\\'));
            var dst = Path.Combine(applyDir, relNoPrefix.Replace('/', '\\'));
            if (!File.Exists(src)) { continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Move(src, dst, true);
            moved++;
        }
        info($"chunk reconciliation: moved {moved}/{missing.Count} files into the image");
        if (moved < missing.Count) {
            warn($"chunk reconciliation: {missing.Count - moved} files could not be materialized");
        }
        File.Delete(listFile);
        try { Directory.Delete(tempOut, true); } catch { /* best effort */ }
        if (File.Exists(sentinel)) {
            info("chunk reconciliation: OOBE sentinel restored");
        } else {
            warn("chunk reconciliation: OOBE sentinel still missing after restore");
        }
    }

    private static string ResolveSevenZip() {
        foreach (var candidate in new[] {
                     "C:\\Program Files\\7-Zip\\7z.exe",
                     "C:\\Program Files (x86)\\7-Zip\\7z.exe"
                 }) {
            if (File.Exists(candidate)) { return candidate; }
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var candidate = Path.Combine(dir.Trim(), "7z.exe");
            if (File.Exists(candidate)) { return candidate; }
        }
        throw new InvalidOperationException(
            "7-Zip (7z.exe) is required to build from 24H2+ chunked media: install it or add it to PATH.");
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
