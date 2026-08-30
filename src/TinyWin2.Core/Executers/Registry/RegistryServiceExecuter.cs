using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
///     Converges offline service start modes: every start mode is present-with-a-value.
/// </summary>
public sealed partial class RegistryServiceExecuter(IProcessRunner runner) : IExecuter {
    private const string ResourceId = "registry.service";
    public string Resource => ResourceId;

    public object Bind(OperationSpec spec) {
        if (spec.Action != OperationAction.Apply) {
            throw new ExecException($"{ResourceId} supports only action 'apply'.");
        }

        return RegistryServiceOptions.FromDesired(spec.Spec);
    }

    public async Task<ResourceDiff> InspectAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, operation, ct);
        return new(changes.All(c => c.Change.Kind == ChangeKind.Skipped), [.. changes.Select(c => c.Change)]);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, operation, ct);
        if (changes.All(c => c.Change.Kind == ChangeKind.Skipped)) {
            return ExecResult.Skipped("services already in the desired start mode",
                [.. changes.Where(c => c.Change.Kind == ChangeKind.Skipped).Select(c => c.Change)]);
        }

        var applied = new List<ChangeItem>();
        foreach (var entry in changes.Where(entry => entry.Change.Kind != ChangeKind.Skipped)) {
            try {
                await OfflineReg.AddValueAsync(runner, entry.ServiceKey, "Start", "REG_DWORD",
                    entry.Start.ToString(), entry.ServiceKey, ct);
                await OfflineReg.AddValueAsync(runner, entry.ServiceKey, "DelayedAutoStart", "REG_DWORD",
                    entry.Delayed.ToString(), entry.ServiceKey, ct);
                if (entry.TriggerInfoChanged) {
                    await WriteTriggerInfoAsync(entry.ServiceKey, entry.Triggers, ct);

                    if (entry.Triggers.Count > 0) {
                        context.Log.Info(
                            $"service {entry.Change.Target}: start=manual + {entry.Triggers.Count} trigger(s)");
                    }
                }

                context.Log.Info($"service {entry.Change.Target} → {entry.Change.After}");
                applied.Add(entry.Change);
            }
            catch (ProcessRunnerException ex) {
                context.Log.Warn($"service {entry.Change.Target} could not be modified; skipping ({ex.Result.ExitCode})");
                applied.Add(entry.Change with {
                    Kind = ChangeKind.Skipped,
                    Before = "service key is not writable in this image",
                    After = null
                });
            }
        }

        return ExecResult.Applied(applied);
    }

    private async Task<List<ServiceChange>> InspectCoreAsync(ExecContext context, BoundOperation operation,
        CancellationToken ct) {
        var options = (RegistryServiceOptions)operation.Options;
        var hive = await context.Hives.GetAsync("system", context.Log, ct);
        var controlSet = await ResolveControlSetAsync(hive, ct);
        var servicesRoot = $@"{hive.HiveKey}\{controlSet}\Services";

        // Enumerate service key names once; patterns match against them. RegQuery normalizes
        // the HKEY_LOCAL_MACHINE prefix reg.exe prints, so HKLM-form roots compare directly.
        var enumerated = await OfflineReg.QueryAsync(runner, servicesRoot, ct);

        var allNames = RegQuery.DirectChildKeys(enumerated.Output, servicesRoot).ToList();
        var resolved = new List<string>(options.Services);
        foreach (var pattern in options.ServicePatterns) {
            var matches = allNames.Where(n => LikePattern.IsMatch(pattern, n)).ToList();
            if (matches.Count == 0) {
                context.Log.Warn($"no services matched pattern '{pattern}' in {controlSet}; skipping.");
            }

            resolved.AddRange(matches);
        }

        var desiredDelayed = options.IsDelayed ? 1 : 0;
        var changes = new List<ServiceChange>();
        foreach (var service in resolved.Distinct(StringComparer.OrdinalIgnoreCase)) {
            var serviceKey = $"{servicesRoot}\\{service}";
            var existingStart = await ReadDwordAsync(serviceKey, "Start", ct);
            if (existingStart is null) {
                context.Log.Warn($"service '{service}' was not present in {controlSet}; skipping.");
                changes.Add(new(new(ChangeKind.Skipped, service, "service not present"), serviceKey, 0, 0, []));
                continue;
            }

            var existingDelayed = await ReadDwordAsync(serviceKey, "DelayedAutoStart", ct) ?? 0;
            var desiredTriggers = string.Equals(options.Start, "trigger", StringComparison.OrdinalIgnoreCase)
                ? options.ResolveTriggers()
                : [];
            var triggerInfoChanged = !await HasDesiredTriggerInfoAsync(serviceKey, desiredTriggers, ct);
            if (existingStart != options.StartDword || existingDelayed != desiredDelayed || triggerInfoChanged) {
                changes.Add(new(
                    new(ChangeKind.Modified, service,
                        Describe(existingStart.Value, existingDelayed),
                        Describe(options.StartDword, desiredDelayed)),
                    serviceKey, options.StartDword, desiredDelayed, desiredTriggers, triggerInfoChanged));
            }
        }

        return changes;
    }

    /// <summary>Replaces offline TriggerInfo using the same 0-based layout emitted by the SCM.</summary>
    private async Task WriteTriggerInfoAsync(
        string serviceKey, IReadOnlyList<RegistryServiceOptions.ServiceTrigger> triggers, CancellationToken ct) {
        var triggerRoot = $"{serviceKey}\\TriggerInfo";
        await OfflineReg.DeleteKeyAsync(runner, triggerRoot, serviceKey, ct);

        for (var i = 0; i < triggers.Count; i++) {
            var kind = triggers[i];
            var key = $"{triggerRoot}\\{i}";
            await OfflineReg.AddValueAsync(runner, key, "Type", "REG_DWORD", kind.Type.ToString(),
                serviceKey, ct);
            await OfflineReg.AddValueAsync(runner, key, "Action", "REG_DWORD", "1", serviceKey, ct);
            await OfflineReg.AddValueAsync(runner, key, "GUID", "REG_BINARY",
                Convert.ToHexString(kind.SubType.ToByteArray()), serviceKey, ct);
        }
    }

    private async Task<bool> HasDesiredTriggerInfoAsync(
        string serviceKey, IReadOnlyList<RegistryServiceOptions.ServiceTrigger> triggers, CancellationToken ct) {
        var triggerRoot = $"{serviceKey}\\TriggerInfo";
        var root = await OfflineReg.QueryAsync(runner, triggerRoot, ct, "/s");
        if (!root.Success) {
            return triggers.Count == 0;
        }

        var actualKeys = RegQuery.DirectChildKeys(root.Output, triggerRoot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedKeys = Enumerable.Range(0, triggers.Count)
            .Select(index => index.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualKeys.SetEquals(expectedKeys)) {
            return false;
        }

        for (var i = 0; i < triggers.Count; i++) {
            var result = await OfflineReg.QueryAsync(runner, $"{triggerRoot}\\{i}", ct);
            if (!result.Success) {
                return false;
            }

            var type = RegValues.ParseQueryValue(result.Output, "Type");
            var action = RegValues.ParseQueryValue(result.Output, "Action");
            var guid = RegValues.ParseQueryValue(result.Output, "GUID");
            var expected = triggers[i];
            if (type is null || action is null || guid is null
                || !type.Type.Equals("REG_DWORD", StringComparison.OrdinalIgnoreCase)
                || !action.Type.Equals("REG_DWORD", StringComparison.OrdinalIgnoreCase)
                || !guid.Type.Equals("REG_BINARY", StringComparison.OrdinalIgnoreCase)
                || !RegValues.Equals("REG_DWORD", type.Data, $"0x{expected.Type:x}")
                || !RegValues.Equals("REG_DWORD", action.Data, "0x1")
                || !BinaryEqualsGuid(guid.Data, expected.SubType)) {
                return false;
            }
        }

        return true;
    }

    private static bool BinaryEqualsGuid(string data, Guid expected) =>
        string.Equals(data.Replace(" ", "", StringComparison.Ordinal),
            Convert.ToHexString(expected.ToByteArray()), StringComparison.OrdinalIgnoreCase);

    private async Task<string> ResolveControlSetAsync(RegistryHive hive, CancellationToken ct) {
        var result = await runner.RunAsync("reg.exe", ["query", $"{hive.HiveKey}\\Select", "/v", "Current"],
            new() { IgnoreExitCode = true }, ct);
        var match = HexRegex().Match(result.Output);
        if (!result.Success || !match.Success) {
            throw new ExecException("could not resolve the active control set from the offline SYSTEM hive.");
        }

        return $"ControlSet{Convert.ToInt32(match.Groups[1].Value, 16):D3}";
    }

    private async Task<int?> ReadDwordAsync(string keyPath, string valueName, CancellationToken ct) {
        var result = await OfflineReg.QueryAsync(runner, keyPath, ct, "/v", valueName);
        if (!result.Success) {
            return null;
        }

        var value = RegValues.ParseQueryValue(result.Output, valueName);
        if (value is null || !value.Type.Equals("REG_DWORD", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        return RegValues.TryParseDword(value.Data, out var parsed) ? parsed : null;
    }

    private static string Describe(int start, int delayed) => start switch {
        4 => "disabled",
        3 => "manual",
        _ => delayed == 1 ? "delayedAuto" : "auto"
    };

    [GeneratedRegex("0x([0-9A-Fa-f]+)")]
    private static partial Regex HexRegex();

    /// <summary>
    ///     One structured difference carrying its own service key and desired start values —
    ///     apply never re-parses options or re-resolves the control set.
    /// </summary>
    private sealed record ServiceChange(
        ChangeItem Change,
        string ServiceKey,
        int Start,
        int Delayed,
        IReadOnlyList<RegistryServiceOptions.ServiceTrigger> Triggers,
        bool TriggerInfoChanged = false);
}
