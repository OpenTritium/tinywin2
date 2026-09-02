using TinyWin2.Core.Logging;

namespace TinyWin2.Core.Layers;

/// <summary>
///     The one attach → operate → detach scope used everywhere a layer VHDX is mounted.
///     Centralizing it keeps the detach contract from diverging per call site: a detach
///     failure is always logged (never silently swallowed) because a surviving attachment
///     poisons the chain — the next diff creation would fail on a still-attached parent.
/// </summary>
public static class MountScope {
    public static async Task<T> RunAsync<T>(
        ILayerBackend backend,
        string vhdxPath,
        BuildLog log,
        string operationName,
        Func<string, Task<T>> operation,
        CancellationToken ct) {
        var letter = await backend.AttachAsync(vhdxPath, ct);
        try {
            return await operation($"{letter}:\\");
        }
        finally {
            await DetachLoggingFailureAsync(backend, vhdxPath, log, operationName);
        }
    }

    public static Task RunAsync(
        ILayerBackend backend,
        string vhdxPath,
        BuildLog log,
        string operationName,
        Func<string, Task> operation,
        CancellationToken ct) =>
        RunAsync<object?>(backend, vhdxPath, log, operationName, async mountPath => {
            await operation(mountPath);
            return null;
        }, ct);

    private static async Task DetachLoggingFailureAsync(
        ILayerBackend backend, string vhdxPath, BuildLog log, string operationName) {
        try {
            await backend.DetachAsync(vhdxPath, CancellationToken.None);
        }
        catch (Exception cleanupError) {
            log.Error($"detach failed after {operationName}: {cleanupError.Message}");
        }
    }
}
