namespace TinyWin2.Core.Layers;

/// <summary>
/// Storage backend for the overlay chain: one base VHDX plus a chain of differencing
/// VHDX layers. The diskpart implementation needs no Hyper-V module; a fake powers tests.
/// </summary>
public interface ILayerBackend
{
    /// <summary>Creates a fresh expandable VHDX with one formatted MBR partition (unattached).</summary>
    Task CreateBaseAsync(string vhdxPath, long maximumMb, string volumeLabel, CancellationToken ct);

    /// <summary>Creates a differencing VHDX whose parent is the current chain leaf (unattached).</summary>
    Task CreateDiffAsync(string diffPath, string parentPath, CancellationToken ct);

    /// <summary>Attaches a VHDX and assigns the drive letter (partition 1).</summary>
    Task AttachAsync(string vhdxPath, string driveLetter, CancellationToken ct);

    Task DetachAsync(string vhdxPath, CancellationToken ct);

    /// <summary>Merges the selected disk and depth-1 ancestors into its parent (offline).</summary>
    Task MergeAsync(string vhdxPath, int depth, CancellationToken ct);
}
