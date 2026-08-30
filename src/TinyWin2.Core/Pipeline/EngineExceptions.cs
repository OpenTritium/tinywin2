namespace TinyWin2.Core.Pipeline;

/// <summary>The workspace cannot serve the requested run: not empty, not resumable, or foreign.</summary>
public sealed class WorkspaceConflictException(string message) : IOException(message);

/// <summary>The requested output path exists and --overwrite was not passed.</summary>
public sealed class OutputExistsException(string message) : IOException(message);
