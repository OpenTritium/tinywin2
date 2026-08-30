using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;

namespace TinyWin2.Core.Plans;

/// <summary>User's (or a profile's) choice for one plan: enabled + parameter values.</summary>
public sealed record PlanSelection(
    string PlanId,
    bool Enabled = true,
    IReadOnlyDictionary<string, JsonNode?>? Parameters = null);

/// <summary>A plan with parameters resolved and its operation bound to pure data.</summary>
public sealed record ResolvedPlan(
    PlanDefinition Definition,
    OperationSpec Operation);

/// <summary>
///     One top-level unit of the build: exactly one VHDX differencing layer and one plan.
/// </summary>
public sealed record PlanStep(
    ResolvedPlan Plan) {
    public string Id => Plan.Definition.Id;
    public string Title => Plan.Definition.Title;
}

/// <summary>The fully resolved, ordered build plan. Pure data; no I/O.</summary>
public sealed record BuildPlan(IReadOnlyList<PlanStep> Steps) {
    public IReadOnlyList<string> PlanIds => [.. Steps.Select(step => step.Id)];
}

public sealed class PlanResolutionException(IReadOnlyList<string> errors)
    : Exception(
        $"Build plan resolution failed:{Environment.NewLine}{string.Join(Environment.NewLine + "  - ", errors)}");

public static class BuildPlanResolver {
    /// <summary>
    ///     Resolves selections against the catalog: expands requires (cycle-safe), rejects conflicts,
    ///     validates/binds parameters, and produces the ordered sequence of atomic steps.
    /// </summary>
    public static BuildPlan Resolve(
        PlanCatalog catalog,
        IReadOnlyList<PlanSelection> selections) {
        var errors = new List<string>();
        var requested = new List<string>();
        var explicitlyDisabled = new HashSet<string>(StringComparer.Ordinal);
        var seenSelections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections) {
            if (!seenSelections.Add(selection.PlanId)) {
                errors.Add($"plan '{selection.PlanId}' was selected more than once.");
                continue;
            }

            if (!catalog.ById.ContainsKey(selection.PlanId)) {
                errors.Add($"unknown plan id '{selection.PlanId}'.");
                continue;
            }

            if (selection.Enabled) {
                if (!requested.Contains(selection.PlanId, StringComparer.Ordinal)) {
                    requested.Add(selection.PlanId);
                }
            }
            else {
                explicitlyDisabled.Add(selection.PlanId);
            }
        }

        // Expand requires depth-first (post-order emission = dependencies first), with cycle detection.
        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);
        foreach (var planId in requested) {
            Visit(catalog, planId, ordered, visited, inProgress, explicitlyDisabled, errors);
        }

        // Conflict rejection over the final enabled set.
        var enabled = ordered.ToHashSet(StringComparer.Ordinal);
        foreach (var planId in ordered) {
            errors.AddRange(from conflict in catalog.Get(planId).Conflicts
                            where enabled.Contains(conflict)
                            select $"plan '{planId}' conflicts with '{conflict}'.");
        }

        if (errors.Count > 0) {
            throw new PlanResolutionException(errors);
        }

        // Bind parameters and the operation for every enabled plan.
        var resolvedById = new Dictionary<string, ResolvedPlan>(StringComparer.Ordinal);
        foreach (var planId in ordered) {
            var definition = catalog.Get(planId);
            var userParameters = selections.FirstOrDefault(s => s.PlanId == planId)?.Parameters;
            resolvedById[planId] = ResolvePlan(definition, userParameters);
        }

        var steps = ordered
            .Select(planId => {
                var resolved = resolvedById[planId];
                return new PlanStep(resolved);
            })
            .ToList();
        return new(steps);
    }

    private static void Visit(
        PlanCatalog catalog,
        string planId,
        List<string> ordered,
        HashSet<string> visited,
        HashSet<string> inProgress,
        HashSet<string> explicitlyDisabled,
        List<string> errors) {
        if (visited.Contains(planId)) {
            return;
        }

        if (!inProgress.Add(planId)) {
            errors.Add($"dependency cycle detected at '{planId}'.");
            return;
        }

        foreach (var required in catalog.Get(planId).Requires) {
            if (explicitlyDisabled.Contains(required)) {
                errors.Add($"plan '{planId}' requires '{required}', which was explicitly disabled.");
                continue;
            }

            Visit(catalog, required, ordered, visited, inProgress, explicitlyDisabled, errors);
        }

        inProgress.Remove(planId);
        visited.Add(planId);
        ordered.Add(planId);
    }

    private static ResolvedPlan ResolvePlan(PlanDefinition definition,
        IReadOnlyDictionary<string, JsonNode?>? userParameters) {
        var errors = new List<string>();
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        userParameters ??= new Dictionary<string, JsonNode?>();
        foreach (var parameter in definition.Parameters) {
            var value = parameter.Default;
            if (userParameters.TryGetValue(parameter.Name, out var provided)) {
                value = provided;
            }

            if (!ValidateValue(parameter, value, out var reason)) {
                errors.Add($"parameter '{parameter.Name}': {reason}");
                continue;
            }

            values[parameter.Name] = value?.DeepClone();
        }

        errors.AddRange(from name in userParameters.Keys
                        where definition.Parameters.All(p => p.Name != name)
                        select $"parameter '{name}' is not declared by plan '{definition.Id}'.");

        if (errors.Count > 0) {
            throw new PlanResolutionException([.. errors.Select(e => $"plan '{definition.Id}': {e}")]);
        }

        var operation = definition.Operation;
        return new(definition,
            new(operation.Resource, operation.Action,
                ParameterBinder.BindOperation(operation.Spec, values)));
    }

    private static bool ValidateValue(PlanParameter parameter, JsonNode? value, out string reason) {
        reason = "";
        if (value is null) {
            reason = "no value and no default.";
            return false;
        }

        switch (parameter.Type) {
            case PlanParameterType.Enum:
                if (value is JsonValue text && text.TryGetValue<string>(out var option)
                                            && parameter.Options.Any(o =>
                                                string.Equals(o.Value, option, StringComparison.Ordinal))) {
                    return true;
                }

                reason = $"must be one of: {string.Join(", ", parameter.Options.Select(o => o.Value))}.";
                return false;
            case PlanParameterType.Int:
                if (value is JsonValue number && number.TryGetValue<int>(out _)) {
                    return true;
                }

                reason = "must be an integer.";
                return false;
            case PlanParameterType.Bool:
                if (value is JsonValue boolean && boolean.TryGetValue<bool>(out _)) {
                    return true;
                }

                reason = "must be a boolean.";
                return false;
            case PlanParameterType.String:
                if (value is JsonValue s && s.TryGetValue<string>(out var str) && !string.IsNullOrWhiteSpace(str)) {
                    return true;
                }

                reason = "must be a non-empty string.";
                return false;
            default:
                reason = $"unsupported type '{parameter.Type}'.";
                return false;
        }
    }
}
