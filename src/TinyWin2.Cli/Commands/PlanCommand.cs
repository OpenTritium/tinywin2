using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;

namespace TinyWin2.Cli.Commands;

internal static class PlanCommand {
    public static int Run(List<string> args) {
        var options = Program.ParseOptions(args);
        var catalog = PlanCatalog.LoadDirectory(Cli.FindPlansDirectory(options.GetValueOrDefault("plans")?.FirstOrDefault()));
        if (args.Count > 0 && !args[0].StartsWith("--")) {
            return args[0] switch {
                "list" => List(catalog, options),
                "show" => Show(catalog, args.Skip(1).ToList()),
                _ => Unknown(args[0]),
            };
        }
        return List(catalog, options);
    }

    private static int List(PlanCatalog catalog, Dictionary<string, List<string>> options) {
        var groupFilter = options.GetValueOrDefault("group")?.FirstOrDefault();
        var plans = catalog.Plans
            .Where(p => groupFilter is null || string.Equals(p.Group, groupFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase);
        if (options.ContainsKey("json")) {
            var root = new JsonObject {
                ["plans"] = new JsonArray(plans.Select(p => (JsonNode)new JsonObject {
                    ["id"] = p.Id,
                    ["title"] = p.Title,
                    ["group"] = p.Group,
                    ["risk"] = p.Risk,
                    ["tier"] = p.Tier,
                    ["requires"] = new JsonArray(p.Requires.Select(r => (JsonNode)r).ToArray()),
                    ["conflicts"] = new JsonArray(p.Conflicts.Select(c => (JsonNode)c).ToArray()),
                    ["arguments"] = new JsonArray(p.Arguments.Select(a => (JsonNode)a.ToJson()).ToArray()),
                }).ToArray()),
            };
            Console.WriteLine(root.ToJsonString(DoctorCommand.JsonSerializerOptions));
            return 0;
        }
        string? currentGroup = null;
        foreach (var plan in plans) {
            if (currentGroup != plan.Group) {
                currentGroup = plan.Group;
                Console.WriteLine();
                Console.WriteLine($"== {plan.Group} ==");
            }
            var risk = plan.Risk.ToLowerInvariant() switch {
                "high" => "‼",
                "medium" => "! ",
                _ => "· ",
            };
            var args = plan.Arguments.Count == 0 ? "" : $"  (args: {string.Join(", ", plan.Arguments.Select(a => a.Name))})";
            Console.WriteLine($"  {risk}{plan.Id,-46} {plan.Title}{args}");
        }
        Console.WriteLine();
        Console.WriteLine($"{catalog.Plans.Count} plans. Details: tinywin2 plan show <id>");
        return 0;
    }

    private static int Show(PlanCatalog catalog, List<string> args) {
        if (args.Count == 0) {
            Console.Error.WriteLine("usage: tinywin2 plan show <id>");
            return 2;
        }
        var plan = catalog.Get(args[0]);
        Console.WriteLine(new JsonObject {
            ["id"] = plan.Id,
            ["version"] = plan.Version,
            ["title"] = plan.Title,
            ["description"] = plan.Description,
            ["group"] = plan.Group,
            ["risk"] = plan.Risk,
            ["tier"] = plan.Tier,
            ["requires"] = new JsonArray(plan.Requires.Select(r => (JsonNode)r).ToArray()),
            ["conflicts"] = new JsonArray(plan.Conflicts.Select(c => (JsonNode)c).ToArray()),
            ["arguments"] = new JsonArray(plan.Arguments.Select(a => (JsonNode)a.ToJson()).ToArray()),
            ["execs"] = new JsonArray(plan.Execs.Select(e => (JsonNode)new JsonObject {
                ["resource"] = e.Resource,
                ["ensure"] = e.Ensure.ToString().ToLowerInvariant(),
                ["with"] = e.With.DeepClone(),
            }).ToArray()),
        }.ToJsonString(DoctorCommand.JsonSerializerOptions));
        return 0;
    }

    private static int Unknown(string command) {
        Console.Error.WriteLine($"unknown plan subcommand: {command} (list|show)");
        return 2;
    }
}
