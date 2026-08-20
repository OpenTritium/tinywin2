using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>
/// Converges offline service start modes. Unifies v1's DisableOfflineService and
/// ConfigureOfflineService: every start mode is present-with-a-value.
/// Desired: <c>{ services:[...], servicePatterns:[...], start: auto|delayedAuto|manual|disabled }</c>.
/// </summary>
public sealed partial class RegistryServiceExecuter(IProcessRunner runner) : IExecuter
{
    public const string ResourceId = "registry.service";
    public string Resource => ResourceId;

    private static readonly IReadOnlyDictionary<string, int> StartValues = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["disabled"] = 4,
        ["manual"] = 3,
        ["auto"] = 2,
        ["delayedAuto"] = 2,
    };

    public async Task<ResourceDiff> InspectAsync(ExecContext context, ExecSpec spec, CancellationToken ct)
    {
        var (services, patterns, start) = Parse(spec);
        var hive = await context.Hives.GetAsync("system", context.Log, ct);
        var controlSet = await ResolveControlSetAsync(hive, ct);
        var servicesRoot = $"{hive.HiveKey}\\{controlSet}\\Services";

        // Enumerate service key names once; patterns match against them (v1 wildcard semantics).
        var enumerated = await runner.RunAsync("reg.exe", ["query", servicesRoot],
            new ProcessRunOptions { IgnoreExitCode = true }, ct);
        var allNames = enumerated.Output.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.StartsWith(servicesRoot + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[(servicesRoot.Length + 1)..])
            .Where(n => !n.Contains('\\'))
            .ToList();

        var resolved = new List<string>(services);
        foreach (var pattern in patterns)
        {
            var regex = LikeToRegex(pattern);
            var matches = allNames.Where(n => regex.IsMatch(n)).ToList();
            if (matches.Count == 0)
            {
                context.Log.Warn($"no services matched pattern '{pattern}' in {controlSet}; skipping.");
            }
            resolved.AddRange(matches);
        }

        var differences = new List<ChangeItem>();
        var desiredStart = StartValues[start];
        var delayed = string.Equals(start, "delayedAuto", StringComparison.OrdinalIgnoreCase);

        foreach (var service in resolved.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var serviceKey = $"{servicesRoot}\\{service}";
            var existingStart = await ReadDwordAsync(serviceKey, "Start", ct);
            if (existingStart is null)
            {
                context.Log.Warn($"service '{service}' was not present in {controlSet}; skipping.");
                differences.Add(new ChangeItem(ChangeKind.Skipped, service, SkipReasonValue()));
                continue;
            }

            var existingDelayed = await ReadDwordAsync(serviceKey, "DelayedAutoStart", ct) ?? 0;
            var satisfied = existingStart == desiredStart && existingDelayed == (delayed ? 1 : 0);
            if (!satisfied)
            {
                differences.Add(new ChangeItem(ChangeKind.Modified, service,
                    Before: Describe(existingStart.Value, existingDelayed),
                    After: Describe(desiredStart, delayed ? 1 : 0)));
            }
        }

        return new ResourceDiff(differences.All(d => d.Kind == ChangeKind.Skipped), differences);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, ExecSpec spec, CancellationToken ct)
    {
        var diff = await InspectAsync(context, spec, ct);
        if (diff.Satisfied)
        {
            return ExecResult.Skipped("services already in the desired start mode",
                diff.Differences.Where(d => d.Kind == ChangeKind.Skipped).ToArray());
        }

        var (_, _, start) = Parse(spec);
        var hive = await context.Hives.GetAsync("system", context.Log, ct);
        var controlSet = await ResolveControlSetAsync(hive, ct);
        var desiredStart = StartValues[start];
        var delayed = string.Equals(start, "delayedAuto", StringComparison.OrdinalIgnoreCase);
        var applied = new List<ChangeItem>();

        foreach (var change in diff.Differences)
        {
            if (change.Kind == ChangeKind.Skipped)
            {
                continue;
            }
            var serviceKey = $"{hive.HiveKey}\\{controlSet}\\Services\\{change.Target}";
            await runner.RunAsync("reg.exe", ["add", serviceKey, "/v", "Start", "/t", "REG_DWORD", "/d", desiredStart.ToString(), "/f"], cancellationToken: ct);
            await runner.RunAsync("reg.exe", ["add", serviceKey, "/v", "DelayedAutoStart", "/t", "REG_DWORD", "/d", (delayed ? 1 : 0).ToString(), "/f"], cancellationToken: ct);
            context.Log.Info($"service {change.Target} → {change.After}");
            applied.Add(change);
        }

        return ExecResult.Applied(applied);
    }

    private async Task<string> ResolveControlSetAsync(RegistryHive hive, CancellationToken ct)
    {
        var result = await runner.RunAsync("reg.exe", ["query", $"{hive.HiveKey}\\Select", "/v", "Current"],
            new ProcessRunOptions { IgnoreExitCode = true }, ct);
        var match = Regex.Match(result.Output, "0x([0-9A-Fa-f]+)");
        if (result.ExitCode != 0 || !match.Success)
        {
            throw new ExecException("could not resolve the active control set from the offline SYSTEM hive.");
        }
        return $"ControlSet{Convert.ToInt32(match.Groups[1].Value, 16):D3}";
    }

    private async Task<int?> ReadDwordAsync(string keyPath, string valueName, CancellationToken ct)
    {
        var result = await runner.RunAsync("reg.exe", ["query", keyPath, "/v", valueName],
            new ProcessRunOptions { IgnoreExitCode = true }, ct);
        if (result.ExitCode != 0)
        {
            return null;
        }
        var value = RegValues.ParseQueryValue(result.Output, valueName);
        if (value is null || !value.Type.Equals("REG_DWORD", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Convert.ToInt32(value.Data.Trim(), 16);
    }

    private static (List<string> Services, List<string> Patterns, string Start) Parse(ExecSpec spec)
    {
        var desired = spec.Desired;
        if (desired["start"] is not JsonValue startValue || !startValue.TryGetValue<string>(out var start)
            || !StartValues.ContainsKey(start))
        {
            throw new ExecException("registry.service requires 'start' (auto|delayedAuto|manual|disabled).");
        }

        var services = desired["services"]?.AsArray().OfType<JsonValue>().Select(v => v.GetValue<string>()).ToList() ?? [];
        var patterns = desired["servicePatterns"]?.AsArray().OfType<JsonValue>().Select(v => v.GetValue<string>()).ToList() ?? [];
        if (services.Count == 0 && patterns.Count == 0)
        {
            throw new ExecException("registry.service requires 'services' or 'servicePatterns'.");
        }
        foreach (var name in services.Concat(patterns))
        {
            if (!ServiceNamePattern().IsMatch(name))
            {
                throw new ExecException($"invalid service name or pattern '{name}'.");
            }
        }
        return (services, patterns, start);
    }

    private static string Describe(int start, int delayed) => start switch
    {
        4 => "disabled",
        3 => "manual",
        _ => delayed == 1 ? "delayedAuto" : "auto",
    };

    private static string SkipReasonValue() => "service not present";

    /// <summary>PowerShell -like wildcards (* and ?) anchored for full-string matching.</summary>
    internal static Regex LikeToRegex(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^[A-Za-z0-9_.?*-]+$")]
    private static partial Regex ServiceNamePattern();
}
