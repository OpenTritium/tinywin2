using TinyWin2.Core.Plans;
using TinyWin2.Core.Profiles;

namespace TinyWin2.Cli.Commands;

internal static class ProfileCommand {
    public static int Run(List<string> args) {
        var options = Program.ParseOptions(args);
        var subcommand = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "list";
        return subcommand switch {
            "list" => List(options),
            "show" => Show(args.Skip(1).ToList()),
            "export" => Export(options),
            "import" => Import(args.Skip(1).ToList(), options),
            _ => Unknown(subcommand),
        };
    }

    private static string DefaultProfilesDirectory(Dictionary<string, List<string>> options) {
        if (options.GetValueOrDefault("profiles")?.FirstOrDefault() is { } explicitDir) {
            return explicitDir;
        }
        var plansDir = new DirectoryInfo(Cli.FindPlansDirectory(options.GetValueOrDefault("plans")?.FirstOrDefault()));
        var candidate = Path.Combine(plansDir.Parent!.FullName, "profiles");
        return candidate;
    }

    private static int List(Dictionary<string, List<string>> options) {
        var directory = DefaultProfilesDirectory(options);
        if (!Directory.Exists(directory)) {
            Console.WriteLine($"no profiles directory at {directory}");
            return 0;
        }
        var files = Directory.GetFiles(directory, "*.json");
        if (files.Length == 0) {
            Console.WriteLine($"no profiles found in {directory}");
            return 0;
        }
        foreach (var file in files) {
            var profile = ProfileStore.Load(file);
            Console.WriteLine($"{Path.GetFileName(file),-34} {profile.Name,-24} {profile.Selections.Count} selections");
        }
        return 0;
    }

    private static int Show(List<string> args) {
        if (args.Count == 0) {
            Console.Error.WriteLine("usage: tinywin2 profile show <file>");
            return 2;
        }
        Console.WriteLine(ProfileStore.Load(args[0]).ToJson().ToJsonString(DoctorCommand.JsonSerializerOptions));
        return 0;
    }

    private static int Export(Dictionary<string, List<string>> options) {
        var output = options.GetValueOrDefault("out")?.FirstOrDefault() ?? options.GetValueOrDefault("o")?.FirstOrDefault();
        if (output is null) {
            Console.Error.WriteLine("usage: tinywin2 profile export -o <file> [--name x] [--profile p] [--plan id ...] [--set planId.parameter=v ...]");
            return 2;
        }
        var catalog = PlanCatalog.LoadDirectory(Cli.FindPlansDirectory(options.GetValueOrDefault("plans")?.FirstOrDefault()));
        var selections = Cli.BuildSelections(options, catalog);
        var profile = new Profile(
            options.GetValueOrDefault("name")?.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(output),
            "exported by tinywin2",
            selections);
        ProfileStore.Save(profile, output);
        Console.WriteLine($"exported {profile.Selections.Count} selections → {output}");
        return 0;
    }

    private static int Import(List<string> args, Dictionary<string, List<string>> options) {
        if (args.Count == 0) {
            Console.Error.WriteLine("usage: tinywin2 profile import <file>");
            return 2;
        }
        var catalog = PlanCatalog.LoadDirectory(Cli.FindPlansDirectory(options.GetValueOrDefault("plans")?.FirstOrDefault()));
        var profile = ProfileStore.Load(args[0]);
        var unknown = ProfileStore.UnknownPlans(profile, catalog);
        Console.WriteLine($"profile '{profile.Name}': {profile.Selections.Count} selections, {profile.Selections.Count(s => s.Enabled)} enabled");
        foreach (var selection in profile.Selections) {
            var marker = unknown.Contains(selection.PlanId) ? "MISSING" : selection.Enabled ? "on " : "off";
            Console.WriteLine($"  [{marker}] {selection.PlanId}");
        }
        if (unknown.Count > 0) {
            Console.Error.WriteLine($"unknown plans (not importable with the current catalog): {string.Join(", ", unknown)}");
            return 1;
        }
        return 0;
    }

    private static int Unknown(string command) {
        Console.Error.WriteLine($"unknown profile subcommand: {command} (list|show|export|import)");
        return 2;
    }
}
