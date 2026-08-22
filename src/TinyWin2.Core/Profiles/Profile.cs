using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Profiles;

/// <summary>An importable/exportable set of plan selections and parameter values.</summary>
public sealed record Profile(
    string Name,
    string? Description,
    IReadOnlyList<PlanSelection> Selections) {
    private const int CurrentSchemaVersion = 3;

    public JsonObject ToJson() {
        var result = new JsonObject {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["name"] = Name,
            ["selections"] = new JsonArray([.. Selections.Select(ToJson)]),
        };
        if (Description is not null) {
            result["description"] = Description;
        }

        return result;
    }

    public static Profile FromJson(JsonObject obj) {
        RejectUnknownProperties(obj, "profile", ["schemaVersion", "name", "description", "selections"]);
        var schemaVersion = obj["schemaVersion"]?.GetValue<int>()
                            ?? throw new JsonException("profile requires 'schemaVersion'.");
        if (schemaVersion != CurrentSchemaVersion) {
            throw new JsonException($"profile schemaVersion must be {CurrentSchemaVersion}.");
        }

        var name = obj["name"]?.GetValue<string>()
                   ?? throw new JsonException("profile requires 'name'.");
        if (string.IsNullOrWhiteSpace(name)) {
            throw new JsonException("profile 'name' must not be empty.");
        }

        var description = obj["description"]?.GetValue<string>();
        var selections = new List<PlanSelection>();
        if (obj["selections"] is not JsonArray array) {
            throw new JsonException("profile requires 'selections' array.");
        }

        var seenPlanIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < array.Count; index++) {
            if (array[index] is not JsonObject node) {
                throw new JsonException($"profile selection at index {index} must be an object.");
            }

            RejectUnknownProperties(node, $"profile selection {index}", ["planId", "enabled", "parameters"]);
            var planId = node["planId"]?.GetValue<string>()
                         ?? throw new JsonException("profile selection requires 'planId'.");
            if (string.IsNullOrWhiteSpace(planId)) {
                throw new JsonException("profile selection 'planId' must not be empty.");
            }

            if (!seenPlanIds.Add(planId)) {
                throw new JsonException($"profile contains duplicate selection for plan '{planId}'.");
            }

            if (node.TryGetPropertyValue("parameters", out var parametersNode)
                && parametersNode is not JsonObject) {
                throw new JsonException($"profile selection '{planId}' parameters must be an object.");
            }

            if (node.TryGetPropertyValue("enabled", out var enabledNode) && enabledNode is null) {
                throw new JsonException($"profile selection '{planId}' enabled must be a boolean.");
            }

            selections.Add(new(
                planId,
                node["enabled"]?.GetValue<bool>() ?? true,
                ToParameterMap(parametersNode as JsonObject)));
        }

        return new Profile(name, description, selections);
    }

    private static void RejectUnknownProperties(JsonObject obj, string path, IReadOnlyList<string> allowed) {
        var unknown = obj.Select(property => property.Key)
            .FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal));
        if (unknown is not null) {
            throw new JsonException($"{path} contains unknown field '{unknown}'.");
        }
    }

    private static JsonObject ToJson(PlanSelection selection) {
        var result = new JsonObject {
            ["planId"] = selection.PlanId,
            ["enabled"] = selection.Enabled,
        };
        if (selection.Parameters is not { Count: > 0 } parameters) {
            return result;
        }

        var parameterObject = new JsonObject();
        foreach (var (name, value) in parameters) {
            parameterObject[name] = value?.DeepClone();
        }

        result["parameters"] = parameterObject;
        return result;
    }

    private static IReadOnlyDictionary<string, JsonNode?>? ToParameterMap(JsonObject? parameters) {
        if (parameters is null) {
            return null;
        }

        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters) {
            map[key] = value?.DeepClone();
        }

        return map;
    }
}

public static class ProfileStore {
    public static Profile Load(string path) {
        var node = JsonNode.Parse(File.ReadAllText(path));
        return node is not JsonObject obj
            ? throw new JsonException($"profile '{path}' is not a JSON object.")
            : Profile.FromJson(obj);
    }

    public static void Save(Profile profile, string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, profile.ToJson().ToPrettyString());
    }

    public static IReadOnlyList<PlanSelection> ToPlanSelections(Profile profile) => profile.Selections;

    /// <summary>Validates a profile against a catalog: returns unknown plan ids.</summary>
    public static IReadOnlyList<string> UnknownPlans(Profile profile, PlanCatalog catalog) => [
        .. profile.Selections
            .Where(s => !catalog.ById.ContainsKey(s.PlanId))
            .Select(s => s.PlanId)
            .Distinct()
    ];
}
