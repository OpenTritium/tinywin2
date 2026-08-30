namespace TinyWin2.Core.Executers;

/// <summary>
///     An executor owns one resource type, binds its explicit operations into typed options,
///     and applies them idempotently.
/// </summary>
public interface IExecuter {
    /// <summary>Resource id this executer owns, e.g. <c>registry.service</c>.</summary>
    string Resource { get; }

    /// <summary>
    ///     Parses and validates an operation spec into the resource's typed options.
    ///     Runs once at plan-resolution time — before any image I/O starts.
    /// </summary>
    object Bind(OperationSpec spec);

    /// <summary>Reads current state; never mutates the image. Powers preview and idempotence.</summary>
    Task<ResourceDiff> InspectAsync(ExecContext context, BoundOperation operation, CancellationToken cancellationToken);

    /// <summary>Applies the operation to the image. Must be idempotent where the resource permits it.</summary>
    Task<ExecResult> ApplyAsync(ExecContext context, BoundOperation operation, CancellationToken cancellationToken);
}
