namespace TinyWin2.Core.Executers;

/// <summary>
/// An executor owns one resource type and validates/applies its explicit operations.
/// </summary>
public interface IExecuter {
    /// <summary>Resource id this executer owns, e.g. <c>registry.service</c>.</summary>
    string Resource { get; }

    /// <summary>Validates an operation before any image I/O starts.</summary>
    void Validate(OperationSpec spec);

    /// <summary>Reads current state; never mutates the image. Powers preview and idempotence.</summary>
    Task<ResourceDiff> InspectAsync(ExecContext context, OperationSpec spec, CancellationToken cancellationToken);

    /// <summary>Applies the operation to the image. Must be idempotent where the resource permits it.</summary>
    Task<ExecResult> ApplyAsync(ExecContext context, OperationSpec spec, CancellationToken cancellationToken);
}
