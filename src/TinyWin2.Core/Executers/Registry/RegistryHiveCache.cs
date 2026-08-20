using System.Text.RegularExpressions;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Registry;

/// <summary>One offline registry hive loaded under HKLM\&lt;sessionPrefix&gt;_&lt;hive&gt;.</summary>
public sealed class RegistryHive(
    string hiveId,
    string hiveKey) {
    public string HiveId { get; } = hiveId;

    /// <summary>Loaded key path, e.g. <c>HKLM\TinyWin2_build1_system</c>.</summary>
    public string HiveKey { get; } = hiveKey;
    public bool IsLoaded { get; internal set; }

    internal string KeyUnderHive(string keyPath) =>
        $"{HiveKey}\\{keyPath.Trim('\\')}";

    internal string ValueUnderHive(string keyPath, string valueName) =>
        $"{HiveId}\\{keyPath.Trim('\\')}\\{valueName}";
}

/// <summary>
/// Loads offline registry hives on demand (reg.exe load) and unloads them all with retry,
/// shared by every registry executer working against one mounted layer.
/// </summary>
public sealed class RegistryHiveCache(string mountPath, IProcessRunner? runner = null) {
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RegistryHive> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private string _sessionPrefix = "TinyWin2";

    public string MountPath { get; } = mountPath;
    public IProcessRunner Runner { get; } = runner ?? new ProcessRunner();

    /// <summary>Hive id → file path inside the image (v1 mapping + SECURITY/SAM).</summary>
    public static readonly IReadOnlyDictionary<string, string> HiveFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["software"] = @"Windows\System32\config\SOFTWARE",
            ["system"] = @"Windows\System32\config\SYSTEM",
            ["security"] = @"Windows\System32\config\SECURITY",
            ["sam"] = @"Windows\System32\config\SAM",
            ["default"] = @"Windows\System32\config\default",
            ["default-user"] = @"Users\Default\NTUSER.DAT",
        };

    public void SetSessionPrefix(string prefix) => _sessionPrefix = prefix;

    public async Task<RegistryHive> GetAsync(string hiveId, IBuildLog log, CancellationToken ct) {
        if (!HiveFiles.TryGetValue(hiveId, out var relativePath)) {
            throw new ExecException(
                $"unknown registry hive '{hiveId}' (expected one of: {string.Join(", ", HiveFiles.Keys)}).");
        }

        lock (_gate) {
            if (_loaded.TryGetValue(hiveId, out var hive) && hive.IsLoaded) {
                return hive;
            }
        }

        var hiveFilePath = Path.GetFullPath(Path.Combine(MountPath, relativePath));
        if (!File.Exists(hiveFilePath)) {
            throw new ExecException($"offline registry hive '{hiveId}' was not found at '{hiveFilePath}'.");
        }

        var hiveKey = $"HKLM\\{_sessionPrefix}_{hiveId.ToLowerInvariant()}";
        await Runner.RunAsync("reg.exe", ["load", hiveKey, hiveFilePath], cancellationToken: ct);
        log.Debug($"loaded offline hive '{hiveId}' at {hiveKey}");
        lock (_gate) {
            _loaded[hiveId] = new RegistryHive(hiveId.ToLowerInvariant(), hiveKey) { IsLoaded = true };
            return _loaded[hiveId];
        }
    }

    /// <summary>Unloads every loaded hive; retries because handles may lag behind a moment.</summary>
    public async Task UnloadAllAsync(IBuildLog log, CancellationToken ct) {
        List<RegistryHive> toUnload;
        lock (_gate) {
            toUnload = [.. _loaded.Values.Where(h => h.IsLoaded)];
            _loaded.Clear();
        }

        foreach (var hive in toUnload) {
            var unloaded = false;
            for (var attempt = 1; attempt <= 5 && !unloaded; attempt++) {
                try {
                    await Runner.RunAsync("reg.exe", ["unload", hive.HiveKey], cancellationToken: ct);
                    unloaded = true;
                }
                catch (ProcessRunnerException) {
                    if (attempt == 5) {
                        log.Warn(
                            $"could not unload offline hive '{hive.HiveId}' after {attempt} attempts; continuing.");
                    }
                    else {
                        await Task.Delay(200 * attempt, ct);
                    }
                }
            }

            hive.IsLoaded = false;
        }
    }
}

/// <summary>Parses and renders reg.exe query/add value representations.</summary>
public static partial class RegValues {
    public sealed record RegValue(string Type, string Data);

    /// <summary>Extracts a named value from <c>reg query KEY /v NAME</c> output; null if absent.</summary>
    public static RegValue? ParseQueryValue(string output, string valueName) {
        foreach (var rawLine in output.Split('\n')) {
            var line = rawLine.TrimEnd('\r');
            var match = ValueLine().Match(line);
            if (!match.Success) {
                continue;
            }

            var name = match.Groups[1].Value.Trim();
            if (string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase)) {
                return new RegValue(match.Groups[2].Value, match.Groups[3].Value);
            }
        }

        return null;
    }

    /// <summary>Renders desired data for reg.exe /d for each supported type.</summary>
    public static string RenderData(string type, System.Text.Json.Nodes.JsonNode? data) {
        switch (type) {
            case "REG_DWORD":
                return $"0x{ToLong(data!):x8}";
            case "REG_QWORD":
                return $"0x{ToLong(data!):x16}";
            case "REG_MULTI_SZ":
                var items = data as System.Text.Json.Nodes.JsonArray
                            ?? throw new ExecException("REG_MULTI_SZ data must be a JSON array of strings.");
                return string.Join("\\0", items.Select(i => i!.GetValue<string>()));
            case "REG_SZ":
            case "REG_EXPAND_SZ":
                return data!.GetValue<string>();
            default:
                throw new ExecException($"unsupported registry value type '{type}'.");
        }
    }

    /// <summary>JsonValue stores int/long/double depending on origin; accept any integral form.</summary>
    private static long ToLong(System.Text.Json.Nodes.JsonNode data) {
        if (data is System.Text.Json.Nodes.JsonValue value) {
            if (value.TryGetValue<int>(out var i)) {
                return i;
            }

            if (value.TryGetValue<long>(out var l)) {
                return l;
            }
        }

        return data.GetValue<long>();
    }

    /// <summary>Normalizes queried data to the rendered form for comparison.</summary>
    public static bool Equals(string type, string queriedData, string desiredData) {
        if (type is "REG_DWORD" or "REG_QWORD") {
            // reg.exe prints 0x-prefixed hex; compare numerically so 0x1 == 0x00000001.
            return TryParseHex(queriedData, out var queried)
                   && TryParseHex(desiredData, out var desired)
                   && queried == desired;
        }

        return string.Equals(queriedData.TrimEnd('\0'), desiredData, StringComparison.Ordinal);
    }

    private static bool TryParseHex(string value, out long number) {
        var trimmed = value.Trim().TrimStart("0x");
        return long.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    /// <summary>reg query value line: four-space separated name, type, data.</summary>
    [GeneratedRegex(@"^\s+(.+?)(?:\s{4})(REG_[A-Z_]+)(?:\s{4})(.*)$")]
    private static partial Regex ValueLine();
}
