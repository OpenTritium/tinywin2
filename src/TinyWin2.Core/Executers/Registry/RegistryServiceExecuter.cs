using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
/// Converges offline service start modes. Unifies v1's DisableOfflineService and
/// ConfigureOfflineService: every start mode is present-with-a-value.
/// </summary>
public sealed partial class RegistryServiceExecuter(IProcessRunner runner) : IExecuter {
    private const string ResourceId = "registry.service";
    public string Resource => ResourceId;

    /// <summary>
    /// One structured difference carrying its own service key and desired start values —
    /// apply never re-parses options or re-resolves the control set.
    /// </summary>
    private sealed record ServiceChange(ChangeItem Change, string ServiceKey, int Start, int Delayed);

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, spec, ct);
        return new(changes.All(c => c.Change.Kind == ChangeKind.Skipped), [.. changes.Select(c => c.Change)]);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, spec, ct);
        if (changes.All(c => c.Change.Kind == ChangeKind.Skipped)) {
            return ExecResult.Skipped("services already in the desired start mode",
                [.. changes.Where(c => c.Change.Kind == ChangeKind.Skipped).Select(c => c.Change)]);
        }

        var applied = new List<ChangeItem>();
        foreach (var (change, serviceKey, start, delayed) in changes) {
            if (change.Kind == ChangeKind.Skipped) {
                continue;
            }

            await AddDwordWithAclRescueAsync(serviceKey, "Start", start, ct);
            await AddDwordWithAclRescueAsync(serviceKey, "DelayedAutoStart", delayed, ct);
            context.Log.Info($"service {change.Target} → {change.After}");
            applied.Add(change);
        }

        return ExecResult.Applied(applied);
    }

    private async Task<List<ServiceChange>> InspectCoreAsync(ExecContext context, ExecSpec spec, CancellationToken ct) {
        var options = RegistryServiceOptions.FromDesired(spec.Desired);
        var hive = await context.Hives.GetAsync("system", context.Log, ct);
        var controlSet = await ResolveControlSetAsync(hive, ct);
        var servicesRoot = $@"{hive.HiveKey}\{controlSet}\Services";

        // Enumerate service key names once; patterns match against them (v1 wildcard semantics).
        var enumerated = await runner.RunAsync("reg.exe", ["query", servicesRoot],
            new() { IgnoreExitCode = true }, ct);
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
                changes.Add(new(new(ChangeKind.Skipped, service, "service not present"), serviceKey, 0, 0));
                continue;
            }

            var existingDelayed = await ReadDwordAsync(serviceKey, "DelayedAutoStart", ct) ?? 0;
            if (existingStart != options.StartDword || existingDelayed != desiredDelayed) {
                changes.Add(new(
                    new(ChangeKind.Modified, service,
                        Before: Describe(existingStart.Value, existingDelayed),
                        After: Describe(options.StartDword, desiredDelayed)),
                    serviceKey, options.StartDword, desiredDelayed));
            }
        }

        return changes;
    }

    private async Task AddDwordWithAclRescueAsync(string serviceKey, string name, int value, CancellationToken ct) {
        try {
            await RegAddDwordAsync(serviceKey, name, value, ct);
        }
        catch (ProcessRunnerException) {
            // TrustedInstaller-owned service keys (e.g. DPS) deny Administrators write:
            // claim ownership + FullControl for the group, then retry once.
            await RegistryAcl.RescueAsync(runner, serviceKey, ct);
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
        if (result.ExitCode != 0) {
            return null;
        }

        var value = RegValues.ParseQueryValue(result.Output, valueName);
        if (value is null || !value.Type.Equals("REG_DWORD", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        return Convert.ToInt32(value.Data.Trim(), 16);
    }

    private static string Describe(int start, int delayed) => start switch {
        4 => "disabled",
        3 => "manual",
        _ => delayed == 1 ? "delayedAuto" : "auto",
    };

    [GeneratedRegex("0x([0-9A-Fa-f]+)")]
    private static partial Regex HexRegex();
}
