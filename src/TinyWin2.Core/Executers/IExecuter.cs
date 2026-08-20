namespace TinyWin2.Core.Executers;

/// <summary>
/// An executer converges one resource type toward a desired state (DSC-style):
/// add, modify and remove are all expressed as <see cref="Ensure"/> targets over the same resource.
/// </summary>
public interface IExecuter
{
    /// <summary>Resource id this executer owns, e.g. <c>registry.service</c>.</summary>
    string Resource { get; }

    /// <summary>Reads current state; never mutates the image. Powers preview and idempotence.</summary>
    Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken cancellationToken);

    /// <summary>Converges the image toward the desired state. Must be idempotent.</summary>
    Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken cancellationToken);
}
