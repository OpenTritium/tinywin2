using TinyWin2.Core.Native;

namespace TinyWin2.Core.Layers;

/// <summary>
///     diskpart-scripted VHDX backend: zero dependencies. Works for single differencing
///     chains, but its parent-locator handling proved fragile for deep chains on large
///     images — prefer <see cref="HyperVhdBackend" /> when the Hyper-V module exists.
/// </summary>
public sealed class DiskPartVhdBackend(IProcessRunner runner) : ILayerBackend {
    /// <summary>
    ///     diskpart's differencing parent locators proved fragile past ~30 links on
    ///     large images — the stack merges down when the chain outgrows this.
    /// </summary>
    public int MaxSafeChainDepth => 30;

    public async Task CreateBaseAsync(string vhdxPath, long maximumMb, string volumeLabel, CancellationToken ct) {
        vhdxPath = Normalize(vhdxPath);
        Directory.CreateDirectory(Path.GetDirectoryName(vhdxPath)!);
        // Same S..Z preference as AttachAsync: low letters collide with ISO mounts and
        // other transient reservations that Directory.GetLogicalDrives() cannot see.
        var driveLetter = FreeDriveLetters().FirstOrDefault(l => l is >= 'S' and <= 'Z');
        if (driveLetter == '\0') {
            throw new IOException("no free drive letter in S..Z for base volume formatting");
        }

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

    public Task CreateDiffAsync(string diffPath, string parentPath, CancellationToken ct) {
        diffPath = Normalize(diffPath);
        parentPath = Normalize(parentPath);
        if (!File.Exists(parentPath)) {
            throw new FileNotFoundException($"differencing parent not found: {parentPath}");
        }

        var script = $"""
                      create vdisk file="{diffPath}" parent="{parentPath}"

                      """;
        return RunScriptAsync(script, ct);
    }

    public async Task<char> AttachAsync(string vhdxPath, CancellationToken ct) {
        vhdxPath = Normalize(vhdxPath);
        if (!File.Exists(vhdxPath)) {
            throw new FileNotFoundException($"layer VHDX not found: {vhdxPath}");
        }

        var letter = FreeDriveLetters().FirstOrDefault(l => l is >= 'S' and <= 'Z');
        if (letter == '\0') {
            throw new IOException("no free drive letter in S..Z for layer attach");
        }

        await AttachWithRecoveryAsync(vhdxPath, letter, ct);
        return letter;
    }

    public Task DetachAsync(string vhdxPath, CancellationToken ct) {
        vhdxPath = Normalize(vhdxPath);
        return DetachCoreAsync(vhdxPath, false, ct);
    }

    public Task MergeAsync(string vhdxPath, int depth, CancellationToken ct) {
        vhdxPath = Normalize(vhdxPath);
        var script = $"""
                      select vdisk file="{vhdxPath}"
                      merge vdisk depth={depth}

                      """;
        return RunScriptAsync(script, ct);
    }

    /// <summary>
    ///     A crashed previous run can leave a VHDX attached ("virtual disk already attached").
    ///     Detach-first on that condition keeps attach idempotent and self-healing.
    /// </summary>
    private async Task AttachWithRecoveryAsync(string vhdxPath, char driveLetter, CancellationToken ct) {
        try {
            await AttachCoreAsync(vhdxPath, driveLetter, ct);
        }
        catch (ProcessRunnerException ex) when (MentionsAlreadyAttached(ex.Result.Output) ||
                                                MentionsAlreadyAttached(ex.Result.Error)) {
            await DetachCoreAsync(vhdxPath, true, CancellationToken.None);
            await AttachCoreAsync(vhdxPath, driveLetter, ct);
        }
    }

    private static bool MentionsAlreadyAttached(string text) =>
        text.Contains("already attached", StringComparison.OrdinalIgnoreCase)
        || text.Contains("已经连接", StringComparison.Ordinal);

    private Task AttachCoreAsync(string vhdxPath, char driveLetter, CancellationToken ct) {
        var script = $"""
                      select vdisk file="{vhdxPath}"
                      attach vdisk
                      select vdisk file="{vhdxPath}"
                      select partition 1
                      assign letter={driveLetter}

                      """;
        return RunScriptAsync(script, ct);
    }

    /// <summary>Detaching a not-attached disk is success for our purposes (idempotent cleanup).</summary>
    private async Task DetachCoreAsync(string vhdxPath, bool swallowErrors, CancellationToken ct) {
        var script = $"""
                      select vdisk file="{vhdxPath}"
                      detach vdisk

                      """;
        try {
            await RunScriptAsync(script, ct);
        }
        catch (ProcessRunnerException) when (swallowErrors) {
            // Not attached (or busy): best effort before re-attach.
        }
    }

    /// <summary>diskpart rejects forward slashes; normalize every path before scripting.</summary>
    private static string Normalize(string path) => Path.GetFullPath(path);

    private async Task RunScriptAsync(string script, CancellationToken ct) {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"tinywin2-diskpart-{Guid.NewGuid():N}.txt");
        try {
            await File.WriteAllTextAsync(scriptPath, script, ct);
            var result = await runner.RunAsync("diskpart.exe", ["/s", scriptPath],
                new() { Timeout = TimeSpan.FromMinutes(10) }, ct);
            if (result.Output.Contains("encountered an error", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("encountered an error", StringComparison.OrdinalIgnoreCase)) {
                throw new IOException("diskpart reported an error running the script.");
            }
        }
        finally {
            try {
                File.Delete(scriptPath);
            }
            catch {
                /* best effort */
            }
        }
    }

    private static IEnumerable<char> FreeDriveLetters() {
        var used = Directory.GetLogicalDrives()
            .Select(d => char.ToUpperInvariant(d[0]))
            .ToHashSet();
        for (var letter = 'D'; letter <= 'Z'; letter++) {
            if (!used.Contains(letter)) {
                yield return letter;
            }
        }
    }
}
