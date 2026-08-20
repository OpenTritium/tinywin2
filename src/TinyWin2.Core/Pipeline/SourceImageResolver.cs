using System.Text.RegularExpressions;
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

/// <summary>A resolved installation media source: folder or mounted ISO.</summary>
public sealed class SourceMedia {
    public required string RootPath { get; init; }
    public bool IsMountedIso { get; init; }
    public required string IsoPath { get; init; }
    public required string InstallImagePath { get; init; }
    public bool IsEsd => InstallImagePath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase);

    public string BootWimPath => Path.Combine(RootPath, "sources", "boot.wim");
}

/// <summary>Resolves ISO/folder sources and inspects install-image indexes (read-only).</summary>
public sealed class SourceImageResolver(IProcessRunner runner, BuildLog log) {
    public async Task<SourceMedia> ResolveAsync(string sourcePath, CancellationToken ct) {
        var fullPath = Path.GetFullPath(sourcePath);
        if (Directory.Exists(fullPath)) {
            var media = new SourceMedia {
                RootPath = fullPath,
                IsMountedIso = false,
                IsoPath = fullPath,
                InstallImagePath = FindInstallImage(fullPath),
            };
            Validate(media);
            return media;
        }
        if (File.Exists(fullPath) && fullPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) {
            var driveRoot = await MountIsoAsync(fullPath, ct);
            var media = new SourceMedia {
                RootPath = driveRoot,
                IsMountedIso = true,
                IsoPath = fullPath,
                InstallImagePath = FindInstallImage(driveRoot),
            };
            Validate(media);
            return media;
        }
        throw new FileNotFoundException($"source '{sourcePath}' is neither a folder nor an .iso file.");
    }

    public async Task DismountIsoAsync(SourceMedia media, CancellationToken ct) {
        if (!media.IsMountedIso) {
            return;
        }
        var result = await runner.RunAsync("pwsh.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", $"Dismount-DiskImage -ImagePath '{media.IsoPath}' | Out-Null; $?"],
            new ProcessRunOptions { IgnoreExitCode = true }, ct);
        if (!result.Success) {
            log.Warn($"could not dismount source ISO '{media.IsoPath}' (exit {result.ExitCode}).");
        }
    }

    public async Task<IReadOnlyList<ImageIndexInfo>> GetIndexesAsync(string installImagePath, CancellationToken ct) {
        var indexes = await GetIndexSummaryAsync(installImagePath, ct);
        // The no-index listing only carries Index/Name/Description; details need per-index queries.
        var detailed = new List<ImageIndexInfo>();
        foreach (var summary in indexes) {
            var result = await runner.RunAsync("dism.exe",
                ["/Get-WimInfo", $"/WimFile:{installImagePath}", $"/Index:{summary.Index}", "/English"],
                new ProcessRunOptions { IgnoreExitCode = true }, ct);
            if (!result.Success) {
                detailed.Add(summary);
                continue;
            }
            var fields = ParseKeyValueLines(result.Output);
            detailed.Add(new ImageIndexInfo(
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
            new ProcessRunOptions { IgnoreExitCode = true }, ct);
        if (!result.Success) {
            throw new InvalidOperationException($"dism.exe could not read image info from '{installImagePath}' (exit {result.ExitCode}).");
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
                current = new ImageIndexInfo(int.Parse(value), "", null, null, null, null, 0);
                continue;
            }
            if (current is null) {
                continue;
            }
            current = key switch {
                "Name" => current with { Name = value },
                "Description" => current with { Description = value },
                "Size" => current with { SizeBytes = long.TryParse(value.Replace(",", ""), out var size) ? size : 0 },
                _ => current,
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
        bool fast,
        CancellationToken ct) {
        if (File.Exists(targetWimPath)) {
            return targetWimPath;
        }
        var compress = fast ? "fast" : "max";
        await runner.RunAsync("dism.exe",
            ["/English", "/Export-Image", $"/SourceImageFile:{sourceImagePath}", $"/SourceIndex:{index}",
             $"/DestinationImageFile:{targetWimPath}", $"/Compress:{compress}"], cancellationToken: ct);
        return targetWimPath;
    }

    /// <summary>Stages the selected index as a plain WIM: ESD sources are exported, WIM sources copied.</summary>
    public async Task StageAsWimAsync(SourceMedia source, int imageIndex, string targetWimPath, bool fast,
        CancellationToken ct) {
        if (!source.IsEsd) {
            File.Copy(source.InstallImagePath, targetWimPath, overwrite: true);
            return;
        }
        await ExportIndexToWimAsync(source.InstallImagePath, imageIndex, targetWimPath, fast, ct);
    }

    private async Task<string> MountIsoAsync(string isoPath, CancellationToken ct) {
        var result = await runner.RunAsync("pwsh.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
             $"(Mount-DiskImage -ImagePath '{isoPath}' -PassThru | Get-Volume).DriveLetter"],
            new ProcessRunOptions { IgnoreExitCode = true }, ct);
        var letter = result.Output.Trim().LastOrDefault(char.IsLetter);
        if (result.ExitCode != 0 || letter == '\0') {
            throw new IOException($"could not mount ISO '{isoPath}': {result.Output} {result.Error}");
        }
        var root = $"{letter}:\\";
        log.Info($"mounted source ISO at {root}");
        return root;
    }

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

    private void Validate(SourceMedia media) {
        if (!File.Exists(media.BootWimPath)) {
            throw new FileNotFoundException($"sources\\boot.wim missing under '{media.RootPath}' — not a bootable media folder.");
        }
    }
}
