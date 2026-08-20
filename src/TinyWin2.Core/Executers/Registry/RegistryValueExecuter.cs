using System.Text.Json.Nodes;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
/// Converges registry values inside offline hives.
/// present = create/modify values; absent = delete values or whole keys.
/// </summary>
public sealed class RegistryValueExecuter(IProcessRunner runner) : IExecuter {
    public const string ResourceId = "registry.value";
    public string Resource => ResourceId;

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var options = RegistryValueOptions.FromDesired(spec.Desired, spec.Ensure);
        var hive = await context.Hives.GetAsync(options.Hive, context.Log, ct);
        var differences = new List<ChangeItem>();
        foreach (var value in options.Values) {
            var keyPath = hive.KeyUnderHive(value.Key);
            var result = await runner.RunAsync("reg.exe",
                string.IsNullOrEmpty(value.Name) ? ["query", keyPath, "/ve"] : ["query", keyPath, "/v", value.Name],
                new ProcessRunOptions { IgnoreExitCode = true }, ct);
            var existing = result.ExitCode == 0 ? RegValues.ParseQueryValue(result.Output, string.IsNullOrEmpty(value.Name) ? "(Default)" : value.Name) : null;
            if (spec.Ensure == Ensure.Absent) {
                if (existing is not null) {
                    differences.Add(new ChangeItem(ChangeKind.Removed, hive.ValueUnderHive(value.Key, value.Name),
                        Before: $"{existing.Type} {existing.Data}"));
                }
                continue;
            }
            var desiredData = RegValues.RenderData(value.RegType, value.Data);
            if (existing is null) {
                differences.Add(new ChangeItem(ChangeKind.Created, hive.ValueUnderHive(value.Key, value.Name),
                    After: $"{value.RegType} {desiredData}"));
            }
            else if (!string.Equals(existing.Type, value.RegType, StringComparison.OrdinalIgnoreCase)
                     || !RegValues.Equals(value.RegType, existing.Data, desiredData)) {
                differences.Add(new ChangeItem(ChangeKind.Modified, hive.ValueUnderHive(value.Key, value.Name),
                    Before: $"{existing.Type} {existing.Data}",
                    After: $"{value.RegType} {desiredData}"));
            }
        }
        if (spec.Ensure == Ensure.Absent) {
            foreach (var key in options.DeleteKeys) {
                var result = await runner.RunAsync("reg.exe", ["query", hive.KeyUnderHive(key)],
                    new ProcessRunOptions { IgnoreExitCode = true }, ct);
                if (result.ExitCode == 0) {
                    differences.Add(new ChangeItem(ChangeKind.Removed, $"{hive.HiveId}\\{key.Trim('\\')} (key)"));
                }
            }
        }
        return new ResourceDiff(differences.Count == 0, differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied) {
            return ExecResult.Skipped("registry values already in the desired state");
        }
        var options = RegistryValueOptions.FromDesired(spec.Desired, spec.Ensure);
        var hive = await context.Hives.GetAsync(options.Hive, context.Log, ct);
        foreach (var change in diff.Differences) {
            if (change.Kind == ChangeKind.Removed && change.Target.EndsWith(" (key)")) {
                var keyPath = change.Target[..^" (key)".Length][(hive.HiveId.Length + 1)..];
                await runner.RunAsync("reg.exe", ["delete", hive.KeyUnderHive(keyPath), "/f"], cancellationToken: ct);
                context.Log.Info($"deleted registry key {hive.HiveId}\\{keyPath}");
                continue;
            }
            var target = options.Values.First(v => hive.ValueUnderHive(v.Key, v.Name) == change.Target);
            if (spec.Ensure == Ensure.Absent) {
                var args = string.IsNullOrEmpty(target.Name)
                    ? (string[])["delete", hive.KeyUnderHive(target.Key), "/ve", "/f"]
                    : ["delete", hive.KeyUnderHive(target.Key), "/v", target.Name, "/f"];
                await runner.RunAsync("reg.exe", args, new ProcessRunOptions { IgnoreExitCode = true }, ct);
                context.Log.Info($"deleted registry value {change.Target}");
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
                context.Log.Info($"set registry value {change.Target} = {target.RegType}");
            }
        }
        return ExecResult.Applied(diff.Differences);
    }
}
