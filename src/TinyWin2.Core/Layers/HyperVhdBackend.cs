
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Layers;

/// <summary>
/// Hyper-V VHD cmdlets backend (via pwsh). This is the same machinery Hyper-V uses for
/// its own differencing checkpoints, with correct parent-locator handling for deep
/// chains — the preferred backend wherever the Hyper-V module is available.
/// </summary>
public sealed partial class HyperVhdBackend(IProcessRunner runner) : ILayerBackend {
    public async Task CreateBaseAsync(string vhdxPath, long maximumMb, string volumeLabel, CancellationToken ct) {
        Directory.CreateDirectory(Path.GetDirectoryName(vhdxPath)!);
        var ps = $"""
                  $ErrorActionPreference = 'Stop'
                  $vhd = New-VHD -Path '{vhdxPath}' -SizeBytes {maximumMb}MB -Dynamic
                  Mount-VHD -Path $vhd.Path -PassThru |
                      Initialize-Disk -PartitionStyle MBR -PassThru |
                      New-Partition -UseMaximumSize -AssignDriveLetter:$false -DriveLetter X |
                      Format-Volume -FileSystem NTFS -NewFileSystemLabel '{volumeLabel}' -Confirm:$false | Out-Null
                  Dismount-VHD -Path $vhd.Path
                  """;
        await RunPsAsync(ps, ct);
    }

    public Task CreateDiffAsync(string diffPath, string parentPath, CancellationToken ct) {
        if (!File.Exists(parentPath)) {
            throw new FileNotFoundException($"differencing parent not found: {parentPath}");
        }

        var ps =
            $"$ErrorActionPreference = 'Stop'\nNew-VHD -Path '{diffPath}' -ParentPath '{parentPath}' -Differencing | Out-Null";
        return RunPsAsync(ps, ct);
    }

    public async Task<char> AttachAsync(string vhdxPath, CancellationToken ct) {
        if (!File.Exists(vhdxPath)) {
            throw new FileNotFoundException($"layer VHDX not found: {vhdxPath}");
        }

        // Brace-free PowerShell (raw interpolated strings and script blocks do not mix well).
        var ps = string.Join('\n',
            "$ErrorActionPreference = 'Stop'",
            $"if ((Get-VHD -Path '{vhdxPath}').Attached) {{ Dismount-VHD -Path '{vhdxPath}' }}",
            $"(Mount-VHD -Path '{vhdxPath}' -PassThru | Get-Partition | Get-Volume | Where-Object DriveLetter | Select-Object -First 1).DriveLetter");
        var output = await RunPsAsync(ps, ct);
        var letter = output.Trim().LastOrDefault(char.IsLetter);
        if (letter == default) {
            throw new IOException($"mounted '{vhdxPath}' but could not resolve its drive letter.");
        }

        return letter;
    }

    public Task DetachAsync(string vhdxPath, CancellationToken ct) =>
        RunPsAsync($"$ErrorActionPreference = 'Stop'\nDismount-VHD -Path '{vhdxPath}'", ct);

    public Task MergeAsync(string vhdxPath, int depth, CancellationToken ct) =>
        RunPsAsync($"$ErrorActionPreference = 'Stop'\nMerge-VHD -Path '{vhdxPath}' -Depth {depth}", ct);

    public static bool IsAvailable() {
        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        var probe = new ProcessRunner();
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

    private async Task<string> RunPsAsync(string script, CancellationToken ct) {
        var result = await runner.RunAsync("pwsh.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(15) }, ct);
        return result.Output + result.Error;
    }
}
