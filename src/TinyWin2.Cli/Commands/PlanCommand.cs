using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;

namespace TinyWin2.Cli.Commands;

internal static class PlanCommand {
    public static int Run(List<string> args) {
        var options = Program.ParseOptions(args);
        var catalog =
            PlanCatalog.LoadDirectory(Cli.FindPlansDirectory(options.GetValueOrDefault("plans")?.FirstOrDefault()));
        if (args.Count > 0 && !args[0].StartsWith("--")) {
            return args[0] switch {
                "list" => List(catalog, options),
                "show" => Show(catalog, args.Skip(1).ToList()),
                _ => Unknown(args[0])
            };
        }

        return List(catalog, options);
    }

    private static int List(PlanCatalog catalog, Dictionary<string, List<string>> options) {
        var categoryFilter = options.GetValueOrDefault("category")?.FirstOrDefault();
        var plans = catalog.Plans
            .Where(p => categoryFilter is null ||
                        string.Equals(p.Category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase);
        if (options.ContainsKey("json")) {
            var root = new JsonObject {
                ["plans"] = new JsonArray(plans.Select(p => (JsonNode)new JsonObject {
                    ["id"] = p.Id,
                    ["title"] = p.Title,
                    ["category"] = p.Category,
                    ["riskLevel"] = p.RiskLevel,
                    ["requires"] = new JsonArray(p.Requires.Select(r => (JsonNode)r).ToArray()),
                    ["conflicts"] = new JsonArray(p.Conflicts.Select(c => (JsonNode)c).ToArray()),
                    ["parameters"] = new JsonArray(p.Parameters.Select(a => (JsonNode)a.ToJson()).ToArray())
                }).ToArray())
            };
            Console.WriteLine(root.ToJsonString(DoctorCommand.JsonSerializerOptions));
            return 0;
        }

        string? currentCategory = null;
        foreach (var plan in plans) {
            if (currentCategory != plan.Category) {
                currentCategory = plan.Category;
                Console.WriteLine();
                Console.WriteLine($"== {plan.Category} ==");
            }

            var risk = plan.RiskLevel.ToLowerInvariant() switch {
                "high" => "‼",
                "medium" => "! ",
                _ => "· "
            };
            var parameters = plan.Parameters.Count == 0
                ? ""
                : $"  (parameters: {string.Join(", ", plan.Parameters.Select(a => a.Name))})";
            Console.WriteLine($"  {risk}{plan.Id,-46} {plan.Title}{parameters}");
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
            ["category"] = plan.Category,
            ["riskLevel"] = plan.RiskLevel,
            ["requires"] = new JsonArray(plan.Requires.Select(r => (JsonNode)r).ToArray()),
            ["conflicts"] = new JsonArray(plan.Conflicts.Select(c => (JsonNode)c).ToArray()),
            ["parameters"] = new JsonArray(plan.Parameters.Select(a => (JsonNode)a.ToJson()).ToArray()),
            ["operation"] = new JsonObject {
                ["resource"] = plan.Operation.Resource,
                ["action"] = plan.Operation.Action.ToString().ToLowerInvariant(),
                ["spec"] = plan.Operation.Spec.DeepClone()
            }
        }.ToJsonString(DoctorCommand.JsonSerializerOptions));
        return 0;
    }

    private static int Unknown(string command) {
        Console.Error.WriteLine($"unknown plan subcommand: {command} (list|show)");
        return 2;
    }
}
