using TinyWin2.Core.Plans;
using TinyWin2.Core.Profiles;

namespace TinyWin2.Cli.Commands;

internal static class ProfileHandler {
    public static int List(ProfileListRequest request) {
        var directory = ResolveProfilesDirectory(request.PlansDirectory, request.ProfilesDirectory);
        if (!Directory.Exists(directory)) {
            Console.WriteLine($"no profiles directory at {directory}");
            return 0;
        }

        var files = Directory.GetFiles(directory, "*.json");
        if (files.Length == 0) {
            Console.WriteLine($"no profiles found in {directory}");
            return 0;
        }

        foreach (var file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) {
            var profile = ProfileStore.Load(file);
            Console.WriteLine($"{Path.GetFileName(file),-34} {profile.Name,-24} {profile.Selections.Count} selections");
        }

        return 0;
    }

    public static int Show(ProfileShowRequest request) {
        Console.WriteLine(ProfileStore.Load(request.File).ToJson().ToJsonString(Cli.JsonSerializerOptions));
        return 0;
    }

    public static int Export(ProfileExportRequest request) {
        var catalog = LoadCatalog(request.Selection.PlansDirectory);
        var selections = Cli.BuildSelections(request.Selection, catalog);
        var profile = new Profile(
            request.Name ?? Path.GetFileNameWithoutExtension(request.Output),
            "exported by tinywin2",
            selections);
        ProfileStore.Save(profile, request.Output);
        Console.WriteLine($"exported {profile.Selections.Count} selections → {request.Output}");
        return 0;
    }

    public static int Validate(ProfileValidateRequest request) {
        var catalog = LoadCatalog(request.PlansDirectory);
        var profile = ProfileStore.Load(request.File);
        var unknown = ProfileStore.UnknownPlans(profile, catalog);
        Console.WriteLine(
            $"profile '{profile.Name}': {profile.Selections.Count} selections, {profile.Selections.Count(s => s.Enabled)} enabled");
        foreach (var selection in profile.Selections) {
            var marker = unknown.Contains(selection.PlanId) ? "MISSING" : selection.Enabled ? "on " : "off";
            Console.WriteLine($"  [{marker}] {selection.PlanId}");
        }

        if (unknown.Count > 0) {
            Console.Error.WriteLine(
                $"unknown plans (not valid with the current catalog): {string.Join(", ", unknown)}");
            return 1;
        }

        return 0;
    }

    private static PlanCatalog LoadCatalog(string? plansDirectory) =>
        PlanCatalog.LoadDirectory(Cli.FindPlansDirectory(plansDirectory));

    private static string ResolveProfilesDirectory(string? plansDirectory, string? profilesDirectory) {
        if (profilesDirectory is not null) {
            return Path.GetFullPath(profilesDirectory);
        }

        var plans = new DirectoryInfo(Cli.FindPlansDirectory(plansDirectory));
        return Path.Combine(plans.Parent!.FullName, "profiles");
    }
}
