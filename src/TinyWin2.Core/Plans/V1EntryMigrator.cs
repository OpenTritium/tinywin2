using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyWin2.Core.Plans;

public sealed record MigrationOutput(
    IReadOnlyList<JsonObject> Plans,
    IReadOnlyDictionary<string, string> IdMap,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Converts v1 <c>entries/*.json</c> into v2 plans: handler→resource mapping, id renaming,
/// and merging of "same target, different mode" service entry pairs into single plans
/// with a <c>startMode</c> enum argument.
/// </summary>
public static class V1EntryMigrator {
    private static readonly string[] ServiceVerbs = ["disable", "delay", "manual", "auto", "configure", "set", "remove"];

    public static MigrationOutput Migrate(IEnumerable<(string FileName, JsonObject Entry)> entries) {
        var materialized = entries.ToList();
        var warnings = new List<string>();
        var plans = new List<JsonObject>();
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        // ---- 1. service entries: group by (services ∪ patterns) target signature ----
        var serviceEntries = materialized
            .Where(e => e.Entry["handler"]?.GetValue<string>() is "Registry.DisableOfflineService" or "Registry.ConfigureOfflineService")
            .ToList();
        foreach (var group in serviceEntries.GroupBy(e => ServiceSignature(e.Entry), StringComparer.Ordinal)) {
            if (group.Key.Length == 0) {
                warnings.Add("a service entry had no services/servicePatterns; skipped.");
                continue;
            }
            var (plan, newId) = BuildServicePlan(group.ToList(), usedIds);
            foreach (var entry in group) {
                idMap[entry.Entry["id"]!.GetValue<string>()] = newId;
            }
            plans.Add(plan);
        }

        // ---- 2. every other handler: one-to-one ----
        foreach (var (fileName, entry) in materialized) {
            var handler = entry["handler"]?.GetValue<string>();
            if (handler is "Registry.DisableOfflineService" or "Registry.ConfigureOfflineService") {
                continue;
            }
            var (plan, newId) = handler switch {
                "Registry.SetOfflineValue" => BuildRegistryValuePlan(entry, usedIds),
                "Dism.RemoveOptionalComponent" => BuildDismComponentPlan(entry, usedIds),
                "Dism.ComponentCleanup" => BuildComponentCleanupPlan(entry, usedIds),
                "Dism.RemovePackage" => BuildSimplePlan(entry, usedIds, "dism.package", "component.",
                    p => new JsonObject { ["patterns"] = CopyArray(p, "packagePatterns") }),
                "Appx.RemoveProvisioned" => BuildSimplePlan(entry, usedIds, "appx.provisioned", "appx.",
                    p => new JsonObject { ["patterns"] = CopyArray(p, "patterns") }),
                "DriverStore.RemoveInbox" => BuildSimplePlan(entry, usedIds, "driver.store", "driver.",
                    p => new JsonObject { ["infNames"] = CopyArray(p, "infNames") }),
                "Filesystem.RemovePath" => BuildSimplePlan(entry, usedIds, "fs.path", "fs.",
                    p => new JsonObject { ["paths"] = CopyArray(p, "paths") }),
                _ => throw new InvalidOperationException($"{fileName}: unknown v1 handler '{handler}'."),
            };
            idMap[entry["id"]!.GetValue<string>()] = newId;
            plans.Add(plan);
        }

        // ---- 3. rewire requires/conflicts through the id map ----
        foreach (var plan in plans) {
            RewireReferences(plan, idMap, warnings);
        }
        return new MigrationOutput(plans, idMap, warnings);
    }

    private static (JsonObject Plan, string NewId) BuildServicePlan(
        List<(string FileName, JsonObject Entry)> group,
        HashSet<string> usedIds) {
        var configure = group.FirstOrDefault(g =>
            g.Entry["handler"]!.GetValue<string>() == "Registry.ConfigureOfflineService");
        var disable = group.FirstOrDefault(g =>
            g.Entry["handler"]!.GetValue<string>() == "Registry.DisableOfflineService");
        var template = configure.Entry ?? disable.Entry;
        var parameters = template["parameters"]!.AsObject();
        var oldId = template["id"]!.GetValue<string>();
        var newId = UniqueId(usedIds, "service." + SlugFromOldId(oldId));
        var with = new JsonObject();
        if (parameters["services"] is { } services) {
            with["services"] = services.DeepClone();
        }
        if (parameters["servicePatterns"] is { } patterns) {
            with["servicePatterns"] = patterns.DeepClone();
        }
        JsonObject plan;
        if (configure.Entry is not null && disable.Entry is not null) {
            // Merged pair: enum argument over start modes.
            var configureRisk = configure.Entry["risk"]?.GetValue<string>() ?? "Medium";
            var disableRisk = disable.Entry["risk"]?.GetValue<string>() ?? "High";
            var configureMode = V1ModeToV2(parameters["startMode"]?.GetValue<string>() ?? "Manual");
            with["start"] = new JsonObject {
                ["$map"] = new JsonObject {
                    ["arg"] = "startMode",
                    ["cases"] = new JsonObject {
                        [configureMode] = parameters["startMode"]!.GetValue<string>(),
                        ["disabled"] = "disabled",
                    },
                },
            };
            plan = BasePlan(newId, template, tierOverride: configure.Entry["selectionTier"]?.GetValue<string>());
            plan["arguments"] = new JsonArray(new JsonObject {
                ["name"] = "startMode",
                ["type"] = "enum",
                ["label"] = "启动方式",
                ["default"] = configureMode,
                ["options"] = new JsonArray(
                    Option(configureMode, ModeLabel(configureMode), configureRisk),
                    Option("disabled", "禁用", disableRisk)),
            });
        }
        else if (configure.Entry is not null) {
            with["start"] = parameters["startMode"]!.GetValue<string>();
            plan = BasePlan(newId, template);
        }
        else {
            with["start"] = "disabled";
            plan = BasePlan(newId, disable.Entry!);
        }
        plan["execs"] = new JsonArray(new JsonObject {
            ["resource"] = "registry.service",
            ["ensure"] = "present",
            ["with"] = with,
        });
        return (plan, newId);
    }

    private static (JsonObject, string) BuildRegistryValuePlan(JsonObject entry, HashSet<string> usedIds) {
        var parameters = entry["parameters"]!.AsObject();
        var hive = parameters["hive"]?.GetValue<string>() switch {
            "SOFTWARE" => "software",
            "SYSTEM" => "system",
            "DEFAULT" => "default",
            "NTUSER" => "default-user",
            var unknown => throw new InvalidOperationException($"unknown v1 hive '{unknown}' in {entry["id"]}"),
        };
        var values = new JsonArray();
        if (parameters["values"] is JsonArray many) {
            foreach (var node in many.OfType<JsonObject>()) {
                values.Add(ConvertValue(node));
            }
        }
        else {
            values.Add(ConvertValue(parameters));
        }
        var oldId = entry["id"]!.GetValue<string>();
        var newId = UniqueId(usedIds, "registry." + SlugFromOldId(oldId));
        var plan = BasePlan(newId, entry);
        plan["execs"] = new JsonArray(new JsonObject {
            ["resource"] = "registry.value",
            ["ensure"] = "present",
            ["with"] = new JsonObject { ["hive"] = hive, ["values"] = values },
        });
        return (plan, newId);
    }

    private static JsonObject ConvertValue(JsonObject value) {
        var type = value["type"]?.GetValue<string>() switch {
            "DWord" => "dword",
            "QWord" => "qword",
            "MultiString" => "multi",
            "ExpandString" => "expand",
            "String" => "string",
            var unknown => throw new InvalidOperationException($"unknown v1 registry type '{unknown}'"),
        };
        return new JsonObject {
            ["key"] = value["key"]!.GetValue<string>(),
            ["name"] = value["name"]?.GetValue<string>() ?? "",
            ["type"] = type,
            ["data"] = value["value"]?.DeepClone(),
        };
    }

    private static (JsonObject, string) BuildDismComponentPlan(JsonObject entry, HashSet<string> usedIds) {
        var parameters = entry["parameters"]!.AsObject();
        var oldId = entry["id"]!.GetValue<string>();
        var slug = SlugFromOldId(oldId);
        var execs = new JsonArray();
        string prefix;
        if (parameters["features"] is JsonArray features && features.Count > 0) {
            prefix = "feature.";
            execs.Add(new JsonObject {
                ["resource"] = "dism.feature",
                ["ensure"] = "absent",
                ["with"] = new JsonObject {
                    ["features"] = features.DeepClone(),
                    ["removePayload"] = parameters["removePayload"]?.GetValue<bool>() ?? true,
                },
            });
        }
        else {
            prefix = "capability.";
        }
        if (parameters["capabilities"] is JsonArray capabilities && capabilities.Count > 0) {
            execs.Add(new JsonObject {
                ["resource"] = "dism.capability",
                ["ensure"] = "absent",
                ["with"] = new JsonObject { ["capabilities"] = capabilities.DeepClone() },
            });
        }
        var newId = UniqueId(usedIds, prefix + slug);
        var plan = BasePlan(newId, entry);
        plan["execs"] = execs;
        return (plan, newId);
    }

    private static (JsonObject, string) BuildComponentCleanupPlan(JsonObject entry, HashSet<string> usedIds) {
        var parameters = entry["parameters"]!.AsObject();
        var newId = UniqueId(usedIds, "maintenance.component-cleanup");
        var plan = BasePlan(newId, entry);
        plan["execs"] = new JsonArray(new JsonObject {
            ["resource"] = "dism.component-store",
            ["ensure"] = "absent",
            ["with"] = new JsonObject {
                ["resetBase"] = parameters["resetBase"]?.GetValue<bool>() ?? false,
            },
        });
        return (plan, newId);
    }

    private static (JsonObject, string) BuildSimplePlan(
        JsonObject entry,
        HashSet<string> usedIds,
        string resource,
        string idPrefix,
        Func<JsonObject, JsonObject> withFactory) {
        var oldId = entry["id"]!.GetValue<string>();
        var newId = UniqueId(usedIds, idPrefix + SlugFromOldId(oldId));
        var plan = BasePlan(newId, entry);
        plan["execs"] = new JsonArray(new JsonObject {
            ["resource"] = resource,
            ["ensure"] = "absent",
            ["with"] = withFactory(entry["parameters"]!.AsObject()),
        });
        return (plan, newId);
    }

    private static JsonObject BasePlan(string newId, JsonObject template, string? tierOverride = null) {
        var risk = template["risk"]?.GetValue<string>() ?? "Medium";
        var tier = tierOverride ?? template["selectionTier"]?.GetValue<string>()
            ?? (risk == "High" ? "Expert" : "Standard");
        var plan = new JsonObject {
            ["schemaVersion"] = 2,
            ["id"] = newId,
            ["version"] = "2.0.0",
            ["title"] = template["title"]!.DeepClone(),
            ["description"] = template["description"]!.DeepClone(),
            ["group"] = template["category"]!.DeepClone(),
            ["risk"] = risk,
            ["tier"] = tier,
        };
        // requires/conflicts are copied raw and rewired to new ids afterwards.
        if (template["requires"] is JsonArray requires && requires.Count > 0) {
            plan["requires"] = requires.DeepClone();
        }
        if (template["conflicts"] is JsonArray conflicts && conflicts.Count > 0) {
            plan["conflicts"] = conflicts.DeepClone();
        }
        return plan;
    }

    private static void RewireReferences(JsonObject plan, IReadOnlyDictionary<string, string> idMap, List<string> warnings) {
        var planId = plan["id"]!.GetValue<string>();
        foreach (var field in new[] { "requires", "conflicts" }) {
            if (plan[field] is not JsonArray array) {
                continue;
            }
            var kept = new JsonArray();
            foreach (var node in array) {
                var oldRef = node!.GetValue<string>();
                if (idMap.TryGetValue(oldRef, out var newRef)) {
                    if (newRef != planId) {
                        kept.Add(newRef);
                    }
                }
                else {
                    warnings.Add($"{planId}: dropped {field} reference to '{oldRef}' (internal pair merge).");
                }
            }
            if (kept.Count > 0) {
                plan[field] = kept;
            }
            else {
                plan.Remove(field);
            }
        }
    }

    /// <summary>registry.delay-workstation-service → workstation; dism.remove-hyper-v → hyper-v.</summary>
    private static string SlugFromOldId(string oldId) {
        var body = oldId.Contains('.') ? oldId[(oldId.IndexOf('.') + 1)..] : oldId;
        foreach (var verb in ServiceVerbs) {
            if (body.StartsWith(verb + "-", StringComparison.Ordinal)) {
                body = body[(verb.Length + 1)..];
                break;
            }
        }
        if (body.EndsWith("-service", StringComparison.Ordinal)) {
            body = body[..^"-service".Length];
        }
        return body;
    }

    private static string UniqueId(HashSet<string> used, string candidate) {
        var final = candidate;
        for (var i = 2; used.Contains(final); i++) {
            final = $"{candidate}-{i}";
        }
        used.Add(final);
        return final;
    }

    private static string V1ModeToV2(string v1Mode) => v1Mode switch {
        "DelayedAuto" => "delayed",
        "Manual" => "manual",
        "Auto" => "auto",
        "Disabled" => "disabled",
        _ => "manual",
    };

    private static string ModeLabel(string mode) => mode switch {
        "delayed" => "延迟启动",
        "manual" => "手动",
        "auto" => "自动",
        _ => "禁用",
    };

    private static JsonObject Option(string value, string label, string risk) => new() {
        ["value"] = value,
        ["label"] = label,
        ["risk"] = risk,
    };

    private static JsonArray CopyArray(JsonObject parameters, string name) {
        var array = new JsonArray();
        if (parameters[name] is JsonArray source) {
            foreach (var item in source) {
                array.Add(item!.DeepClone());
            }
        }
        return array;
    }

    private static string ServiceSignature(JsonObject entry) {
        var parameters = entry["parameters"]!.AsObject();
        var targets = new List<string>();
        if (parameters["services"] is JsonArray services) {
            targets.AddRange(services.OfType<JsonValue>().Select(v => "s:" + v.GetValue<string>()));
        }
        if (parameters["servicePatterns"] is JsonArray patterns) {
            targets.AddRange(patterns.OfType<JsonValue>().Select(v => "p:" + v.GetValue<string>()));
        }
        targets.Sort(StringComparer.Ordinal);
        return string.Join("|", targets);
    }
}
