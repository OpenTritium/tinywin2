using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
/// Converges registry values inside offline hives.
/// present = create/modify values; absent = delete values or whole keys.
/// </summary>
public sealed class RegistryValueExecuter(IProcessRunner runner) : IExecuter {
    public const string ResourceId = "registry.value";
    public string Resource => ResourceId;

    /// <summary>One structured difference: exactly one of Value/DeleteKey is set.</summary>
    private sealed record ValueChange(ChangeItem Change, RegistryValueTarget? Value = null, string? DeleteKey = null);

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, spec, ct);
        return new ResourceDiff(changes.Count == 0, changes.Select(c => c.Change).ToList());
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, spec, ct);
        if (changes.Count == 0) {
            return ExecResult.Skipped("registry values already in the desired state");
        }
        var options = RegistryValueOptions.FromDesired(spec.Desired, spec.Ensure);
        var hive = await context.Hives.GetAsync(options.Hive, context.Log, ct);
        foreach (var change in changes) {
            if (change.DeleteKey is { } deleteKey) {
                await runner.RunAsync("reg.exe", ["delete", hive.KeyUnderHive(deleteKey), "/f"], cancellationToken: ct);
                context.Log.Info($"deleted registry key {hive.HiveId}\\{deleteKey}");
                continue;
            }
            var target = change.Value!;
            if (spec.Ensure == Ensure.Absent) {
                var args = string.IsNullOrEmpty(target.Name)
                    ? (string[])["delete", hive.KeyUnderHive(target.Key), "/ve", "/f"]
                    : ["delete", hive.KeyUnderHive(target.Key), "/v", target.Name, "/f"];
                await runner.RunAsync("reg.exe", args, new ProcessRunOptions { IgnoreExitCode = true }, ct);
                context.Log.Info($"deleted registry value {change.Change.Target}");
            }
            else {
                var args = new List<string>
                {
                    "add",
                    hive.KeyUnderHive(target.Key),
                    string.IsNullOrEmpty(target.Name) ? "/ve" : "/v",
                    target.Name,
                    "/t",
                    target.RegType,
                    "/d",
                    RegValues.RenderData(target.RegType, target.Data),
                    "/f",
                };
                await runner.RunAsync("reg.exe", args, cancellationToken: ct);
                context.Log.Info($"set registry value {change.Change.Target} = {target.RegType}");
            }
        }
        return ExecResult.Applied(changes.Select(c => c.Change).ToList());
    }

    /// <summary>Produces structured differences carrying their own execution targets — no reverse lookup by display string.</summary>
    private async Task<List<ValueChange>> InspectCoreAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var options = RegistryValueOptions.FromDesired(spec.Desired, spec.Ensure);
        var hive = await context.Hives.GetAsync(options.Hive, context.Log, ct);
        var changes = new List<ValueChange>();
        foreach (var value in options.Values) {
            var keyPath = hive.KeyUnderHive(value.Key);
            var result = await runner.RunAsync("reg.exe",
                string.IsNullOrEmpty(value.Name) ? ["query", keyPath, "/ve"] : ["query", keyPath, "/v", value.Name],
                new ProcessRunOptions { IgnoreExitCode = true }, ct);
            var existing = result.Success ? RegValues.ParseQueryValue(result.Output, string.IsNullOrEmpty(value.Name) ? "(Default)" : value.Name) : null;
            var display = hive.ValueUnderHive(value.Key, value.Name);
            if (spec.Ensure == Ensure.Absent) {
                if (existing is not null) {
                    changes.Add(new ValueChange(
                        new ChangeItem(ChangeKind.Removed, display, Before: $"{existing.Type} {existing.Data}"),
                        value));
                }
                continue;
            }
            var desiredData = RegValues.RenderData(value.RegType, value.Data);
            if (existing is null) {
                changes.Add(new ValueChange(
                    new ChangeItem(ChangeKind.Created, display, After: $"{value.RegType} {desiredData}"),
                    value));
            }
            else if (!string.Equals(existing.Type, value.RegType, StringComparison.OrdinalIgnoreCase)
                     || !RegValues.Equals(value.RegType, existing.Data, desiredData)) {
                changes.Add(new ValueChange(
                    new ChangeItem(ChangeKind.Modified, display,
                        Before: $"{existing.Type} {existing.Data}",
                        After: $"{value.RegType} {desiredData}"),
                    value));
            }
        }
        if (spec.Ensure == Ensure.Absent) {
            foreach (var key in options.DeleteKeys) {
                var result = await runner.RunAsync("reg.exe", ["query", hive.KeyUnderHive(key)],
                    new ProcessRunOptions { IgnoreExitCode = true }, ct);
                if (result.Success) {
                    changes.Add(new ValueChange(
                        new ChangeItem(ChangeKind.Removed, $"{hive.HiveId}\\{key.Trim('\\')} (key)"),
                        DeleteKey: key.Trim('\\')));
                }
            }
        }
        return changes;
    }
}
