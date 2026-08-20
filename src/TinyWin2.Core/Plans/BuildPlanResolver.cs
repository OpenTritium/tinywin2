using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;

namespace TinyWin2.Core.Plans;

/// <summary>User's (or a profile's) choice for one plan: enabled + argument values.</summary>
public sealed record PlanSelection(
    string PlanId,
    bool Enabled = true,
    IReadOnlyDictionary<string, JsonNode?>? Args = null);

public enum LayerGranularity
{
    /// <summary>One VHDX layer per group (default; fewer, coarser layers).</summary>
    Group,
    /// <summary>One VHDX layer per plan (finest traceback).</summary>
    Plan,
}

/// <summary>A plan with arguments resolved and execs bound to pure data.</summary>
public sealed record ResolvedPlan(
    PlanDefinition Definition,
    JsonObject BoundArgs,
    IReadOnlyList<ExecSpec> Execs);

/// <summary>
/// One top-level atomic unit of the build = one VHDX differencing layer.
/// With <see cref="LayerGranularity.Group"/> a step bundles a whole group; with
/// <see cref="LayerGranularity.Plan"/> each step is a single plan.
/// </summary>
public sealed record PlanStep(
    string Id,
    string Title,
    string Group,
    IReadOnlyList<ResolvedPlan> Plans)
{
    public bool IsComposite => Plans.Count > 1;
}

/// <summary>The fully resolved, ordered build plan. Pure data; no I/O.</summary>
public sealed record BuildPlan(
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<string> PlanIds,
    LayerGranularity Granularity);

public sealed class PlanResolutionException(IReadOnlyList<string> errors)
    : Exception($"Build plan resolution failed:{Environment.NewLine}{string.Join(Environment.NewLine + "  - ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class BuildPlanResolver
{
    /// <summary>
    /// Resolves selections against the catalog: expands requires (cycle-safe), rejects conflicts,
    /// validates/binds arguments, and produces the ordered sequence of atomic steps.
    /// </summary>
    public static BuildPlan Resolve(
        PlanCatalog catalog,
        IReadOnlyList<PlanSelection> selections,
        LayerGranularity granularity)
    {
        var errors = new List<string>();
        var requested = new List<string>();
        var explicitlyDisabled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var selection in selections)
        {
            if (!catalog.ById.ContainsKey(selection.PlanId))
            {
                errors.Add($"unknown plan id '{selection.PlanId}'.");
                continue;
            }
            if (selection.Enabled)
            {
                if (!requested.Contains(selection.PlanId, StringComparer.Ordinal))
                {
                    requested.Add(selection.PlanId);
                }
            }
            else
            {
                explicitlyDisabled.Add(selection.PlanId);
            }
        }

        // Expand requires depth-first (post-order emission = dependencies first), with cycle detection.
        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);
        foreach (var planId in requested)
        {
            Visit(catalog, planId, ordered, visited, inProgress, explicitlyDisabled, errors);
        }

        // Conflict rejection over the final enabled set.
        var enabled = ordered.ToHashSet(StringComparer.Ordinal);
        foreach (var planId in ordered)
        {
            foreach (var conflict in catalog.Get(planId).Conflicts)
            {
                if (enabled.Contains(conflict))
                {
                    errors.Add($"plan '{planId}' conflicts with '{conflict}'.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new PlanResolutionException(errors);
        }

        // Bind arguments and execs for every enabled plan.
        var resolvedById = new Dictionary<string, ResolvedPlan>(StringComparer.Ordinal);
        foreach (var planId in ordered)
        {
            var definition = catalog.Get(planId);
            var userArgs = selections.FirstOrDefault(s => s.PlanId == planId)?.Args;
            resolvedById[planId] = ResolvePlan(definition, userArgs);
        }

        var steps = granularity == LayerGranularity.Plan
            ? BuildPlanGranularitySteps(ordered, resolvedById)
            : BuildGroupGranularitySteps(catalog, ordered, resolvedById);

        return new BuildPlan(steps, ordered, granularity);
    }

    private static void Visit(
        PlanCatalog catalog,
        string planId,
        List<string> ordered,
        HashSet<string> visited,
        HashSet<string> inProgress,
        HashSet<string> explicitlyDisabled,
        List<string> errors)
    {
        if (visited.Contains(planId))
        {
            return;
        }
        if (!inProgress.Add(planId))
        {
            errors.Add($"dependency cycle detected at '{planId}'.");
            return;
        }

        foreach (var required in catalog.Get(planId).Requires)
        {
            if (explicitlyDisabled.Contains(required))
            {
                errors.Add($"plan '{planId}' requires '{required}', which was explicitly disabled.");
                continue;
            }
            Visit(catalog, required, ordered, visited, inProgress, explicitlyDisabled, errors);
        }
        inProgress.Remove(planId);
        visited.Add(planId);
        ordered.Add(planId);
    }

    private static ResolvedPlan ResolvePlan(PlanDefinition definition, IReadOnlyDictionary<string, JsonNode?>? userArgs)
    {
        var errors = new List<string>();
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        userArgs ??= new Dictionary<string, JsonNode?>();
        foreach (var arg in definition.Arguments)
        {
            JsonNode? value = arg.Default;
            if (userArgs.TryGetValue(arg.Name, out var provided) && provided is not null)
            {
                value = provided;
            }
            if (!ValidateValue(arg, value, out var reason))
            {
                errors.Add($"argument '{arg.Name}': {reason}");
                continue;
            }
            values[arg.Name] = value?.DeepClone();
        }
        foreach (var name in userArgs.Keys)
        {
            if (definition.Arguments.All(a => a.Name != name))
            {
                errors.Add($"argument '{name}' is not declared by plan '{definition.Id}'.");
            }
        }
        if (errors.Count > 0)
        {
            throw new PlanResolutionException(errors.Select(e => $"plan '{definition.Id}': {e}").ToArray());
        }

        var execs = definition.Execs
            .Select(exec => new ExecSpec(exec.Resource, exec.Ensure, ArgBinder.BindExec(exec, values)))
            .ToArray();
        var boundArgs = new JsonObject();
        foreach (var (name, value) in values)
        {
            boundArgs[name] = value?.DeepClone();
        }
        return new ResolvedPlan(definition, boundArgs, execs);
    }

    private static bool ValidateValue(PlanArgument argument, JsonNode? value, out string reason)
    {
        reason = "";
        if (value is null)
        {
            reason = "no value and no default.";
            return false;
        }
        switch (argument.Type)
        {
            case PlanArgumentType.Enum:
                if (value is JsonValue text && text.TryGetValue<string>(out var option)
                    && argument.Options.Any(o => string.Equals(o.Value, option, StringComparison.Ordinal)))
                {
                    return true;
                }
                reason = $"must be one of: {string.Join(", ", argument.Options.Select(o => o.Value))}.";
                return false;
            case PlanArgumentType.Int:
                if (value is JsonValue number && number.TryGetValue<int>(out _))
                {
                    return true;
                }
                reason = "must be an integer.";
                return false;
            case PlanArgumentType.Bool:
                if (value is JsonValue boolean && boolean.TryGetValue<bool>(out _))
                {
                    return true;
                }
                reason = "must be a boolean.";
                return false;
            case PlanArgumentType.String:
                if (value is JsonValue s && s.TryGetValue<string>(out var str) && !string.IsNullOrWhiteSpace(str))
                {
                    return true;
                }
                reason = "must be a non-empty string.";
                return false;
            default:
                reason = $"unsupported type '{argument.Type}'.";
                return false;
        }
    }

    private static List<PlanStep> BuildPlanGranularitySteps(
        IReadOnlyList<string> ordered,
        IReadOnlyDictionary<string, ResolvedPlan> resolvedById)
        => ordered
            .Select(planId =>
            {
                var resolved = resolvedById[planId];
                return new PlanStep(planId, resolved.Definition.Title, resolved.Definition.Group, [resolved]);
            })
            .ToList();

    private static List<PlanStep> BuildGroupGranularitySteps(
        PlanCatalog catalog,
        IReadOnlyList<string> ordered,
        IReadOnlyDictionary<string, ResolvedPlan> resolvedById)
    {
        // Group plans preserving plan topology; groups ordered by first appearance.
        var groupOrder = new List<string>();
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var planId in ordered)
        {
            var group = catalog.Get(planId).Group;
            if (!groups.TryGetValue(group, out var members))
            {
                members = [];
                groups[group] = members;
                groupOrder.Add(group);
            }
            members.Add(planId);
        }

        // Inter-group edges from requires: target's group must come before dependent's group.
        var dependsOn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var planId in ordered)
        {
            var dependentGroup = catalog.Get(planId).Group;
            foreach (var required in catalog.Get(planId).Requires)
            {
                var requiredGroup = catalog.Get(required).Group;
                if (requiredGroup == dependentGroup)
                {
                    continue;
                }
                if (!dependsOn.TryGetValue(dependentGroup, out var deps))
                {
                    deps = [];
                    dependsOn[dependentGroup] = deps;
                }
                deps.Add(requiredGroup);
            }
        }

        // Kahn's algorithm with first-appearance tiebreak; cyclic groups merge into one step.
        var pendingGroups = groupOrder.ToList();
        var steps = new List<PlanStep>();
        while (pendingGroups.Count > 0)
        {
            var ready = pendingGroups
                .Where(g => !dependsOn.TryGetValue(g, out var deps) || deps.All(d => !pendingGroups.Contains(d)))
                .ToList();
            if (ready.Count == 0)
            {
                // Cycle across groups: merge everything still pending into one combined step,
                // keeping plans in topological order so intra-step sequencing stays valid.
                var mergedPlans = ordered
                    .Where(id => pendingGroups.Contains(catalog.Get(id).Group))
                    .Select(id => resolvedById[id])
                    .ToArray();
                steps.Add(new PlanStep(
                    "group:merged",
                    string.Join(" + ", pendingGroups),
                    "merged",
                    mergedPlans));
                break;
            }
            foreach (var group in ready)
            {
                steps.Add(new PlanStep(
                    $"group:{group}",
                    group,
                    group,
                    groups[group].Select(id => resolvedById[id]).ToArray()));
                pendingGroups.Remove(group);
            }
        }
        return steps;
    }
}
