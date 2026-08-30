using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Native;
using TinyWin2.Core.Pipeline;
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
            return ExitCodes.Success;
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
        return ExitCodes.Success;
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
        return ExitCodes.Success;
    }

    /// <summary>
    ///     Validates one plan file: schema first, then the full production pipeline
    ///     (requires/conflicts resolution, parameter binding, operation binding). This is
    ///     the self-check entry point for tools and agents authoring plan JSON.
    /// </summary>
    public static int Validate(PlanValidateRequest request) {
        var file = Path.GetFullPath(request.File);
        var errors = new List<string>();
        PlanDefinition? definition = null;
        try {
            var node = JsonNode.Parse(File.ReadAllText(file));
            if (node is not JsonObject obj) {
                throw new PlanValidationException(file, ["root must be a JSON object."]);
            }

            definition = PlanDefinition.FromJson(obj, file);
        }
        catch (PlanValidationException ex) {
            errors.AddRange(ex.Errors);
        }
        catch (JsonException ex) {
            errors.Add($"invalid JSON: {ex.Message}");
        }

        if (definition is not null && errors.Count == 0) {
            try {
                var registry = new ExecuterRegistry(new ProcessRunner());
                var catalog = PlanCatalog.LoadDirectory(Cli.FindPlansDirectory(request.PlansDirectory));
                if (catalog.ById.ContainsKey(definition.Id)) {
                    BuildPlanResolver.Resolve(catalog, [new PlanSelection(definition.Id)], registry);
                }
                else {
                    // The file is not part of the located catalog; bind it standalone.
                    BuildPlanResolver.Resolve(new PlanCatalog([definition]),
                        [new PlanSelection(definition.Id)], registry);
                }
            }
            catch (PlanValidationException ex) {
                errors.AddRange(ex.Errors);
            }
            catch (PlanResolutionException ex) {
                errors.Add(ex.Message);
            }
            catch (ParameterBindingException ex) {
                errors.Add(ex.Message);
            }
            catch (ExecException ex) {
                errors.Add(ex.Message);
            }
            catch (KeyNotFoundException ex) {
                errors.Add(ex.Message);
            }
        }

        if (request.Json) {
            Console.WriteLine(new JsonObject {
                ["file"] = file,
                ["valid"] = errors.Count == 0,
                ["errors"] = new JsonArray([.. errors.Select(e => (JsonNode)JsonValue.Create(e))]),
                ["plan"] = definition is null
                    ? null
                    : new JsonObject {
                        ["id"] = definition.Id,
                        ["version"] = definition.Version,
                        ["operations"] = definition.Operations.Count,
                        ["parameters"] = definition.Parameters.Count
                    }
            }.ToJsonString(Cli.JsonSerializerOptions));
        }
        else if (errors.Count > 0) {
            Console.WriteLine($"✘ invalid plan ({errors.Count} error(s)):");
            foreach (var error in errors) {
                Console.WriteLine($"  - {error}");
            }
        }
        else {
            Console.WriteLine(
                $"✔ plan '{definition!.Id}' is valid ({definition.Operations.Count} operation(s), " +
                $"{definition.Parameters.Count} parameter(s))");
        }

        return errors.Count == 0 ? ExitCodes.Success : ExitCodes.Failure;
    }

    private static PlanCatalog LoadCatalog(string? plansDirectory) =>
        PlanCatalog.LoadDirectory(Cli.FindPlansDirectory(plansDirectory));
}
