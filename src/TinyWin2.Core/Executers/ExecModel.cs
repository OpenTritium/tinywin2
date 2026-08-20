using System.Text.Json.Nodes;
using TinyWin2.Core.Logging;

namespace TinyWin2.Core.Executers;

/// <summary>Desired state of a resource: present (add/modify) or absent (remove).</summary>
public enum Ensure {
    Present,
    Absent,
}

/// <summary>Semantic change kinds recorded per exec.</summary>
public enum ChangeKind {
    Created,
    Modified,
    Removed,
    Skipped,
}

/// <summary>Final outcome status of one exec.</summary>
public enum ExecStatus {
    Applied,
    Skipped,
    Failed,
}

/// <summary>One semantic change an exec made (or skipped) against a named target.</summary>
public sealed record ChangeItem(ChangeKind Kind, string Target, string? Before = null, string? After = null) {
    public JsonObject ToJson() => new() {
        ["kind"] = Kind.ToString().ToLowerInvariant(),
        ["target"] = Target,
        ["before"] = Before,
        ["after"] = After,
    };
}

/// <summary>Result of one exec.</summary>
public sealed record ExecResult(
    ExecStatus Status,
    IReadOnlyList<ChangeItem> Changes,
    string? SkipReason = null,
    int? HResult = null,
    string? Error = null) {
    public static ExecResult Applied(IReadOnlyList<ChangeItem> changes) => new(ExecStatus.Applied, changes);
    public static ExecResult Skipped(string reason, IReadOnlyList<ChangeItem>? changes = null)
        => new(ExecStatus.Skipped, changes ?? [], reason);

    public JsonObject ToJson() => new() {
        ["status"] = Status.ToString().ToLowerInvariant(),
        ["changes"] = new JsonArray(Changes.Select(c => (JsonNode)c.ToJson()).ToArray()),
        ["skipReason"] = SkipReason,
        ["hresult"] = HResult,
        ["error"] = Error,
    };
}

/// <summary>
/// Result of inspecting a resource against an <see cref="ExecSpec"/>: whether the image
/// already satisfies the desired state, and which changes convergence would produce.
/// </summary>
public sealed record ResourceDiff(bool Satisfied, IReadOnlyList<ChangeItem> Differences);

/// <summary>
/// One bound exec: pure data produced at plan-resolution time (after argument binding).
/// <paramref name="Desired"/> is resource-specific and validated against the resource schema.
/// </summary>
public sealed record ExecSpec(string Resource, Ensure Ensure, JsonObject Desired) {
    public JsonObject ToJson() => new() {
        ["resource"] = Resource,
        ["ensure"] = Ensure.ToString().ToLowerInvariant(),
        ["with"] = Desired.DeepClone(),
    };
}

/// <summary>Thrown by executers for hard failures; soft/expected misses become Skipped results.</summary>
public sealed class ExecException(string message, int? hResult = null, Exception? inner = null)
    : Exception(message, inner) {
    public int? HResult2 { get; } = hResult;
}

/// <summary>Per-exec runtime context handed to every executer.</summary>
public sealed class ExecContext(
    string mountPath,
    IBuildLog log,
    int layerIndex,
    bool fastMode) {
    /// <summary>Drive letter root of the currently attached (mounted) image layer.</summary>
    public string MountPath { get; } = mountPath;
    public IBuildLog Log { get; } = log;
    public int LayerIndex { get; } = layerIndex;
    public bool FastMode { get; } = fastMode;
    /// <summary>Offline registry hive sessions for the current layer; owned by the build engine.</summary>
    public Registry.RegistryHiveCache Hives { get; set; } = new(mountPath);
    /// <summary>Root directory of the current plan's bundled assets (for fs.path present copies).</summary>
    public string? PlanAssetsRoot { get; set; }
}
