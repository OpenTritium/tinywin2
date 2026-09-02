using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
///     Converges registry values inside offline hives.
///     present = create/modify values; absent = delete values or whole keys.
/// </summary>
public sealed class RegistryValueExecuter(IProcessRunner runner) : IExecuter {
    private const string ResourceId = "registry.value";
    public string Resource => ResourceId;

    public object Bind(OperationSpec spec) {
        if (spec.Action is not (OperationAction.Apply or OperationAction.Remove)) {
            throw new ExecException($"{ResourceId} supports actions 'apply' and 'remove'.");
        }

        return RegistryValueOptions.FromDesired(spec.Spec, spec.Action);
    }

    public async Task<ResourceDiff> InspectAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, operation, ct);
        return new(changes.Count == 0, [.. changes.Select(c => c.Change)]);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, operation, ct);
        if (changes.Count == 0) {
            return ExecResult.Skipped("registry values already in the desired state");
        }

        var options = (RegistryValueOptions)operation.Options;
        var hive = await context.Hives.GetAsync(options.Hive, context.Log, ct);
        foreach (var change in changes) {
            if (change.DeleteKey is { } deleteKey) {
                var keyPath = hive.KeyUnderHive(deleteKey);
                if (!await OfflineReg.DeleteKeyAsync(runner, keyPath, ct)) {
                    throw new ExecException(
                        $"registry key '{hive.HiveId}\\{deleteKey}' is locked and could not be deleted " +
                        "even after an ACL rescue.");
                }

                context.Log.Info($"deleted registry key {hive.HiveId}\\{deleteKey}");
                continue;
            }

            var target = change.Value!;
            if (operation.Action == OperationAction.Remove) {
                var valuePath = hive.KeyUnderHive(target.Key);
                if (!await OfflineReg.DeleteValueAsync(runner, valuePath, target.Name, ct)) {
                    throw new ExecException(
                        $"registry value '{change.Change.Target}' is locked and could not be deleted " +
                        "even after an ACL rescue.");
                }

                context.Log.Info($"deleted registry value {change.Change.Target}");
            }
            else {
                var args = new List<string> {
                    "add",
                    hive.KeyUnderHive(target.Key),
                    string.IsNullOrEmpty(target.Name) ? "/ve" : "/v",
                    target.Name,
                    "/t",
                    target.RegType,
                    "/d",
                    RegValues.RenderData(target.RegType, target.Data),
                    "/f"
                };
                await runner.RunAsync("reg.exe", args, cancellationToken: ct);
                context.Log.Info($"set registry value {change.Change.Target} = {target.RegType}");
            }
        }

        return ExecResult.Applied([.. changes.Select(c => c.Change)]);
    }

    /// <summary>Produces structured differences carrying their own execution targets — no reverse lookup by display string.</summary>
    private async Task<List<ValueChange>> InspectCoreAsync(ExecContext context, BoundOperation operation,
        CancellationToken ct) {
        var options = (RegistryValueOptions)operation.Options;
        var hive = await context.Hives.GetAsync(options.Hive, context.Log, ct);
        var changes = new List<ValueChange>();
        foreach (var value in options.Values) {
            var keyPath = hive.KeyUnderHive(value.Key);
            var result = string.IsNullOrEmpty(value.Name)
                ? await OfflineReg.QueryAsync(runner, keyPath, ct, "/ve")
                : await OfflineReg.QueryAsync(runner, keyPath, ct, "/v", value.Name);
            var existing = result.Success
                ? RegValues.ParseQueryValue(result.Output, string.IsNullOrEmpty(value.Name) ? "(Default)" : value.Name)
                : null;
            var display = hive.DisplayValuePath(value.Key, value.Name);
            if (operation.Action == OperationAction.Remove) {
                if (existing is not null) {
                    changes.Add(new(
                        new(ChangeKind.Removed, display, $"{existing.Type} {existing.Data}"),
                        value));
                }

                continue;
            }

            var desiredData = RegValues.RenderData(value.RegType, value.Data);
            if (existing is null) {
                changes.Add(new(
                    new(ChangeKind.Created, display, After: $"{value.RegType} {desiredData}"),
                    value));
            }
            else if (!string.Equals(existing.Type, value.RegType, StringComparison.OrdinalIgnoreCase)
                     || !RegValues.Equals(value.RegType, existing.Data, desiredData)) {
                changes.Add(new(
                    new(ChangeKind.Modified, display,
                        $"{existing.Type} {existing.Data}",
                        $"{value.RegType} {desiredData}"),
                    value));
            }
        }

        if (operation.Action == OperationAction.Remove) {
            foreach (var key in options.DeleteKeys) {
                var result = await OfflineReg.QueryAsync(runner, hive.KeyUnderHive(key), ct);
                if (result.Success) {
                    changes.Add(new(
                        new(ChangeKind.Removed, $"{hive.HiveId}\\{key.Trim('\\')} (key)"),
                        DeleteKey: key.Trim('\\')));
                }
            }
        }

        return changes;
    }

    /// <summary>One structured difference: exactly one of Value/DeleteKey is set.</summary>
    private sealed record ValueChange(ChangeItem Change, RegistryValueTarget? Value = null, string? DeleteKey = null);
}
