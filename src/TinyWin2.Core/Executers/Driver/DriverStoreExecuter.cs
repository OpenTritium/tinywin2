using System.Text.RegularExpressions;
using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Executers.Driver;

/// <summary>
///     Converges offline third-party driver packages through DISM. The requested INF
///     names match <c>Original File Name</c>; removal uses DISM's <c>Published Name</c>
///     so the driver store metadata remains consistent.
/// </summary>
public sealed partial class DriverStoreExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    private const string ResourceId = "driver.store";
    private readonly IProcessRunner _runner = runner;

    public string Resource => ResourceId;

    public void Validate(OperationSpec spec) {
        if (spec.Action != OperationAction.Remove) {
            throw new ExecException($"{ResourceId} supports only action 'remove'.");
        }

        _ = DriverStoreOptions.FromDesired(spec.Spec);
    }

    public async Task<ResourceDiff> InspectAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = await InspectCoreAsync(context, spec, ct);
        return new(changes.All(c => c.Change.Kind == ChangeKind.Skipped),
            [.. changes.Select(c => c.Change)]);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, OperationSpec spec, CancellationToken ct) {
        Validate(spec);
        var changes = await InspectCoreAsync(context, spec, ct);
        if (changes.All(c => c.Change.Kind == ChangeKind.Skipped)) {
            return ExecResult.Skipped("no removable third-party Driver Store packages",
                [.. changes.Select(c => c.Change)]);
        }

        var applied = changes.Where(c => c.Change.Kind == ChangeKind.Skipped)
            .Select(c => c.Change)
            .ToList();
        foreach (var change in changes.Where(c => c.Change.Kind != ChangeKind.Skipped)) {
            if (change.Package is not null) {
                await RemoveUnusedInboxPackageAsync(context, change.Package, ct);
                applied.Add(change.Change);
                continue;
            }

            var publishedName = change.PublishedName
                                ?? throw new ExecException(
                                    $"driver target '{change.Change.Target}' has no published name.");
            var (exitCode, output) = await RunDismAsync(context,
                ["/Remove-Driver", $"/Driver:{publishedName}"], ct);
            var outcome = DismErrors.Classify(exitCode, output);
            if (outcome is DismOutcome.Success or DismOutcome.SuccessRebootRequired) {
                context.Log.Info($"{ResourceId}: {change.Change.Target} removed");
                applied.Add(change.Change);
            }
            else {
                throw new ExecException(
                    $"dism.exe failed to remove driver '{change.Change.Target}' (exit {exitCode}).");
            }
        }

        return ExecResult.Applied(applied);
    }

    private async Task<List<DriverChange>> InspectCoreAsync(
        ExecContext context, OperationSpec spec, CancellationToken ct) {
        var options = DriverStoreOptions.FromDesired(spec.Spec);
        var (exitCode, output) = await RunDismAsync(context,
            ["/Get-Drivers", "/All", "/Format:List"], ct);
        var outcome = DismErrors.Classify(exitCode, output);
        if (outcome is not (DismOutcome.Success or DismOutcome.SuccessRebootRequired)) {
            throw new ExecException($"dism.exe failed to list driver targets (exit {exitCode}).");
        }

        var records = DismListParser.Parse(output, "Published Name");
        if (records.Count == 0) {
            throw new ExecException(
                $"dism.exe returned no driver records despite a successful listing (exit {exitCode}).");
        }

        var byOriginalName = records
            .Where(r => DismListParser.Get(r, "Original File Name") is not null)
            .GroupBy(r => DismListParser.Get(r, "Original File Name")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var controlSets = options.ForceUnusedInbox
            ? await ReadControlSetsAsync(context, ct)
            : [];
        var enumReferences = options.ForceUnusedInbox
            ? await ReadOfflineEnumServicesAsync(context, controlSets, ct)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<DriverChange>();
        foreach (var infName in options.InfNames) {
            if (!byOriginalName.TryGetValue(infName, out var matches)) {
                changes.Add(new(new(ChangeKind.Skipped, infName, "not in DISM driver list")));
                continue;
            }

            foreach (var record in matches) {
                var publishedName = DismListParser.Get(record, "Published Name");
                if (publishedName is null) {
                    changes.Add(new(new(ChangeKind.Skipped, infName, "driver record has no published name")));
                    continue;
                }

                var inbox = DismListParser.Get(record, "Inbox");
                if (string.Equals(inbox, "Yes", StringComparison.OrdinalIgnoreCase)) {
                    if (!options.ForceUnusedInbox) {
                        context.Log.Info(
                            $"driver '{infName}' is an inbox package; DISM cannot remove it, skipping.");
                        changes.Add(new(new(ChangeKind.Skipped, infName,
                            "inbox driver packages cannot be removed by DISM; set forceUnusedInbox to opt in")));
                        continue;
                    }

                    var package = await InboxDriverPackage.InspectAsync(
                        context, _runner, infName, options.InfNames, controlSets, enumReferences, ct);
                    if (package is null) {
                        changes.Add(new(new(ChangeKind.Skipped, infName,
                            "inbox driver package files or DriverDatabase metadata were not found")));
                        continue;
                    }

                    if (package.ActiveServices.Count > 0) {
                        changes.Add(new(new(ChangeKind.Skipped, infName,
                            "inbox driver has offline device references through service(s): " +
                            string.Join(", ", package.ActiveServices))));
                        continue;
                    }

                    changes.Add(new(
                        new(ChangeKind.Removed, infName,
                            "inbox package; forceUnusedInbox enabled and no offline device references",
                            "package files, service registration, and DriverDatabase metadata removed"),
                        Package: package));
                    continue;
                }

                changes.Add(new(
                    new(ChangeKind.Removed, infName, publishedName), publishedName));
            }
        }

        return changes;
    }

    private async Task<IReadOnlyList<string>> ReadControlSetsAsync(
        ExecContext context, CancellationToken ct) {
        var system = await context.Hives.GetAsync("system", context.Log, ct);
        var result = await _runner.RunAsync("reg.exe", ["query", system.HiveKey],
            new() { IgnoreExitCode = true }, ct);
        if (!result.Success) {
            throw new ExecException(
                "could not enumerate offline SYSTEM control sets; refusing forceUnusedInbox removal.");
        }

        var prefix = system.HiveKey + "\\";
        var controlSets = result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Select(line => line.Replace("HKEY_LOCAL_MACHINE\\", "HKLM\\",
                StringComparison.OrdinalIgnoreCase))
            .Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(line => line[prefix.Length..])
            .Where(name => name.Length == 13
                           && name.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase)
                           && name[10..].All(char.IsAsciiDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (controlSets.Count == 0) {
            throw new ExecException(
                "offline SYSTEM hive has no discoverable ControlSet### keys; refusing forceUnusedInbox removal.");
        }

        context.Log.Debug($"driver.store: discovered control sets {string.Join(", ", controlSets)}");
        return controlSets;
    }

    private async Task<HashSet<string>> ReadOfflineEnumServicesAsync(
        ExecContext context, IReadOnlyList<string> controlSets, CancellationToken ct) {
        var system = await context.Hives.GetAsync("system", context.Log, ct);
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var controlSet in controlSets) {
            var result = await _runner.RunAsync("reg.exe", [
                "query", $@"{system.HiveKey}\{controlSet}\Enum", "/s", "/v", "Service"
            ], new() { IgnoreExitCode = true }, ct);
            if (!result.Success && result.ExitCode != 1) {
                throw new ProcessRunnerException("reg.exe", result);
            }

            services.UnionWith(InboxDriverPackage.ReadNamedValues(result.Output + result.Error, "Service"));
        }

        context.Log.Debug($"driver.store: found {services.Count} offline device service reference(s)");
        return services;
    }

    private async Task RemoveUnusedInboxPackageAsync(
        ExecContext context, InboxDriverPackage package, CancellationToken ct) {
        var system = await context.Hives.GetAsync("system", context.Log, ct);
        foreach (var service in package.Services.Where(service => service.Owners.Count > 0
                                                                  && service.Owners.All(owner => package.TargetInfNames
                                                                      .Contains(owner,
                                                                          StringComparer.OrdinalIgnoreCase)))) {
            foreach (var controlSet in package.ControlSets) {
                await DeleteKeyWithAclRescueAsync(
                    $@"{system.HiveKey}\{controlSet}\Services\{service.Name}", ct);
                await DeleteKeyWithAclRescueAsync(
                    $@"{system.HiveKey}\{controlSet}\Services\EventLog\System\{service.Name}", ct);
            }

            foreach (var imagePath in service.ImagePaths) {
                var driverPath = Path.Combine(context.MountPath, "Windows", "System32", "drivers", imagePath);
                await DeletePathIfPresentAsync(driverPath, ct);
            }
        }

        foreach (var driverFile in package.DriverFileNames) {
            await DeletePathIfPresentAsync(
                Path.Combine(context.MountPath, "Windows", "System32", "drivers", driverFile), ct);
        }

        foreach (var key in package.DeviceIdKeys) {
            await DeleteValueWithAclRescueAsync(
                $@"{system.HiveKey}\DriverDatabase\DeviceIds\{key}", package.InfName, ct);
        }

        await DeleteKeyWithAclRescueAsync(
            $@"{system.HiveKey}\DriverDatabase\DriverInfFiles\{package.InfName}", ct);
        await DeleteKeyWithAclRescueAsync(
            $@"{system.HiveKey}\DriverDatabase\DriverPackages\{package.PackageDirectoryName}", ct);

        await DeletePathIfPresentAsync(
            Path.Combine(context.MountPath, "Windows", "INF", package.InfName), ct);
        await DeletePathIfPresentAsync(Path.Combine(context.MountPath, "Windows", "INF",
            Path.ChangeExtension(package.InfName, ".PNF")), ct);
        await DeletePackageFilesAsync(context, package.PackageDirectoryPath, ct);

        context.Log.Warn(
            $"driver.store: force-removed unused inbox package '{package.InfName}' " +
            "after confirming no offline device references");
    }

    private async Task DeletePackageFilesAsync(
        ExecContext context, string packageDirectoryPath, CancellationToken ct) {
        if (!Directory.Exists(packageDirectoryPath)) {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(packageDirectoryPath, "*", SearchOption.AllDirectories)) {
            await DeletePathIfPresentAsync(file, ct);
        }

        try {
            Directory.Delete(packageDirectoryPath, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) {
            // DriverStore's parent is TrustedInstaller-owned and may deny DELETE_CHILD even
            // after the package files themselves were ACL-rescued. An empty package directory
            // is inert: DISM and DriverDatabase no longer index it as an installed package.
            context.Log.Debug(
                $"driver.store: retained empty DriverStore directory '{packageDirectoryPath}' " +
                "because its parent denies directory deletion");
        }
    }

    private async Task DeleteKeyWithAclRescueAsync(string key, CancellationToken ct) {
        var result = await _runner.RunAsync("reg.exe", ["delete", key, "/f"],
            new() { IgnoreExitCode = true }, ct);
        if (result.Success || result.ExitCode == 1) {
            return;
        }

        await RegistryAcl.RescueAsync(_runner, key, ct);
        result = await _runner.RunAsync("reg.exe", ["delete", key, "/f"],
            new() { IgnoreExitCode = true }, ct);
        if (!result.Success && result.ExitCode != 1) {
            throw new ProcessRunnerException("reg.exe", result);
        }
    }

    private async Task DeleteValueWithAclRescueAsync(string key, string value, CancellationToken ct) {
        var result = await _runner.RunAsync("reg.exe", ["delete", key, "/v", value, "/f"],
            new() { IgnoreExitCode = true }, ct);
        if (result.Success || result.ExitCode == 1) {
            return;
        }

        await RegistryAcl.RescueAsync(_runner, key, ct);
        result = await _runner.RunAsync("reg.exe", ["delete", key, "/v", value, "/f"],
            new() { IgnoreExitCode = true }, ct);
        if (!result.Success && result.ExitCode != 1) {
            throw new ProcessRunnerException("reg.exe", result);
        }
    }

    private async Task DeletePathIfPresentAsync(string path, CancellationToken ct) {
        if (!File.Exists(path) && !Directory.Exists(path)) {
            return;
        }

        await ImageFs.DeleteWithRescueAsync(_runner, path, ct);
    }

    private sealed record DriverChange(
        ChangeItem Change,
        string? PublishedName = null,
        InboxDriverPackage? Package = null);

    private sealed partial class InboxDriverPackage {
        [GeneratedRegex(@"^(?:HKEY_LOCAL_MACHINE|HKLM)\\.+$", RegexOptions.IgnoreCase)]
        private static partial Regex RegistryKeyLine();

        [GeneratedRegex(@"^\s*(?<name>[^\s].*?)\s{2,}(?<type>REG_[A-Z_]+)\s{2,}(?<data>.*)$",
            RegexOptions.IgnoreCase)]
        private static partial Regex ValueLine();

        public required string InfName { get; init; }
        public required string PackageDirectoryName { get; init; }
        public required string PackageDirectoryPath { get; init; }
        public required IReadOnlyList<string> TargetInfNames { get; init; }
        public required IReadOnlyList<string> ControlSets { get; init; }
        public required IReadOnlyList<DriverService> Services { get; init; }
        public required IReadOnlyList<string> DriverFileNames { get; init; }
        public required IReadOnlyList<string> DeviceIdKeys { get; init; }
        public required IReadOnlyList<string> ActiveServices { get; init; }

        public static async Task<InboxDriverPackage?> InspectAsync(
            ExecContext context,
            IProcessRunner runner,
            string infName,
            IReadOnlyList<string> targetInfNames,
            IReadOnlyList<string> controlSets,
            IReadOnlySet<string> enumReferences,
            CancellationToken ct) {
            var storeRoot = Path.Combine(context.MountPath, "Windows", "System32", "DriverStore",
                "FileRepository");
            var packageDirectory = Directory.Exists(storeRoot)
                ? Directory.EnumerateDirectories(storeRoot, infName + "_*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path => File.Exists(Path.Combine(path, infName)))
                : null;
            if (packageDirectory is null) {
                return null;
            }

            var driverFileNames = Directory.EnumerateFiles(packageDirectory, "*.sys")
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .Where(IsSafeDriverFileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var system = await context.Hives.GetAsync("system", context.Log, ct);
            var packageKey = $"{system.HiveKey}\\DriverDatabase\\DriverPackages\\{Path.GetFileName(packageDirectory)}";
            var packageResult = await QueryAsync(runner, packageKey + "\\Configurations", ct);
            if (!packageResult.Success && packageResult.ExitCode != 1) {
                throw new ProcessRunnerException("reg.exe", packageResult);
            }

            var serviceNames = ReadNamedValues(packageResult.Output + packageResult.Error, "Service")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Some inbox packages register an auxiliary boot driver (for example,
            // nvstor) through its Owners value without listing it in the package
            // configuration's Service value. Include those services before cleanup.
            foreach (var controlSet in controlSets) {
                var ownedServices = await QueryAsync(runner,
                    $"{system.HiveKey}\\{controlSet}\\Services", ct,
                    "/s", "/f", infName, "/d");
                if (!ownedServices.Success && ownedServices.ExitCode != 1) {
                    throw new ProcessRunnerException("reg.exe", ownedServices);
                }

                serviceNames.AddRange(ReadDirectServiceNames(
                    ownedServices.Output + ownedServices.Error,
                    $@"{system.HiveKey}\{controlSet}\Services"));
            }

            serviceNames = [
                .. serviceNames
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            ];
            var services = new List<DriverService>();
            foreach (var serviceName in serviceNames) {
                var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var imagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var controlSet in controlSets) {
                    var serviceKey = $"{system.HiveKey}\\{controlSet}\\Services\\{serviceName}";
                    var serviceResult = await QueryAsync(runner, serviceKey, ct);
                    if (!serviceResult.Success && serviceResult.ExitCode != 1) {
                        throw new ProcessRunnerException("reg.exe", serviceResult);
                    }

                    if (!serviceResult.Success) {
                        continue;
                    }

                    owners.UnionWith(ReadNamedValues(serviceResult.Output + serviceResult.Error, "Owners")
                        .SelectMany(SplitMultiString));
                    var serviceOutput = serviceResult.Output + serviceResult.Error;
                    foreach (var imagePath in ReadNamedValues(serviceOutput,
                                 "ImagePath").SelectMany(SplitMultiString)) {
                        var fileName = Path.GetFileName(imagePath.Replace('/', '\\'));
                        if (IsSafeDriverFileName(fileName)) {
                            imagePaths.Add(fileName);
                        }
                    }
                }

                services.Add(new(serviceName, owners, imagePaths));
            }

            var deviceIdRoot = $"{system.HiveKey}\\DriverDatabase\\DeviceIds";
            var deviceIds = await QueryAsync(runner, deviceIdRoot, ct, "/s", "/f", infName, "/d");
            if (!deviceIds.Success && deviceIds.ExitCode != 1) {
                throw new ProcessRunnerException("reg.exe", deviceIds);
            }

            var deviceIdKeys = ReadKeysContainingValue(deviceIds.Output + deviceIds.Error, infName);
            var activeServices = services.Where(service => enumReferences.Contains(service.Name))
                .Select(service => service.Name)
                .ToList();

            return new() {
                InfName = infName,
                PackageDirectoryName = Path.GetFileName(packageDirectory),
                PackageDirectoryPath = packageDirectory,
                TargetInfNames = targetInfNames,
                ControlSets = controlSets,
                Services = services,
                DriverFileNames = driverFileNames,
                DeviceIdKeys = deviceIdKeys,
                ActiveServices = activeServices
            };
        }

        private static async Task<ProcessRunResult> QueryAsync(
            IProcessRunner runner,
            string key,
            CancellationToken ct,
            params string[] suffix) {
            var args = new List<string> { "query", key };
            args.AddRange(suffix);
            return await runner.RunAsync("reg.exe", args, new() { IgnoreExitCode = true }, ct);
        }

        public static IEnumerable<string> ReadNamedValues(string output, string name) {
            foreach (var line in output.Split('\n')) {
                var match = ValueLine().Match(line.TrimEnd('\r'));
                if (match.Success && string.Equals(match.Groups["name"].Value.Trim(), name,
                        StringComparison.OrdinalIgnoreCase)) {
                    yield return match.Groups["data"].Value.Trim();
                }
            }
        }

        private static IEnumerable<string> ReadDirectServiceNames(string output, string servicesRoot) {
            var prefix = servicesRoot + "\\";
            foreach (var rawLine in output.Split('\n')) {
                var key = rawLine.Trim();
                key = key.Replace("HKEY_LOCAL_MACHINE\\", "HKLM\\",
                    StringComparison.OrdinalIgnoreCase);
                if (!RegistryKeyLine().IsMatch(key)
                    || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                var suffix = key[prefix.Length..];
                if (suffix.Length > 0 && !suffix.Contains('\\')) {
                    yield return suffix;
                }
            }
        }

        private static IReadOnlyList<string> ReadKeysContainingValue(string output, string valueName) {
            var keys = new List<string>();
            string? current = null;
            foreach (var rawLine in output.Split('\n')) {
                var line = rawLine.TrimEnd('\r');
                if (RegistryKeyLine().IsMatch(line.Trim())) {
                    current = line.Trim();
                    continue;
                }

                var match = ValueLine().Match(line);
                if (current is not null && match.Success
                                        && string.Equals(match.Groups["name"].Value.Trim(), valueName,
                                            StringComparison.OrdinalIgnoreCase)) {
                    keys.Add(current);
                }
            }

            return keys;
        }

        private static IEnumerable<string> SplitMultiString(string value) =>
            value.Split(["\\0", "\0", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim().Trim('"'))
                .Where(part => part.Length > 0);

        private static bool IsSafeDriverFileName(string fileName) =>
            fileName.Length > 0
            && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            && !fileName.Contains('*')
            && !fileName.Contains('?')
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private sealed record DriverService(
        string Name,
        IReadOnlySet<string> Owners,
        IReadOnlySet<string> ImagePaths);
}
