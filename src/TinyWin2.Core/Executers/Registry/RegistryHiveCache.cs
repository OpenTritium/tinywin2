using System.Globalization;
using System.Text.Json.Nodes;
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

    /// <summary>Display path for change targets and logs: hive id (e.g. software\K\V), not the transient HKLM key.</summary>
    internal string DisplayValuePath(string keyPath, string valueName) =>
        $@"{HiveId}\{keyPath.Trim('\\')}\{valueName}";
}

/// <summary>
///     Loads offline registry hives on demand (reg.exe load) and unloads them all with retry,
///     shared by every registry executer working against one mounted layer.
/// </summary>
public sealed class RegistryHiveCache(string mountPath, IProcessRunner runner) {
    /// <summary>Hive id → file path inside the image, including SECURITY/SAM.</summary>
    public static readonly IReadOnlyDictionary<string, string> HiveFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["software"] = @"Windows\System32\config\SOFTWARE",
            ["system"] = @"Windows\System32\config\SYSTEM",
            ["security"] = @"Windows\System32\config\SECURITY",
            ["sam"] = @"Windows\System32\config\SAM",
            ["default"] = @"Windows\System32\config\default",
            ["default-user"] = @"Users\Default\NTUSER.DAT"
        };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, RegistryHive> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<RegistryHive>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private string _sessionPrefix = "TinyWin2";

    private string MountPath { get; } = mountPath;
    private IProcessRunner Runner { get; } = runner;

    public void SetSessionPrefix(string prefix) => _sessionPrefix = prefix;

    public Task<RegistryHive> GetAsync(string hiveId, BuildLog log, CancellationToken ct) {
        if (!HiveFiles.TryGetValue(hiveId, out var relativePath)) {
            throw new ExecException(
                $"unknown registry hive '{hiveId}' (expected one of: {string.Join(", ", HiveFiles.Keys)}).");
        }

        lock (_gate) {
            if (_loaded.TryGetValue(hiveId, out var hive) && hive.IsLoaded) {
                return Task.FromResult(hive);
            }

            // Serialize concurrent loads of the same hive: two callers would otherwise both
            // run `reg.exe load` and the second would fail on the existing mount point.
            if (!_inFlight.TryGetValue(hiveId, out var load)) {
                load = LoadAsync(hiveId, relativePath, log, ct);
                _inFlight[hiveId] = load;
            }

            return load;
        }
    }

    private async Task<RegistryHive> LoadAsync(string hiveId, string relativePath, BuildLog log, CancellationToken ct) {
        try {
            var hiveFilePath = Path.GetFullPath(Path.Combine(MountPath, relativePath));
            if (!File.Exists(hiveFilePath)) {
                throw new ExecException($"offline registry hive '{hiveId}' was not found at '{hiveFilePath}'.");
            }

            var hiveKey = $"HKLM\\{_sessionPrefix}_{hiveId.ToLowerInvariant()}";
            await Runner.RunAsync("reg.exe", ["load", hiveKey, hiveFilePath], cancellationToken: ct);
            log.Debug($"loaded offline hive '{hiveId}' at {hiveKey}");
            var hive = new RegistryHive(hiveId.ToLowerInvariant(), hiveKey) { IsLoaded = true };
            lock (_gate) {
                _loaded[hiveId] = hive;
            }

            return hive;
        }
        finally {
            lock (_gate) {
                _inFlight.Remove(hiveId);
            }
        }
    }

    /// <summary>Unloads every loaded hive; retries because handles may lag behind a moment.</summary>
    public async Task UnloadAllAsync(BuildLog log, CancellationToken ct) {
        List<RegistryHive> toUnload;
        lock (_gate) {
            toUnload = [.. _loaded.Values.Where(h => h.IsLoaded)];
        }

        List<Exception>? failures = null;
        foreach (var hive in toUnload) {
            try {
                await UnloadWithRetryAsync(Runner, hive.HiveKey, hive.HiveId, log, ct);
                hive.IsLoaded = false;
                lock (_gate) {
                    _loaded.Remove(hive.HiveId);
                }
            }
            catch (Exception ex) when (ex is ProcessRunnerException or ExecException) {
                failures ??= [];
                failures.Add(ex);
            }
        }

        if (failures is not null) {
            throw new AggregateException("one or more offline registry hives could not be unloaded", failures);
        }
    }

    /// <summary>reg.exe unload lags behind handle release: linear-backoff retries, then fail visibly.</summary>
    internal static async Task UnloadWithRetryAsync(
        IProcessRunner runner,
        string hiveKey,
        string what,
        BuildLog log,
        CancellationToken ct) {
        for (var attempt = 1; ; attempt++) {
            try {
                await runner.RunAsync("reg.exe", ["unload", hiveKey], cancellationToken: ct);
                return;
            }
            catch (ProcessRunnerException) when (attempt < 5) {
                await Task.Delay(200 * attempt, ct);
            }
            catch (ProcessRunnerException) {
                log.Error($"could not unload offline hive '{what}' after {attempt} attempts.");
                throw;
            }
        }
    }
}

/// <summary>Parses and renders reg.exe query/add value representations.</summary>
public static partial class RegValues {
    /// <summary>Extracts a named value from <c>reg query KEY /v NAME</c> output; null if absent.</summary>
    public static RegValue? ParseQueryValue(string output, string valueName) {
        return (from entry in RegQuery.Parse(output)
                from value in entry.Values
                where string.Equals(value.Name, valueName, StringComparison.OrdinalIgnoreCase)
                select new RegValue(value.Type, value.Data)).FirstOrDefault();
    }

    /// <summary>Renders desired data for reg.exe /d for each supported type.</summary>
    public static string RenderData(string type, JsonNode? data) {
        switch (type) {
            case "REG_DWORD":
                var dword = ToUnsignedLong(data, type);
                return dword <= uint.MaxValue
                    ? $"0x{dword:x8}"
                    : throw new ExecException("REG_DWORD data must be an unsigned 32-bit integer.");
            case "REG_QWORD":
                return $"0x{ToUnsignedLong(data, type):x16}";
            case "REG_MULTI_SZ":
                var items = data as JsonArray
                            ?? throw new ExecException("REG_MULTI_SZ data must be a JSON array of strings.");
                return string.Join("\\0", items.Select(i => i?.GetValue<string>()
                                                            ?? throw new ExecException(
                                                                "REG_MULTI_SZ data must contain only strings.")));
            case "REG_SZ":
            case "REG_EXPAND_SZ":
                return data!.GetValue<string>();
            case "REG_BINARY":
                var hex = data?.GetValue<string>()
                          ?? throw new ExecException("REG_BINARY data must be a hexadecimal string.");
                hex = HexNoise().Replace(hex, "");
                if (hex.Length == 0 || hex.Length % 2 != 0
                    || !hex.All(Uri.IsHexDigit)) {
                    throw new ExecException("REG_BINARY data must contain an even number of hexadecimal digits.");
                }

                return hex.ToUpperInvariant();
            default:
                throw new ExecException($"unsupported registry value type '{type}'.");
        }
    }

    /// <summary>Accepts non-negative JSON integers for DWORD/QWORD values.</summary>
    private static ulong ToUnsignedLong(JsonNode? data, string type) {
        if (data is not JsonValue value) {
            throw new ExecException($"{type} data must be a non-negative integer.");
        }

        if (value.TryGetValue<int>(out var integer) && integer >= 0) {
            return (ulong)integer;
        }

        if (value.TryGetValue<uint>(out var unsignedInteger)) {
            return unsignedInteger;
        }

        if (value.TryGetValue<ulong>(out var unsigned)) {
            return unsigned;
        }

        if (value.TryGetValue<long>(out var signed) && signed >= 0) {
            return (ulong)signed;
        }

        throw new ExecException($"{type} data must be a non-negative integer.");
    }

    /// <summary>Normalizes queried data to the rendered form for comparison.</summary>
    public static bool Equals(string type, string queriedData, string desiredData) {
        if (type is "REG_DWORD" or "REG_QWORD") {
            // reg.exe prints 0x-prefixed hex; compare numerically so 0x1 == 0x00000001.
            return TryParseHex(queriedData, out var queried)
                   && TryParseHex(desiredData, out var desired)
                   && queried == desired;
        }

        if (type == "REG_MULTI_SZ") {
            return string.Equals(NormalizeMultiString(queriedData), NormalizeMultiString(desiredData),
                StringComparison.Ordinal);
        }

        if (type == "REG_BINARY") {
            static string NormalizeBinary(string value) =>
                HexNoise().Replace(value, "").ToUpperInvariant();

            return string.Equals(NormalizeBinary(queriedData), NormalizeBinary(desiredData),
                StringComparison.Ordinal);
        }

        return string.Equals(queriedData.TrimEnd('\0'), desiredData, StringComparison.Ordinal);
    }

    private static string NormalizeMultiString(string value) {
        var normalized = value.TrimEnd('\0');
        while (normalized.EndsWith(@"\0", StringComparison.Ordinal)) {
            normalized = normalized[..^2];
        }

        return normalized;
    }

    internal static bool TryParseDword(string value, out int number) {
        number = 0;
        if (!TryParseHex(value, out var parsed) || parsed > int.MaxValue) {
            return false;
        }

        number = (int)parsed;
        return true;
    }

    private static bool TryParseHex(string value, out ulong number) {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
            trimmed = trimmed[2..];
        }

        return ulong.TryParse(trimmed, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out number);
    }

    /// <summary>Strips whitespace, colons, and hyphens from hexadecimal strings.</summary>
    [GeneratedRegex(@"[\s:-]", RegexOptions.CultureInvariant)]
    private static partial Regex HexNoise();

    public sealed record RegValue(string Type, string Data);
}
