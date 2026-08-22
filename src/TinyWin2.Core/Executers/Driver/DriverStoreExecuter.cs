using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Driver;

/// <summary>
///     Converges offline third-party driver packages through DISM. The requested INF
///     names match <c>Original File Name</c>; removal uses DISM's <c>Published Name</c>
///     so the driver store metadata remains consistent.
/// </summary>
public sealed class DriverStoreExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    private const string ResourceId = "driver.store";
    public string Resource => ResourceId;

    public void Validate(OperationSpec spec) {
        if (spec.Action != OperationAction.Remove) {
            throw new ExecException($"{ResourceId} supports only action 'remove'.");
        }

        _ = DriverStoreOptions.FromDesired(spec.Spec);
    }

    public async Task<ResourceDiff> InspectAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = await InspectCoreAsync(context, spec, ct);
        return new(changes.All(c => c.Change.Kind == ChangeKind.Skipped),
            [.. changes.Select(c => c.Change)]);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = await InspectCoreAsync(context, spec, ct);
        if (changes.All(c => c.Change.Kind == ChangeKind.Skipped)) {
            return ExecResult.Skipped("no removable third-party Driver Store packages",
                [.. changes.Select(c => c.Change)]);
        }

        var applied = changes.Where(c => c.Change.Kind == ChangeKind.Skipped)
            .Select(c => c.Change)
            .ToList();
        foreach (var change in changes.Where(c => c.Change.Kind != ChangeKind.Skipped)) {
            var publishedName = change.PublishedName
                                ?? throw new ExecException(
                                    $"driver target '{change.Change.Target}' has no published name.");
            var (exitCode, output) = await RunDismAsync(context,
                ["/Remove-Driver", $"/Driver:{publishedName}"], ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"{ResourceId}: {change.Change.Target} removed");
                applied.Add(change.Change);
            }
            else {
                throw new ExecException(
                    $"dism.exe failed to remove driver '{change.Change.Target}' (exit {exitCode}).");
            }
        }

        return ExecResult.Applied(applied);
    }

    private async Task<List<DriverChange>> InspectCoreAsync(
        ExecContext context, OperationSpec spec, CancellationToken ct) {
        var options = DriverStoreOptions.FromDesired(spec.Spec);
        var (exitCode, output) = await RunDismAsync(context,
            ["/Get-Drivers", "/All", "/Format:List"], ct);
        var outcome = DismErrors.Classify(exitCode, output);
        if (outcome is not (DismOutcome.Success or DismOutcome.SuccessRebootRequired)) {
            throw new ExecException($"dism.exe failed to list driver targets (exit {exitCode}).");
        }

        var records = DismListParser.Parse(output, "Published Name");
        if (records.Count == 0) {
            throw new ExecException(
                $"dism.exe returned no driver records despite a successful listing (exit {exitCode}).");
        }

        var byOriginalName = records
            .Where(r => DismListParser.Get(r, "Original File Name") is not null)
            .GroupBy(r => DismListParser.Get(r, "Original File Name")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var changes = new List<DriverChange>();
        foreach (var infName in options.InfNames) {
            if (!byOriginalName.TryGetValue(infName, out var matches)) {
                changes.Add(new(new(ChangeKind.Skipped, infName, "not in DISM driver list")));
                continue;
            }

            foreach (var record in matches) {
                var publishedName = DismListParser.Get(record, "Published Name");
                if (publishedName is null) {
                    changes.Add(new(new(ChangeKind.Skipped, infName, "driver record has no published name")));
                    continue;
                }

                var inbox = DismListParser.Get(record, "Inbox");
                if (string.Equals(inbox, "Yes", StringComparison.OrdinalIgnoreCase)) {
                    context.Log.Info($"driver '{infName}' is an inbox package; DISM cannot remove it, skipping.");
                    changes.Add(new(new(ChangeKind.Skipped, infName,
                        "inbox driver packages cannot be removed by DISM")));
                    continue;
                }

                changes.Add(new(
                    new(ChangeKind.Removed, infName, publishedName), publishedName));
            }
        }

        return changes;
    }

    private sealed record DriverChange(ChangeItem Change, string? PublishedName = null);
}
