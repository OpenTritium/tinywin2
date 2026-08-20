using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;

namespace TinyWin2.Tools.MigrateV1;

/// <summary>One-shot migration: v1 entries/*.json → v2 plans/*.json + report.</summary>
internal static class Program {
    private static int Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("usage: tinywin2-migrate-v1 <v1-entries-dir> <v2-plans-dir> [--profile-out <file>]");
            return 2;
        }
        var entriesDir = args[0];
        var plansDir = args[1];
        var profileOut = args.Skip(2).ToList().IndexOf("--profile-out") is { } index && index >= 0 && index + 3 <= args.Length
            ? args[index + 3]
            : null;

        var files = Directory.GetFiles(entriesDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"migrating {files.Count} v1 entries from {entriesDir}");

        var entries = new List<(string, JsonObject)>();
        foreach (var file in files) {
            var node = JsonNode.Parse(File.ReadAllText(file));
            if (node is JsonObject obj) {
                entries.Add((Path.GetFileName(file), obj));
            }
        }

        var output = V1EntryMigrator.Migrate(entries);

        Directory.CreateDirectory(plansDir);
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        foreach (var plan in output.Plans) {
            var id = plan["id"]!.GetValue<string>();
            File.WriteAllText(Path.Combine(plansDir, id + ".json"), plan.ToJsonString(options));
        }
        Console.WriteLine($"wrote {output.Plans.Count} plans → {plansDir}");

        foreach (var warning in output.Warnings) {
            Console.WriteLine($"  warn: {warning}");
        }

        // Migration report: old→new id mapping for traceability.
        var report = new JsonObject {
            ["source"] = entriesDir,
            ["count"] = output.Plans.Count,
            ["map"] = new JsonObject(output.IdMap
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["warnings"] = new JsonArray(output.Warnings.Select(w => (JsonNode)w!).ToArray()),
        };
        var reportPath = Path.Combine(plansDir, "..", "migration-report.json");
        File.WriteAllText(reportPath, report.ToJsonString(options));
        Console.WriteLine($"report → {Path.GetFullPath(reportPath)}");

        // Optional: bundled profile selecting every Standard-tier plan (v1 常规档).
        if (profileOut is not null) {
            var standardPlans = output.Plans
                .Where(p => p["tier"]?.GetValue<string>() == "Standard")
                .Select(p => p["id"]!.GetValue<string>())
                .ToList();
            var profile = new JsonObject {
                ["schemaVersion"] = 1,
                ["name"] = "standard-safe",
                ["description"] = "v1 Standard tier equivalent: all low-risk common plans.",
                ["selections"] = new JsonArray(standardPlans.Select(id => (JsonNode)new JsonObject {
                    ["planId"] = id,
                    ["enabled"] = true,
                }).ToArray()),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(profileOut))!);
            File.WriteAllText(profileOut, profile.ToJsonString(options));
            Console.WriteLine($"bundled profile ({standardPlans.Count} plans) → {profileOut}");
        }
        return 0;
    }
}
