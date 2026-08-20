using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyWin2.Core.Plans;

/// <summary>Loads and validates every plan JSON under a directory. Single loader shared by CLI and GUI.</summary>
public sealed class PlanCatalog {
    public PlanCatalog(IReadOnlyList<PlanDefinition> plans) {
        Plans = plans;
        ById = plans.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<PlanDefinition> Plans { get; }
    public IReadOnlyDictionary<string, PlanDefinition> ById { get; }

    public PlanDefinition Get(string planId) =>
        ById.TryGetValue(planId, out var plan) ? plan : throw new KeyNotFoundException($"Unknown plan id '{planId}'.");

    public static PlanCatalog LoadDirectory(string directory) {
        if (!Directory.Exists(directory)) {
            throw new DirectoryNotFoundException($"Plans directory not found: {directory}");
        }

        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) {
            throw new InvalidOperationException($"No plan definitions (*.json) found in {directory}.");
        }

        var plans = new List<PlanDefinition>();
        var errors = new List<string>();
        var seenIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files) {
            PlanDefinition? plan;
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            try {
                var node = JsonNode.Parse(File.ReadAllText(file));
                if (node is not JsonObject obj) {
                    throw new PlanValidationException(file, ["root must be a JSON object."]);
                }
                plan = PlanDefinition.FromJson(obj, sourceFile: file);
            }
            catch (JsonException ex) {
                errors.Add($"'{Path.GetFileName(file)}': invalid JSON ({ex.Message}).");
                continue;
            }
            catch (PlanValidationException ex) {
                errors.Add(ex.Message);
                continue;
            }

            if (seenIds.TryGetValue(plan.Id, out var otherFile)) {
                errors.Add($"duplicate plan id '{plan.Id}' in '{Path.GetFileName(otherFile)}' and '{Path.GetFileName(file)}'.");
                continue;
            }
            seenIds[plan.Id] = file;
            plans.Add(plan with { Sha256 = sha256 });
        }

        foreach (var plan in plans) {
            foreach (var required in plan.Requires) {
                if (!seenIds.ContainsKey(required)) {
                    errors.Add($"plan '{plan.Id}' requires unknown plan '{required}'.");
                }
            }
            foreach (var conflict in plan.Conflicts) {
                if (!seenIds.ContainsKey(conflict)) {
                    errors.Add($"plan '{plan.Id}' conflicts with unknown plan '{conflict}'.");
                }
            }
            if (plan.Conflicts.Contains(plan.Id)) {
                errors.Add($"plan '{plan.Id}' cannot conflict with itself.");
            }
        }

        if (errors.Count > 0) {
            throw new PlanValidationException(directory, errors);
        }
        return new PlanCatalog(plans);
    }
}
