using TinyWin2.Core.Native;

namespace TinyWin2.Core.Layers;

/// <summary>
/// Hyper-V VHD cmdlets backend (via pwsh). This is the same machinery Hyper-V uses for
/// its own differencing checkpoints, with correct parent-locator handling for deep
/// chains — the preferred backend wherever the Hyper-V module is available.
/// </summary>
public sealed class HyperVhdBackend(IProcessRunner runner) : ILayerBackend {
    private readonly Lock _attachmentGate = new();
    private readonly HashSet<string> _ownedAttachments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This is the same machinery Hyper-V uses for its own checkpoint chains:
    /// arbitrarily deep differencing chains are native, so merging is never forced mid-build.</summary>
    public int MaxSafeChainDepth => int.MaxValue;

    public async Task CreateBaseAsync(string vhdxPath, long maximumMb, string volumeLabel, CancellationToken ct) {
        Directory.CreateDirectory(Path.GetDirectoryName(vhdxPath)!);
        var ps = $"""
                  $ErrorActionPreference = 'Stop'
                  $vhd = New-VHD -Path {PsQuote(vhdxPath)} -SizeBytes {maximumMb}MB -Dynamic
                  Mount-VHD -Path $vhd.Path -PassThru |
                      Initialize-Disk -PartitionStyle MBR -PassThru |
                      New-Partition -UseMaximumSize -AssignDriveLetter:$false -DriveLetter X |
                      Format-Volume -FileSystem NTFS -NewFileSystemLabel {PsQuote(volumeLabel)} -Confirm:$false | Out-Null
                  Dismount-VHD -Path $vhd.Path
                  """;
        await RunPsAsync(ps, ct);
    }

    public Task CreateDiffAsync(string diffPath, string parentPath, CancellationToken ct) {
        if (!File.Exists(parentPath)) {
            throw new FileNotFoundException($"differencing parent not found: {parentPath}");
        }

        var ps =
            $"$ErrorActionPreference = 'Stop'\nNew-VHD -Path {PsQuote(diffPath)} -ParentPath {PsQuote(parentPath)} -Differencing | Out-Null";
        return RunPsAsync(ps, ct);
    }

    public async Task<char> AttachAsync(string vhdxPath, CancellationToken ct) {
        if (!File.Exists(vhdxPath)) {
            throw new FileNotFoundException($"layer VHDX not found: {vhdxPath}");
        }

        // Reuse an existing attachment. A build workspace can be inspected or used by
        // another process while the engine is paused; detaching it here would be invasive.
        var ps = string.Join('\n',
            "$ErrorActionPreference = 'Stop'",
            $"$image = Get-DiskImage -ImagePath {PsQuote(vhdxPath)}",
            "$owned = $false",
            $"if (-not $image.Attached) {{ Mount-VHD -Path {PsQuote(vhdxPath)} | Out-Null; $owned = $true; $image = Get-DiskImage -ImagePath {PsQuote(vhdxPath)} }}",
            "$letter = (Get-Disk -Number $image.Number | Get-Partition | Get-Volume | Where-Object DriveLetter | Select-Object -First 1).DriveLetter",
            "if ($owned) { \"tinywin2-owned|$letter\" } else { \"tinywin2-existing|$letter\" }");
        var output = await RunPsAsync(ps, ct);
        var marker = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith("tinywin2-", StringComparison.OrdinalIgnoreCase));
        var owned = marker?.StartsWith("tinywin2-owned|", StringComparison.OrdinalIgnoreCase) == true;
        var markerSeparator = marker?.IndexOf('|') ?? -1;
        var letterText = markerSeparator >= 0 ? marker![(markerSeparator + 1)..] : output.Trim();
        var letter = letterText.Trim().LastOrDefault(char.IsLetter);
        if (letter == '\0') {
            if (!owned) {
                throw new IOException($"mounted '{vhdxPath}' but could not resolve its drive letter.");
            }

            try {
                await RunPsAsync(
                    $"$ErrorActionPreference = 'SilentlyContinue'\nDismount-VHD -Path {PsQuote(vhdxPath)}",
                    CancellationToken.None);
            }
            catch {
                /* best effort recovery after an owned attach with no volume */
            }

            throw new IOException($"mounted '{vhdxPath}' but could not resolve its drive letter.");
        }

        if (!owned) {
            return letter;
        }

        lock (_attachmentGate) {
            _ownedAttachments.Add(Path.GetFullPath(vhdxPath));
        }

        return letter;
    }

    public Task DetachAsync(string vhdxPath, CancellationToken ct) {
        var path = Path.GetFullPath(vhdxPath);
        lock (_attachmentGate) {
            if (!_ownedAttachments.Contains(path)) {
                return Task.CompletedTask;
            }
        }

        return DetachOwnedAsync(path, ct);
    }

    private async Task DetachOwnedAsync(string path, CancellationToken ct) {
        await RunPsAsync($"$ErrorActionPreference = 'Stop'\nDismount-VHD -Path {PsQuote(path)}", ct);
        lock (_attachmentGate) {
            _ownedAttachments.Remove(path);
        }
    }

    public Task MergeAsync(string vhdxPath, int depth, CancellationToken ct) {
        if (depth <= 0) {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "merge depth must be positive");
        }

        // Merge-VHD has no -Depth parameter. Merge the leaf into each parent explicitly,
        // from the leaf towards the base, which is the supported Hyper-V operation.
        var ps = string.Join('\n',
            "$ErrorActionPreference = 'Stop'",
            $"$current = {PsQuote(vhdxPath)}",
            $"for ($i = 0; $i -lt {depth}; $i++) {{",
            "    $parent = (Get-VHD -Path $current -ErrorAction Stop).ParentPath",
            "    if ([string]::IsNullOrWhiteSpace($parent)) { throw \"VHD chain ended before the requested merge depth.\" }",
            "    Merge-VHD -Path $current -DestinationPath $parent -ErrorAction Stop",
            "    $current = $parent",
            "}");
        return RunPsAsync(ps, ct);
    }

    private static readonly Lock ProbeGate = new();
    private static bool? _available;

    /// <summary>
    /// Probes the Hyper-V module once per process (cached — the pwsh round-trip is slow and
    /// <see cref="LayerBackendFactory"/> asks on every engine wiring). The probe runner is
    /// injectable for tests. Stays sync because the factory contract is sync; the blocking
    /// wait is bounded by the 30s probe timeout and happens at most once.
    /// </summary>
    public static bool IsAvailable(IProcessRunner? probeRunner = null) {
        lock (ProbeGate) {
            _available ??= Probe(probeRunner ?? new ProcessRunner());
            return _available.Value;
        }
    }

    internal static void ResetAvailabilityCache() {
        lock (ProbeGate) {
            _available = null;
        }
    }

    private static bool Probe(IProcessRunner probe) {
        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            var result = probe.RunAsync("pwsh.exe",
                [
                    "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    "[bool](Get-Command New-VHD -ErrorAction SilentlyContinue)"
                ],
                new ProcessRunOptions { IgnoreExitCode = true, Timeout = TimeSpan.FromSeconds(30) },
                CancellationToken.None).GetAwaiter().GetResult();
            return result.Success && result.Output.Trim().EndsWith("True", StringComparison.OrdinalIgnoreCase);
        }
        catch {
            return false;
        }
    }

    /// <summary>PowerShell single-quoted literal — apostrophes in paths are doubled.</summary>
    private static string PsQuote(string value) => $"'{value.Replace("'", "''")}'";

    private async Task<string> RunPsAsync(string script, CancellationToken ct) {
        var result = await runner.RunAsync("pwsh.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(15) }, ct);
        return result.Output + result.Error;
    }
}
