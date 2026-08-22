using System.Text.Json.Nodes;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Logging;

namespace TinyWin2.Core.Executers;

/// <summary>Semantic action requested from a resource executor.</summary>
public enum OperationAction {
    Configure,
    Set,
    Remove,
    Cleanup,
    Copy
}

/// <summary>Semantic change kinds recorded per operation.</summary>
public enum ChangeKind {
    Created,
    Modified,
    Removed,
    Skipped
}

/// <summary>
///     Final outcome status of one operation. Failures are exceptions
///     (<see cref="ExecException" />), never a status value.
/// </summary>
public enum ExecStatus {
    Applied,
    Skipped
}

/// <summary>One semantic change an operation made (or skipped) against a named target.</summary>
public sealed record ChangeItem(ChangeKind Kind, string Target, string? Before = null, string? After = null) {
    public JsonObject ToJson() => new() {
        ["kind"] = Kind.ToString().ToLowerInvariant(),
        ["target"] = Target,
        ["before"] = Before,
        ["after"] = After
    };
}

/// <summary>Result of one operation. Hard failures throw <see cref="ExecException" /> instead.</summary>
/// <param name="Changes">What changed; may carry Skipped items for absent targets.</param>
public sealed record ExecResult(ExecStatus Status, IReadOnlyList<ChangeItem> Changes, string? SkipReason = null) {
    public static ExecResult Applied(IReadOnlyList<ChangeItem> changes) => new(ExecStatus.Applied, changes);

    public static ExecResult Skipped(string reason, IReadOnlyList<ChangeItem>? changes = null)
        => new(ExecStatus.Skipped, changes ?? [], reason);
}

/// <summary>
///     Result of inspecting a resource against an <see cref="OperationSpec" />: whether the image
///     already satisfies the desired state, and which changes convergence would produce.
/// </summary>
public sealed record ResourceDiff(bool Satisfied, IReadOnlyList<ChangeItem> Differences);

/// <summary>
///     One bound operation: pure data produced at plan-resolution time (after parameter binding).
///     <paramref name="Spec" /> is resource-specific and validated against the resource schema.
/// </summary>
public sealed record OperationSpec(string Resource, OperationAction Action, JsonObject Spec);

/// <summary>Thrown by executors for hard failures; soft/expected misses become Skipped results.</summary>
public sealed class ExecException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
///     Per-exec runtime context handed to every executer. Immutable: everything the layer
///     provides is injected at construction (the engine creates one per plan run).
/// </summary>
public sealed class ExecContext(
    string mountPath,
    BuildLog log,
    RegistryHiveCache hives,
    string? planAssetsRoot = null) {
    /// <summary>Drive letter root of the currently attached (mounted) image layer.</summary>
    public string MountPath { get; } = mountPath;

    public BuildLog Log { get; } = log;

    /// <summary>Offline registry hive sessions for the current layer; owned by the build engine.</summary>
    public RegistryHiveCache Hives { get; } = hives;

    /// <summary>Root directory of the current plan's bundled assets (for fs.path present copies).</summary>
    public string? PlanAssetsRoot { get; } = planAssetsRoot;
}
