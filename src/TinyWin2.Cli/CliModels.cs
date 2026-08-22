using TinyWin2.Core.Pipeline;

namespace TinyWin2.Cli;

internal sealed record SelectionRequest(
    string? PlansDirectory,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Plans,
    IReadOnlyList<string> Sets);

internal sealed record DoctorRequest(string Workspace, bool Json);

internal sealed record InspectRequest(string Input, bool Json);

internal sealed record ValidateRequest(string Input, string Kind, bool Json);

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
    string Input,
    int ImageIndex,
    SelectionRequest Selection,
    string Output,
    string Workspace,
    OutputFormat Format,
    bool Fast,
    string? Compression,
    bool Verify,
    bool NoVerify,
    bool CheckIntegrity,
    bool ContinueOnError,
    bool DryRun,
    bool SingleLayer,
    bool SkipEvidence,
    bool Resume,
    bool Overwrite,
    long BaseVhdxMaximumMb,
    bool JsonEvents);

internal sealed record PreviewRequest(
    string Input,
    int ImageIndex,
    SelectionRequest Selection,
    string Workspace,
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

internal sealed record PackageIsoRequest(
    string Input,
    string Image,
    string Output,
    string Workspace,
    string Oscdimg,
    bool Overwrite);

internal sealed record LayerRollbackRequest(
    string Workspace,
    int Layer,
    string Output,
    OutputFormat Format,
    bool Fast,
    string? Compression,
    bool Verify,
    bool NoVerify,
    bool CheckIntegrity);
