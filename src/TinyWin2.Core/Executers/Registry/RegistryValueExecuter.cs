using System.Text.Json.Nodes;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
/// Converges registry values inside offline hives.
/// present = create/modify values; absent = delete values or whole keys.
/// Desired (present): <c>{ hive, values:[{key,name,type,data}], single form }</c>;
/// Desired (absent): <c>{ hive, values:[{key,name}], deleteKeys:[key] }</c>.
/// </summary>
public sealed class RegistryValueExecuter(IProcessRunner runner) : IExecuter
{
    public const string ResourceId = "registry.value";
    public string Resource => ResourceId;

    private static readonly IReadOnlyDictionary<string, string> TypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dword"] = "REG_DWORD",
        ["qword"] = "REG_QWORD",
        ["string"] = "REG_SZ",
        ["expand"] = "REG_EXPAND_SZ",
        ["multi"] = "REG_MULTI_SZ",
    };

    private sealed record ValueTarget(string Key, string Name, string? RegType, JsonNode? Data);

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct)
    {
        var (hiveId, values, deleteKeys) = Parse(spec);
        var hive = await context.Hives.GetAsync(hiveId, context.Log, ct);

        var differences = new List<ChangeItem>();
        foreach (var value in values)
        {
            var keyPath = hive.KeyUnderHive(value.Key);
            var result = await runner.RunAsync("reg.exe",
                string.IsNullOrEmpty(value.Name) ? ["query", keyPath, "/ve"] : ["query", keyPath, "/v", value.Name],
                new ProcessRunOptions { IgnoreExitCode = true }, ct);

            var existing = result.ExitCode == 0 ? RegValues.ParseQueryValue(result.Output, string.IsNullOrEmpty(value.Name) ? "(Default)" : value.Name) : null;
            if (spec.Ensure == Ensure.Absent)
            {
                if (existing is not null)
                {
                    differences.Add(new ChangeItem(ChangeKind.Removed, hive.ValueUnderHive(value.Key, value.Name),
                        Before: $"{existing.Type} {existing.Data}"));
                }
                continue;
            }

            var desiredType = value.RegType!;
            var desiredData = RegValues.RenderData(desiredType, value.Data);
            if (existing is null)
            {
                differences.Add(new ChangeItem(ChangeKind.Created, hive.ValueUnderHive(value.Key, value.Name),
                    After: $"{desiredType} {desiredData}"));
            }
            else if (!string.Equals(existing.Type, desiredType, StringComparison.OrdinalIgnoreCase)
                     || !RegValues.Equals(desiredType, existing.Data, desiredData))
            {
                differences.Add(new ChangeItem(ChangeKind.Modified, hive.ValueUnderHive(value.Key, value.Name),
                    Before: $"{existing.Type} {existing.Data}",
                    After: $"{desiredType} {desiredData}"));
            }
        }

        if (spec.Ensure == Ensure.Absent)
        {
            foreach (var key in deleteKeys)
            {
                var result = await runner.RunAsync("reg.exe", ["query", hive.KeyUnderHive(key)],
                    new ProcessRunOptions { IgnoreExitCode = true }, ct);
                if (result.ExitCode == 0)
                {
                    differences.Add(new ChangeItem(ChangeKind.Removed, $"{hive.HiveId}\\{key.Trim('\\')} (key)"));
                }
            }
        }

        return new ResourceDiff(differences.Count == 0, differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct)
    {
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied)
        {
            return ExecResult.Skipped("registry values already in the desired state");
        }

        var (hiveId, values, deleteKeys) = Parse(spec);
        var hive = await context.Hives.GetAsync(hiveId, context.Log, ct);

        foreach (var change in diff.Differences)
        {
            if (change.Kind == ChangeKind.Removed && change.Target.EndsWith(" (key)"))
            {
                var keyPath = change.Target[..^" (key)".Length][(hive.HiveId.Length + 1)..];
                await runner.RunAsync("reg.exe", ["delete", hive.KeyUnderHive(keyPath), "/f"], cancellationToken: ct);
                context.Log.Info($"deleted registry key {hive.HiveId}\\{keyPath}");
                continue;
            }

            var target = values.First(v => hive.ValueUnderHive(v.Key, v.Name) == change.Target);
            if (spec.Ensure == Ensure.Absent)
            {
                var args = string.IsNullOrEmpty(target.Name)
                    ? (string[])["delete", hive.KeyUnderHive(target.Key), "/ve", "/f"]
                    : ["delete", hive.KeyUnderHive(target.Key), "/v", target.Name, "/f"];
                await runner.RunAsync("reg.exe", args, new ProcessRunOptions { IgnoreExitCode = true }, ct);
                context.Log.Info($"deleted registry value {change.Target}");
            }
            else
            {
                var desiredType = target.RegType!;
                var args = new List<string>
                {
                    "add",
                    hive.KeyUnderHive(target.Key),
                    string.IsNullOrEmpty(target.Name) ? "/ve" : "/v",
                    target.Name,
                    "/t",
                    desiredType,
                    "/d",
                    RegValues.RenderData(desiredType, target.Data),
                    "/f",
                };
                await runner.RunAsync("reg.exe", args, cancellationToken: ct);
                context.Log.Info($"set registry value {change.Target} = {desiredType}");
            }
        }

        return ExecResult.Applied(diff.Differences);
    }

    private static (string Hive, List<ValueTarget> Values, List<string> DeleteKeys) Parse(ExecSpec spec)
    {
        var desired = spec.Desired;
        var hive = desired["hive"]?.GetValue<string>()
                   ?? throw new ExecException("registry.value requires 'hive'.");

        var values = new List<ValueTarget>();
        if (desired["values"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                values.Add(ParseValue(item, spec.Ensure));
            }
        }
        else if (desired.ContainsKey("key"))
        {
            values.Add(ParseValue(desired, spec.Ensure));
        }
        if (values.Count == 0 && desired["deleteKeys"] is null)
        {
            throw new ExecException("registry.value requires 'values', a single value form, or 'deleteKeys'.");
        }

        var deleteKeys = new List<string>();
        if (spec.Ensure == Ensure.Absent && desired["deleteKeys"] is JsonArray keys)
        {
            deleteKeys.AddRange(keys.OfType<JsonValue>().Select(k => k.GetValue<string>()));
        }
        if (spec.Ensure == Ensure.Present && desired["deleteKeys"] is not null)
        {
            throw new ExecException("'deleteKeys' is only valid with ensure: absent.");
        }
        return (hive, values, deleteKeys);
    }

    private static ValueTarget ParseValue(JsonObject obj, Ensure ensure)
    {
        var key = obj["key"]?.GetValue<string>() ?? throw new ExecException("registry value requires 'key'.");
        var name = obj["name"]?.GetValue<string>() ?? "";
        var typeText = obj["type"]?.GetValue<string>();
        if (ensure == Ensure.Present)
        {
            if (typeText is null || !TypeMap.TryGetValue(typeText, out var regType))
            {
                throw new ExecException($"registry value '{key}\\{name}' requires a valid 'type' (dword|qword|string|expand|multi).");
            }
            if (!obj.ContainsKey("data"))
            {
                throw new ExecException($"registry value '{key}\\{name}' requires 'data'.");
            }
            return new ValueTarget(key.Trim('\\'), name, regType, obj["data"]!.DeepClone());
        }
        return new ValueTarget(key.Trim('\\'), name, null, null);
    }
}
