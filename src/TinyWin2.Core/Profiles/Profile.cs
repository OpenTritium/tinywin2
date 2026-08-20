using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Profiles;

/// <summary>One plan entry inside a profile: enabled + argument values.</summary>
public sealed record ProfileSelection(string PlanId, bool Enabled, JsonObject? Args);

/// <summary>An importable/exportable plan+args selection set.</summary>
public sealed record Profile(
    string Name,
    string? Description,
    IReadOnlyList<ProfileSelection> Selections) {
    private const int CurrentSchemaVersion = 1;

    public JsonObject ToJson() => new() {
        ["schemaVersion"] = CurrentSchemaVersion,
        ["name"] = Name,
        ["description"] = Description,
        ["selections"] = new JsonArray(Selections.Select(s => (JsonNode)new JsonObject {
            ["planId"] = s.PlanId,
            ["enabled"] = s.Enabled,
            ["args"] = s.Args?.DeepClone(),
        }).ToArray()),
    };

    public static Profile FromJson(JsonObject obj) {
        var name = obj["name"]?.GetValue<string>()
                   ?? throw new JsonException("profile requires 'name'.");
        var description = obj["description"]?.GetValue<string>();
        var selections = new List<ProfileSelection>();
        if (obj["selections"] is JsonArray array) {
            foreach (var node in array.OfType<JsonObject>()) {
                var planId = node["planId"]?.GetValue<string>()
                             ?? throw new JsonException("profile selection requires 'planId'.");
                selections.Add(new ProfileSelection(
                    planId,
                    node["enabled"]?.GetValue<bool>() ?? true,
                    node["args"] as JsonObject));
            }
        }
        return new Profile(name, description, selections);
    }
}

public static class ProfileStore {
    public static Profile Load(string path) {
        var node = JsonNode.Parse(File.ReadAllText(path));
        if (node is not JsonObject obj) {
            throw new JsonException($"profile '{path}' is not a JSON object.");
        }
        return Profile.FromJson(obj);
    }

    public static void Save(Profile profile, string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, profile.ToJson().ToPrettyString());
    }

    public static IReadOnlyList<PlanSelection> ToPlanSelections(Profile profile) =>
        profile.Selections
            .Select(s => new PlanSelection(s.PlanId, s.Enabled, ToStringMap(s.Args)))
            .ToList();

    /// <summary>Validates a profile against a catalog: returns unknown plan ids.</summary>
    public static IReadOnlyList<string> UnknownPlans(Profile profile, PlanCatalog catalog) =>
        profile.Selections
            .Where(s => !catalog.ById.ContainsKey(s.PlanId))
            .Select(s => s.PlanId)
            .Distinct()
            .ToList();

    private static IReadOnlyDictionary<string, JsonNode?>? ToStringMap(JsonObject? args) {
        if (args is null) {
            return null;
        }
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in args) {
            map[key] = value?.DeepClone();
        }
        return map;
    }
}
