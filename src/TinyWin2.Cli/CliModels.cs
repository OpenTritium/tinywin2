using TinyWin2.Core.Pipeline;

namespace TinyWin2.Cli;

internal sealed record SelectionRequest(
    string? PlansDirectory,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Plans,
    IReadOnlyList<string> Sets);

internal sealed record DoctorRequest(string? OutputDirectory, bool Json);

internal sealed record InspectRequest(string Source, bool Json);

internal sealed record PlanListRequest(string? PlansDirectory, string? Category, bool Json);

internal sealed record PlanShowRequest(string Id, string? PlansDirectory);

internal sealed record ProfileListRequest(string? PlansDirectory, string? ProfilesDirectory);

internal sealed record ProfileShowRequest(string File);

internal sealed record ProfileExportRequest(
    string Output,
    string? Name,
    SelectionRequest Selection);

internal sealed record ProfileValidateRequest(
    string File,
    string? PlansDirectory);

internal sealed record BuildRequest(
    string Source,
    int ImageIndex,
    SelectionRequest Selection,
    string OutputDirectory,
    OutputFormat Format,
    bool CreateIso,
    bool Fast,
    bool ContinueOnError,
    bool KeepLayers,
    bool DryRun,
    bool SingleLayer,
    bool SkipEvidence,
    string? ResumeWorkspace,
    bool ResumeLatest,
    string? OscdimgPath,
    long BaseVhdxMaximumMb,
    bool JsonEvents);

internal sealed record PreviewRequest(
    string Source,
    int ImageIndex,
    SelectionRequest Selection,
    string OutputDirectory,
    long BaseVhdxMaximumMb,
    bool Json);

internal sealed record LayerListRequest(string Workspace, bool Json);

internal sealed record LayerDiffRequest(
    string Workspace,
    int From,
    int To,
    bool Json);

internal sealed record LayerExtractRequest(
    string Workspace,
    int Layer,
    string ImagePath,
    string Destination);

internal sealed record LayerRollbackRequest(
    string Workspace,
    int Layer,
    string Output,
    OutputFormat Format,
    bool Fast);
