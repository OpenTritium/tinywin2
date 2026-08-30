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
public sealed class DriverStoreExecuter(IProcessRunner runner) : DismExecuterBase(runner), IExecuter {
    private const string ResourceId = "driver.store";

    public string Resource => ResourceId;

    public object Bind(OperationSpec spec) {
        if (spec.Action != OperationAction.Remove) {
            throw new ExecException($"{ResourceId} supports only action 'remove'.");
        }

        return DriverStoreOptions.FromDesired(spec.Spec);
    }

    public async Task<ResourceDiff> InspectAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, operation, ct);
        return new(changes.All(c => c.Change.Kind == ChangeKind.Skipped),
            [.. changes.Select(c => c.Change)]);
    }

    public async Task<ExecResult> ApplyAsync(ExecContext context, BoundOperation operation, CancellationToken ct) {
        var changes = await InspectCoreAsync(context, operation, ct);
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
        ExecContext context, BoundOperation operation, CancellationToken ct) {
        var options = (DriverStoreOptions)operation.Options;
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
                        context, Runner, infName, options.InfNames, controlSets, enumReferences, ct);
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
        var result = await OfflineReg.QueryAsync(Runner, system.HiveKey, ct);
        if (!result.Success) {
            throw new ExecException(
                "could not enumerate offline SYSTEM control sets; refusing forceUnusedInbox removal.");
        }

        var controlSets = RegQuery.DirectChildKeys(result.Output, system.HiveKey)
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
            var result = await OfflineReg.QueryAsync(Runner,
                $@"{system.HiveKey}\{controlSet}\Enum", ct, "/s", "/v", "Service");
            services.UnionWith(RegQuery.NamedValues(result.Output + result.Error, "Service"));
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
                await OfflineReg.DeleteKeyAsync(Runner,
                    $@"{system.HiveKey}\{controlSet}\Services\{service.Name}", system.HiveKey, ct);
                await OfflineReg.DeleteKeyAsync(Runner,
                    $@"{system.HiveKey}\{controlSet}\Services\EventLog\System\{service.Name}", system.HiveKey,
                    ct);
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
            await OfflineReg.DeleteValueAsync(Runner,
                $@"{system.HiveKey}\DriverDatabase\DeviceIds\{key}", package.InfName,
                $@"{system.HiveKey}\DriverDatabase\DeviceIds\{key}", ct);
        }

        await OfflineReg.DeleteKeyAsync(Runner,
            $@"{system.HiveKey}\DriverDatabase\DriverInfFiles\{package.InfName}",
            $@"{system.HiveKey}\DriverDatabase\DriverInfFiles\{package.InfName}", ct);
        await OfflineReg.DeleteKeyAsync(Runner,
            $@"{system.HiveKey}\DriverDatabase\DriverPackages\{package.PackageDirectoryName}",
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

    private async Task DeletePathIfPresentAsync(string path, CancellationToken ct) {
        if (!File.Exists(path) && !Directory.Exists(path)) {
            return;
        }

        await ImageFs.DeleteWithRescueAsync(Runner, path, ct);
    }

    private sealed record DriverChange(
        ChangeItem Change,
        string? PublishedName = null,
        InboxDriverPackage? Package = null);

    private sealed class InboxDriverPackage {
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
            var packageResult = await OfflineReg.QueryAsync(runner, packageKey + "\\Configurations", ct);

            var serviceNames = RegQuery.NamedValues(packageResult.Output + packageResult.Error, "Service")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Some inbox packages register an auxiliary boot driver (for example,
            // nvstor) through its Owners value without listing it in the package
            // configuration's Service value. Include those services before cleanup.
            foreach (var controlSet in controlSets) {
                var ownedServices = await OfflineReg.QueryAsync(runner,
                    $"{system.HiveKey}\\{controlSet}\\Services", ct, "/s", "/f", infName, "/d");

                serviceNames.AddRange(RegQuery.DirectChildKeys(
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
                    var serviceResult = await OfflineReg.QueryAsync(runner, serviceKey, ct);
                    if (!serviceResult.Success) {
                        continue;
                    }

                    owners.UnionWith(RegQuery.NamedValues(serviceResult.Output + serviceResult.Error, "Owners")
                        .SelectMany(SplitMultiString));
                    var serviceOutput = serviceResult.Output + serviceResult.Error;
                    foreach (var imagePath in RegQuery.NamedValues(serviceOutput, "ImagePath")
                                 .SelectMany(SplitMultiString)) {
                        var fileName = Path.GetFileName(imagePath.Replace('/', '\\'));
                        if (IsSafeDriverFileName(fileName)) {
                            imagePaths.Add(fileName);
                        }
                    }
                }

                services.Add(new(serviceName, owners, imagePaths));
            }

            var deviceIdRoot = $"{system.HiveKey}\\DriverDatabase\\DeviceIds";
            var deviceIds = await OfflineReg.QueryAsync(runner, deviceIdRoot, ct, "/s", "/f", infName, "/d");

            var deviceIdKeys = RegQuery.KeysWithNamedValue(deviceIds.Output + deviceIds.Error, infName).ToList();
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
