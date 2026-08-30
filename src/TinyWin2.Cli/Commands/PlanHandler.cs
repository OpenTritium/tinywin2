using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;

namespace TinyWin2.Cli.Commands;

internal static class PlanHandler {
    public static int List(PlanListRequest request) {
        var catalog = LoadCatalog(request.PlansDirectory);
        var plans = catalog.Plans
            .Where(plan => request.Category is null
                           || string.Equals(plan.Category, request.Category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(plan => plan.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (request.Json) {
            Console.WriteLine(new JsonObject {
                ["plans"] = new JsonArray(plans.Select(plan => (JsonNode)new JsonObject {
                    ["id"] = plan.Id,
                    ["title"] = plan.Title,
                    ["category"] = plan.Category,
                    ["riskLevel"] = plan.RiskLevel,
                    ["requires"] = new JsonArray(plan.Requires.Select(r => (JsonNode)r).ToArray()),
                    ["conflicts"] = new JsonArray(plan.Conflicts.Select(c => (JsonNode)c).ToArray()),
                    ["parameters"] =
                        new JsonArray(plan.Parameters.Select(parameter => (JsonNode)parameter.ToJson()).ToArray())
                }).ToArray())
            }.ToJsonString(Cli.JsonSerializerOptions));
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
                : $"  (parameters: {string.Join(", ", plan.Parameters.Select(parameter => parameter.Name))})";
            Console.WriteLine($"  {risk}{plan.Id,-46} {plan.Title}{parameters}");
        }

        Console.WriteLine();
        Console.WriteLine($"{plans.Count} plans. Details: tinywin2 plan show <id>");
        return 0;
    }

    public static int Show(PlanShowRequest request) {
        var plan = LoadCatalog(request.PlansDirectory).Get(request.Id);
        Console.WriteLine(new JsonObject {
            ["id"] = plan.Id,
            ["version"] = plan.Version,
            ["title"] = plan.Title,
            ["description"] = plan.Description,
            ["category"] = plan.Category,
            ["riskLevel"] = plan.RiskLevel,
            ["requires"] = new JsonArray(plan.Requires.Select(r => (JsonNode)r).ToArray()),
            ["conflicts"] = new JsonArray(plan.Conflicts.Select(c => (JsonNode)c).ToArray()),
            ["parameters"] = new JsonArray(plan.Parameters.Select(parameter => (JsonNode)parameter.ToJson()).ToArray()),
            ["operations"] = new JsonArray(plan.Operations.Select(operation => (JsonNode)new JsonObject {
                ["resource"] = operation.Resource,
                ["action"] = operation.Action.ToString().ToLowerInvariant(),
                ["spec"] = operation.Spec.DeepClone()
            }).ToArray())
        }.ToJsonString(Cli.JsonSerializerOptions));
        return 0;
    }

    private static PlanCatalog LoadCatalog(string? plansDirectory) =>
        PlanCatalog.LoadDirectory(Cli.FindPlansDirectory(plansDirectory));
}
