namespace TinyWin2.Cli;

/// <summary>The stable process exit code contract: documented in AGENTS.md, consumed by scripts.</summary>
internal static class ExitCodes {
    public const int Success = 0;
    public const int Failure = 1;
    public const int Canceled = 130;
    public const int UnsupportedPlatform = 3;
}
