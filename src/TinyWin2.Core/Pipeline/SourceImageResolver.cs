using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyWin2.Core.Hashing;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Pipeline;

/// <summary>One selectable index inside the source install image.</summary>
public sealed record ImageIndexInfo(
    int Index,
    string Name,
    string? Description,
    string? Architecture,
    string? Version,
    string? EditionId,
    long SizeBytes);

public enum SourceInputKind {
    Image,
    Media,
    Iso
}

/// <summary>A resolved source: a standalone image, an extracted media tree, or a mounted ISO.</summary>
public sealed class SourceInput {
    public required SourceInputKind Kind { get; init; }
    public required string InputPath { get; init; }
    public bool IsMountedIso { get; init; }
    public string? IsoPath { get; init; }
    public string? MediaRootPath { get; init; }
    public required string InstallImagePath { get; init; }
    public bool IsEsd => InstallImagePath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase);
    public bool HasMediaTree => MediaRootPath is not null;
    public string? BootWimPath {
        get {
            if (MediaRootPath is null) {
                return null;
            }

            var path = Path.Combine(MediaRootPath, "sources", "boot.wim");
            return File.Exists(path) ? path : null;
        }
    }
}

/// <summary>Resolves ISO/folder sources and inspects install-image indexes (read-only).</summary>
public sealed class SourceImageResolver(IProcessRunner runner, BuildLog log) {
    public async Task<SourceInput> ResolveAsync(string inputPath, CancellationToken ct) {
        var fullPath = Path.GetFullPath(inputPath);
        if (Directory.Exists(fullPath)) {
            return new SourceInput {
                Kind = SourceInputKind.Media,
                InputPath = fullPath,
                IsMountedIso = false,
                MediaRootPath = fullPath,
                InstallImagePath = FindInstallImage(fullPath)
            };
        }

        if (File.Exists(fullPath) && fullPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) {
            var driveRoot = await MountIsoAsync(fullPath, ct);
            try {
                return new SourceInput {
                    Kind = SourceInputKind.Iso,
                    InputPath = fullPath,
                    IsMountedIso = true,
                    IsoPath = fullPath,
                    MediaRootPath = driveRoot,
                    InstallImagePath = FindInstallImage(driveRoot)
                };
            }
            catch {
                try {
                    var cleanup = await DismountIsoPathAsync(fullPath, CancellationToken.None);
                    if (!cleanup.Success) {
                        log.Warn(
                            $"could not clean up source ISO after resolution failed '{fullPath}' (exit {cleanup.ExitCode}).");
                    }
                }
                catch (Exception ex) {
                    log.Warn($"could not clean up source ISO after resolution failed '{fullPath}': {ex.Message}");
                }

                throw;
            }
        }

        if (File.Exists(fullPath)
            && (fullPath.EndsWith(".wim", StringComparison.OrdinalIgnoreCase)
                || fullPath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))) {
            return new SourceInput {
                Kind = SourceInputKind.Image,
                InputPath = fullPath,
                InstallImagePath = fullPath
            };
        }

        throw new FileNotFoundException(
            $"input '{inputPath}' must be an ISO, media directory, WIM, or ESD file.");
    }

    public async Task DismountIsoAsync(SourceInput media, CancellationToken ct) {
        if (!media.IsMountedIso) {
            return;
        }

        try {
            var result = await DismountIsoPathAsync(media.IsoPath!, ct);
            if (!result.Success) {
                log.Warn($"could not dismount source ISO '{media.IsoPath}' (exit {result.ExitCode}).");
            }
        }
        catch (Exception ex) {
            log.Warn($"could not dismount source ISO '{media.IsoPath}': {ex.Message}");
        }
    }

    /// <summary>Reads one selected index. Build only needs this query; listing all indexes is for inspect/GUI.</summary>
    public async Task<ImageIndexInfo> GetIndexAsync(string installImagePath, int index, CancellationToken ct) {
        var result = await runner.RunAsync("dism.exe",
            ["/Get-WimInfo", $"/WimFile:{installImagePath}", $"/Index:{index}", "/English"],
            new() { IgnoreExitCode = true }, ct);
        if (!result.Success) {
            var summaries = await GetIndexSummaryAsync(installImagePath, ct);
            return summaries.FirstOrDefault(item => item.Index == index)
                   ?? throw new InvalidOperationException(
                       $"image index {index} not found in '{installImagePath}' " +
                       $"(available: {string.Join(", ", summaries.Select(item => item.Index))}).");
        }

        var fields = ParseKeyValueLines(result.Output);
        return new(
            index,
            fields.GetValueOrDefault("Name", ""),
            fields.GetValueOrDefault("Description"),
            fields.GetValueOrDefault("Architecture"),
            fields.GetValueOrDefault("Version"),
            fields.GetValueOrDefault("Edition ID") ?? fields.GetValueOrDefault("EditionId"),
            ParseByteSize(fields.GetValueOrDefault("Size"), 0));
    }

    public async Task<IReadOnlyList<ImageIndexInfo>> GetIndexesAsync(string installImagePath, CancellationToken ct) {
        var indexes = await GetIndexSummaryAsync(installImagePath, ct);
        // The no-index listing only carries Index/Name/Description; details need per-index queries.
        var detailed = new List<ImageIndexInfo>();
        foreach (var summary in indexes) {
            var result = await runner.RunAsync("dism.exe",
                ["/Get-WimInfo", $"/WimFile:{installImagePath}", $"/Index:{summary.Index}", "/English"],
                new() { IgnoreExitCode = true }, ct);
            if (!result.Success) {
                detailed.Add(summary);
                continue;
            }

            var fields = ParseKeyValueLines(result.Output);
            detailed.Add(new(
                summary.Index,
                fields.GetValueOrDefault("Name", summary.Name),
                fields.GetValueOrDefault("Description", summary.Description ?? ""),
                fields.GetValueOrDefault("Architecture", summary.Architecture ?? ""),
                fields.GetValueOrDefault("Version", summary.Version ?? ""),
                fields.GetValueOrDefault("Edition ID", fields.GetValueOrDefault("EditionId", summary.EditionId ?? "")),
                ParseByteSize(fields.GetValueOrDefault("Size"), summary.SizeBytes)));
        }

        return detailed;
    }

    private async Task<List<ImageIndexInfo>> GetIndexSummaryAsync(string installImagePath, CancellationToken ct) {
        var result = await runner.RunAsync("dism.exe",
            ["/Get-WimInfo", $"/WimFile:{installImagePath}", "/English"],
            new() { IgnoreExitCode = true }, ct);
        if (!result.Success) {
            throw new InvalidOperationException(
                $"dism.exe could not read image info from '{installImagePath}' (exit {result.ExitCode}).");
        }

        var indexes = new List<ImageIndexInfo>();
        ImageIndexInfo? current = null;
        foreach (var rawLine in result.Output.Split('\n')) {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("Index", StringComparison.Ordinal)) {
                if (current is not null) {
                    indexes.Add(current);
                }

                current = new(int.Parse(value), "", null, null, null, null, 0);
                continue;
            }

            if (current is null) {
                continue;
            }

            current = key switch {
                "Name" => current with { Name = value },
                "Description" => current with { Description = value },
                "Size" => current with { SizeBytes = long.TryParse(value.Replace(",", ""), out var size) ? size : 0 },
                _ => current
            };
        }

        if (current is not null) {
            indexes.Add(current);
        }

        return indexes;
    }

    internal static Dictionary<string, string> ParseKeyValueLines(string output) {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n')) {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.Length == 0) {
                continue;
            }

            var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) {
                continue;
            }

            fields[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return fields;
    }

    /// <summary>"11,831,247,965 bytes" → 11831247965; falls back when unparsable.</summary>
    internal static long ParseByteSize(string? sizeText, long fallback) =>
        sizeText is not null
        && Regex.Match(sizeText.Replace(",", ""), @"\d+").Value is { Length: > 0 } digits
        && long.TryParse(digits, out var size)
            ? size
            : fallback;

    /// <summary>Exports one index from an ESD into a WIM (dism cannot apply every ESD directly).</summary>
    public async Task<string> ExportIndexToWimAsync(
        string sourceImagePath,
        int index,
        string targetWimPath,
        WimCompression compression,
        bool checkIntegrity,
        CancellationToken ct) {
        var compress = ImageExportOptions.ToDismCompression(compression);
        var metadataPath = targetWimPath + ".tinywin2.json";
        if (IsReusableExport(sourceImagePath, index, compress, checkIntegrity, targetWimPath, metadataPath)) {
            return targetWimPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetWimPath))!);
        var temporaryTarget = $"{targetWimPath}.{Guid.NewGuid():N}.tmp.wim";
        try {
            var args = new List<string> {
                "/English", "/Export-Image", $"/SourceImageFile:{sourceImagePath}", $"/SourceIndex:{index}",
                $"/DestinationImageFile:{temporaryTarget}", $"/Compress:{compress}"
            };
            if (checkIntegrity) {
                args.Add("/CheckIntegrity");
            }

            await runner.RunAsync("dism.exe", args, cancellationToken: ct);

            if (!File.Exists(temporaryTarget) || new FileInfo(temporaryTarget).Length == 0) {
                throw new IOException($"DISM reported a successful export but did not create '{temporaryTarget}'.");
            }

            File.Move(temporaryTarget, targetWimPath, true);
            await File.WriteAllTextAsync(metadataPath,
                BuildExportMetadata(sourceImagePath, index, compress, checkIntegrity), ct);
        }
        finally {
            try {
                File.Delete(temporaryTarget);
            }
            catch {
                /* best effort */
            }
        }

        return targetWimPath;
    }

    /// <summary>Stages the selected index as a plain WIM: ESD sources are exported, WIM sources copied.</summary>
    public async Task StageAsWimAsync(SourceInput source, int imageIndex, string targetWimPath,
        WimCompression compression, bool checkIntegrity, CancellationToken ct) {
        if (!source.IsEsd) {
            File.Copy(source.InstallImagePath, targetWimPath, true);
            try {
                File.Delete(targetWimPath + ".tinywin2.json");
            }
            catch {
                /* stale metadata is harmless */
            }

            return;
        }

        await ExportIndexToWimAsync(source.InstallImagePath, imageIndex, targetWimPath, compression,
            checkIntegrity, ct);
    }

    private async Task<string> MountIsoAsync(string isoPath, CancellationToken ct) {
        var result = await runner.RunAsync("pwsh.exe",
            [
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                $"$ErrorActionPreference = 'Stop'; (Mount-DiskImage -ImagePath {PsQuote(isoPath)} -PassThru -ErrorAction Stop | Get-Volume).DriveLetter"
            ],
            new() { IgnoreExitCode = true }, ct);
        var letter = result.Output.Trim().LastOrDefault(char.IsLetter);
        if (result.ExitCode != 0 || letter == '\0') {
            throw new IOException($"could not mount ISO '{isoPath}': {result.Output} {result.Error}");
        }

        var root = $"{letter}:\\";
        log.Info($"mounted source ISO at {root}");
        return root;
    }

    public static string ComputeSourceFingerprint(SourceInput media, int imageIndex) {
        var identity = new StringBuilder();
        identity.Append(media.Kind).Append('|').Append(Path.GetFullPath(media.InputPath)).Append('|').Append(imageIndex)
            .Append('|').Append(media.IsEsd);
        if (media.Kind == SourceInputKind.Iso) {
            identity.Append('|').Append(FileStamp(media.IsoPath!));
        }
        else {
            identity.Append('|').Append(FileStamp(media.InstallImagePath));
            if (media.BootWimPath is { } bootWimPath) {
                identity.Append('|').Append(FileStamp(bootWimPath));
            }
        }

        return Fingerprinting.Compute(identity.ToString());
    }

    private async Task<ProcessRunResult> DismountIsoPathAsync(string isoPath, CancellationToken ct) {
        return await runner.RunAsync("pwsh.exe",
            [
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                $"$ErrorActionPreference = 'Stop'; Dismount-DiskImage -ImagePath {PsQuote(isoPath)} -ErrorAction Stop | Out-Null"
            ],
            new() { IgnoreExitCode = true }, ct);
    }

    private static bool IsReusableExport(string sourceImagePath, int index, string compress, bool checkIntegrity,
        string targetWimPath, string metadataPath) {
        if (!File.Exists(targetWimPath) || !File.Exists(metadataPath)
                                        || new FileInfo(targetWimPath).Length == 0) {
            return false;
        }

        try {
            var metadata =
                JsonNode.Parse(File.ReadAllText(metadataPath)) as
                    JsonObject;
            return metadata?["source"]?.GetValue<string>() == Path.GetFullPath(sourceImagePath)
                   && metadata["sourceStamp"]?.GetValue<string>() == FileStamp(sourceImagePath)
                   && metadata["index"]?.GetValue<int>() == index
                   && metadata["compress"]?.GetValue<string>() == compress
                   && metadata["checkIntegrity"]?.GetValue<bool>() == checkIntegrity;
        }
        catch (Exception) {
            return false;
        }
    }

    private static string BuildExportMetadata(string sourceImagePath, int index, string compress,
        bool checkIntegrity) =>
        new JsonObject {
            ["source"] = Path.GetFullPath(sourceImagePath),
            ["sourceStamp"] = FileStamp(sourceImagePath),
            ["index"] = index,
            ["compress"] = compress,
            ["checkIntegrity"] = checkIntegrity
        }.ToJsonString();

    private static string FileStamp(string path) {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) {
            return $"missing:{fullPath}";
        }

        var info = new FileInfo(fullPath);
        return $"{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static string PsQuote(string value) => $"'{value.Replace("'", "''")}'";

    private static string FindInstallImage(string root) {
        var wim = Path.Combine(root, "sources", "install.wim");
        if (File.Exists(wim)) {
            return wim;
        }

        var esd = Path.Combine(root, "sources", "install.esd");
        if (File.Exists(esd)) {
            return esd;
        }

        throw new FileNotFoundException($"no sources\\install.wim or sources\\install.esd under '{root}'.");
    }

}
