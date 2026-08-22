using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
/// Converges offline service start modes: every start mode is present-with-a-value.
/// </summary>
public sealed partial class RegistryServiceExecuter(IProcessRunner runner) : IExecuter {
    private const string ResourceId = "registry.service";
    public string Resource => ResourceId;

    /// <summary>
    /// One structured difference carrying its own service key and desired start values —
    /// apply never re-parses options or re-resolves the control set.
    /// </summary>
    private sealed record ServiceChange(ChangeItem Change, string ServiceKey, int Start, int Delayed,
        IReadOnlyList<RegistryServiceOptions.ServiceTrigger> Triggers, bool TriggerInfoChanged = false);

    public void Validate(OperationSpec spec) {
        if (spec.Action != OperationAction.Configure) {
            throw new ExecException($"{ResourceId} supports only action 'configure'.");
        }
        _ = RegistryServiceOptions.FromDesired(spec.Spec);
    }

    public async Task<ResourceDiff> InspectAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = await InspectCoreAsync(context, spec, ct);
        return new(changes.All(c => c.Change.Kind == ChangeKind.Skipped), [.. changes.Select(c => c.Change)]);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = await InspectCoreAsync(context, spec, ct);
        if (changes.All(c => c.Change.Kind == ChangeKind.Skipped)) {
            return ExecResult.Skipped("services already in the desired start mode",
                [.. changes.Where(c => c.Change.Kind == ChangeKind.Skipped).Select(c => c.Change)]);
        }

        var applied = new List<ChangeItem>();
        foreach (var entry in changes) {
            if (entry.Change.Kind == ChangeKind.Skipped) {
                continue;
            }

            await AddDwordWithAclRescueAsync(entry.ServiceKey, "Start", entry.Start, ct);
            await AddDwordWithAclRescueAsync(entry.ServiceKey, "DelayedAutoStart", entry.Delayed, ct);
            if (entry.TriggerInfoChanged) {
                await WriteTriggerInfoAsync(entry.ServiceKey, entry.Triggers, ct);
                if (entry.Triggers.Count > 0) {
                    context.Log.Info($"service {entry.Change.Target}: start=manual + {entry.Triggers.Count} trigger(s)");
                }
            }
            context.Log.Info($"service {entry.Change.Target} → {entry.Change.After}");
            applied.Add(entry.Change);
        }

        return ExecResult.Applied(applied);
    }

    private async Task<List<ServiceChange>> InspectCoreAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        var options = RegistryServiceOptions.FromDesired(spec.Spec);
        var hive = await context.Hives.GetAsync("system", context.Log, ct);
        var controlSet = await ResolveControlSetAsync(hive, ct);
        var servicesRoot = $@"{hive.HiveKey}\{controlSet}\Services";

        // Enumerate service key names once; patterns match against them.
        var enumerated = await runner.RunAsync("reg.exe", ["query", servicesRoot],
            new() { IgnoreExitCode = true }, ct);
        if (!enumerated.Success && enumerated.ExitCode != 1) {
            throw new ProcessRunnerException("reg.exe", enumerated);
        }
        var allNames = enumerated.Output.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.StartsWith(servicesRoot + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[(servicesRoot.Length + 1)..])
            .Where(n => !n.Contains('\\'))
            .ToList();
        var resolved = new List<string>(options.Services);
        foreach (var pattern in options.ServicePatterns) {
            var regex = LikePattern.ToRegex(pattern);
            var matches = allNames.Where(n => regex.IsMatch(n)).ToList();
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
                        Before: Describe(existingStart.Value, existingDelayed),
                        After: Describe(options.StartDword, desiredDelayed)),
                    serviceKey, options.StartDword, desiredDelayed, desiredTriggers, triggerInfoChanged));
            }
        }

        return changes;
    }

    /// <summary>Replaces offline TriggerInfo using the same 0-based layout emitted by the SCM.</summary>
    private async Task WriteTriggerInfoAsync(
        string serviceKey, IReadOnlyList<RegistryServiceOptions.ServiceTrigger> triggers, CancellationToken ct) {
        var triggerRoot = $"{serviceKey}\\TriggerInfo";
        await DeleteKeyWithAclRescueAsync(serviceKey, triggerRoot, ct);

        for (var i = 0; i < triggers.Count; i++) {
            var kind = triggers[i];
            var key = $"{triggerRoot}\\{i}";
            await AddDwordWithAclRescueAsync(key, "Type", kind.Type, ct, serviceKey);
            await AddDwordWithAclRescueAsync(key, "Action", 1, ct, serviceKey);
            await AddBinaryWithAclRescueAsync(key, "GUID", Convert.ToHexString(kind.SubType.ToByteArray()), ct, serviceKey);
        }
    }

    private async Task<bool> HasDesiredTriggerInfoAsync(
        string serviceKey, IReadOnlyList<RegistryServiceOptions.ServiceTrigger> triggers, CancellationToken ct) {
        var triggerRoot = $"{serviceKey}\\TriggerInfo";
        var root = await runner.RunAsync("reg.exe", ["query", triggerRoot, "/s"],
            new() { IgnoreExitCode = true }, ct);
        if (!root.Success) {
            if (root.ExitCode == 1) {
                return triggers.Count == 0;
            }

            throw new ProcessRunnerException("reg.exe", root);
        }

        var actualKeys = root.Output.Split('\n')
            .Select(line => line.TrimEnd('\r').Trim())
            .Where(line => line.StartsWith(triggerRoot + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(triggerRoot.Length + 1)..])
            .Where(name => !name.Contains('\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedKeys = Enumerable.Range(0, triggers.Count)
            .Select(index => index.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualKeys.SetEquals(expectedKeys)) {
            return false;
        }

        for (var i = 0; i < triggers.Count; i++) {
            var result = await runner.RunAsync("reg.exe", ["query", $"{triggerRoot}\\{i}"],
                new() { IgnoreExitCode = true }, ct);
            if (!result.Success) {
                if (result.ExitCode != 1) {
                    throw new ProcessRunnerException("reg.exe", result);
                }

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

    private async Task DeleteKeyWithAclRescueAsync(string serviceKey, string key, CancellationToken ct) {
        var result = await runner.RunAsync("reg.exe", ["delete", key, "/f"],
            new() { IgnoreExitCode = true }, ct);
        if (result.Success || result.ExitCode == 1) {
            return;
        }

        await RegistryAcl.RescueAsync(runner, serviceKey, ct);
        result = await runner.RunAsync("reg.exe", ["delete", key, "/f"],
            new() { IgnoreExitCode = true }, ct);
        if (!result.Success && result.ExitCode != 1) {
            throw new ProcessRunnerException("reg.exe", result);
        }
    }

    private async Task AddBinaryWithAclRescueAsync(
        string key, string name, string value, CancellationToken ct, string aclKey) {
        try {
            await runner.RunAsync("reg.exe",
                ["add", key, "/v", name, "/t", "REG_BINARY", "/d", value, "/f"], cancellationToken: ct);
        }
        catch (ProcessRunnerException) {
            await RegistryAcl.RescueAsync(runner, aclKey, ct);
            await runner.RunAsync("reg.exe",
                ["add", key, "/v", name, "/t", "REG_BINARY", "/d", value, "/f"], cancellationToken: ct);
        }
    }

    private static bool BinaryEqualsGuid(string data, Guid expected) =>
        string.Equals(data.Replace(" ", "", StringComparison.Ordinal),
            Convert.ToHexString(expected.ToByteArray()), StringComparison.OrdinalIgnoreCase);

    private async Task AddDwordWithAclRescueAsync(
        string serviceKey, string name, int value, CancellationToken ct, string? aclKey = null) {
        try {
            await RegAddDwordAsync(serviceKey, name, value, ct);
        }
        catch (ProcessRunnerException) {
            // TrustedInstaller-owned service keys (e.g. DPS) deny Administrators write:
            // claim ownership + FullControl for the group, then retry once.
            await RegistryAcl.RescueAsync(runner, aclKey ?? serviceKey, ct);
            await RegAddDwordAsync(serviceKey, name, value, ct);
        }
    }

    private Task RegAddDwordAsync(string serviceKey, string name, int value, CancellationToken ct) =>
        runner.RunAsync("reg.exe",
            ["add", serviceKey, "/v", name, "/t", "REG_DWORD", "/d", value.ToString(), "/f"],
            cancellationToken: ct);

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
        var result = await runner.RunAsync("reg.exe", ["query", keyPath, "/v", valueName],
            new() { IgnoreExitCode = true }, ct);
        if (!result.Success && result.ExitCode != 1) {
            throw new ProcessRunnerException("reg.exe", result);
        }

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
        _ => delayed == 1 ? "delayedAuto" : "auto",
    };

    [GeneratedRegex("0x([0-9A-Fa-f]+)")]
    private static partial Regex HexRegex();
}
