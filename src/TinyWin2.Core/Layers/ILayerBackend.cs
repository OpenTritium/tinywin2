namespace TinyWin2.Core.Layers;

/// <summary>
///     Storage backend for the overlay chain: one base VHDX plus a chain of differencing
///     VHDX layers. Attach returns the assigned drive letter. The Hyper-V implementation is
///     preferred (it manages differencing chains exactly like production checkpoints);
///     diskpart remains the zero-dependency fallback.
/// </summary>
public interface ILayerBackend {
    /// <summary>
    ///     Deepest differencing chain this backend attaches reliably. The stack merges mid-build
    ///     only past this safety limit — merging is export-time work (the VHDX artifact path does
    ///     it once), so a backend that handles deep chains natively should never force it.
    ///     diskpart's parent locators are fragile on deep chains; Hyper-V checkpoint machinery is not.
    /// </summary>
    int MaxSafeChainDepth { get; }

    /// <summary>Creates a fresh expandable VHDX with one formatted MBR partition (unattached).</summary>
    Task CreateBaseAsync(string vhdxPath, long maximumMb, string volumeLabel, CancellationToken ct);

    /// <summary>
    ///     Creates a differencing VHDX whose parent is the current chain leaf (unattached).
    ///     The parent MUST be detached — diskpart produces a broken chain otherwise.
    /// </summary>
    Task CreateDiffAsync(string diffPath, string parentPath, CancellationToken ct);

    /// <summary>Attaches a VHDX; returns the drive letter of its first volume.</summary>
    Task<char> AttachAsync(string vhdxPath, CancellationToken ct);

    Task DetachAsync(string vhdxPath, CancellationToken ct);

    /// <summary>Merges the selected disk and depth-1 ancestors into its parent (offline).</summary>
    Task MergeAsync(string vhdxPath, int depth, CancellationToken ct);
}
