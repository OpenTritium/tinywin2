using TinyWin2.Core.Native;

namespace TinyWin2.Core.Layers;

/// <summary>
/// Hyper-V VHD cmdlets backend (via pwsh). This is the same machinery Hyper-V uses for
/// its own differencing checkpoints, with correct parent-locator handling for deep
/// chains — the preferred backend wherever the Hyper-V module is available.
/// </summary>
public sealed class HyperVhdBackend(IProcessRunner runner) : ILayerBackend {
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

        // Built via string.Join: raw interpolated strings and script blocks do not mix well.
        var ps = string.Join('\n',
            "$ErrorActionPreference = 'Stop'",
            $"if ((Get-VHD -Path {PsQuote(vhdxPath)}).Attached) {{ Dismount-VHD -Path {PsQuote(vhdxPath)} }}",
            $"(Mount-VHD -Path {PsQuote(vhdxPath)} -PassThru | Get-Partition | Get-Volume | Where-Object DriveLetter | Select-Object -First 1).DriveLetter");
        var output = await RunPsAsync(ps, ct);
        var letter = output.Trim().LastOrDefault(char.IsLetter);
        if (letter == '\0') {
            throw new IOException($"mounted '{vhdxPath}' but could not resolve its drive letter.");
        }

        return letter;
    }

    public Task DetachAsync(string vhdxPath, CancellationToken ct) =>
        RunPsAsync($"$ErrorActionPreference = 'Stop'\nDismount-VHD -Path {PsQuote(vhdxPath)}", ct);

    public Task MergeAsync(string vhdxPath, int depth, CancellationToken ct) =>
        RunPsAsync($"$ErrorActionPreference = 'Stop'\nMerge-VHD -Path {PsQuote(vhdxPath)} -Depth {depth}", ct);

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
