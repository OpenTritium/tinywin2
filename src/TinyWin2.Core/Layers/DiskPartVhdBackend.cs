using TinyWin2.Core.Native;

namespace TinyWin2.Core.Layers;

/// <summary>
/// diskpart-scripted VHDX backend. Every operation writes a small script file and runs
/// <c>diskpart.exe /s</c>; scripts use only commands present on any Windows 10+ host.
/// </summary>
public sealed class DiskPartVhdBackend(IProcessRunner runner) : ILayerBackend
{
    public async Task CreateBaseAsync(string vhdxPath, long maximumMb, string volumeLabel, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(vhdxPath)!);
        var driveLetter = FreeDriveLetters().First();
        var script = $"""
            create vdisk file="{vhdxPath}" maximum={maximumMb} type=expandable
            select vdisk file="{vhdxPath}"
            attach vdisk
            convert mbr
            create partition primary
            format fs=ntfs label="{volumeLabel}" quick
            assign letter={driveLetter}
            select vdisk file="{vhdxPath}"
            detach vdisk

            """;
        await RunScriptAsync(script, ct);
    }

    public Task CreateDiffAsync(string diffPath, string parentPath, CancellationToken ct)
    {
        if (!File.Exists(parentPath))
        {
            throw new FileNotFoundException($"differencing parent not found: {parentPath}");
        }
        var script = $"""
            create vdisk file="{diffPath}" parent="{parentPath}"

            """;
        return RunScriptAsync(script, ct);
    }

    public Task AttachAsync(string vhdxPath, string driveLetter, CancellationToken ct)
    {
        if (!File.Exists(vhdxPath))
        {
            throw new FileNotFoundException($"layer VHDX not found: {vhdxPath}");
        }
        if (new DriveInfo(driveLetter + @":\").IsReady)
        {
            throw new IOException($"drive letter {driveLetter}: is already in use.");
        }
        var script = $"""
            select vdisk file="{vhdxPath}"
            attach vdisk
            select vdisk file="{vhdxPath}"
            select partition 1
            assign letter={driveLetter}

            """;
        return RunScriptAsync(script, ct);
    }

    public Task DetachAsync(string vhdxPath, CancellationToken ct)
    {
        var script = $"""
            select vdisk file="{vhdxPath}"
            detach vdisk

            """;
        return RunScriptAsync(script, ct);
    }

    public Task MergeAsync(string vhdxPath, int depth, CancellationToken ct)
    {
        var script = $"""
            select vdisk file="{vhdxPath}"
            merge vdisk depth={depth}

            """;
        return RunScriptAsync(script, ct);
    }

    private async Task RunScriptAsync(string script, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"tinywin2-diskpart-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script, ct);
            var result = await runner.RunAsync("diskpart.exe", ["/s", scriptPath],
                new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(10) }, ct);
            if (result.Output.Contains("encountered an error", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("encountered an error", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("diskpart reported an error running the script.");
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }
    }

    internal static IEnumerable<char> FreeDriveLetters()
    {
        var used = Directory.GetLogicalDrives()
            .Select(d => char.ToUpperInvariant(d[0]))
            .ToHashSet();
        for (var letter = 'D'; letter <= 'Z'; letter++)
        {
            if (!used.Contains(letter))
            {
                yield return letter;
            }
        }
    }
}
