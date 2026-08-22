namespace TinyWin2.Core.Pipeline;

public enum WimCompression {
    None,
    Fast,
    Max
}

/// <summary>Controls image capture and export work independently from layer execution speed.</summary>
public sealed record ImageExportOptions {
    public WimCompression Compression { get; init; } = WimCompression.Max;
    public bool VerifyCapture { get; init; } = true;
    public bool CheckIntegrity { get; init; }

    public string DismCompression => ToDismCompression(Compression);

    public static string ToDismCompression(WimCompression compression) => compression switch {
        WimCompression.None => "none",
        WimCompression.Fast => "fast",
        WimCompression.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "unknown WIM compression")
    };
}
