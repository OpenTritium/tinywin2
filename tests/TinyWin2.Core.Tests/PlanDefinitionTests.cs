using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public static class TestPlans {
    public static string WritePlan(string directory, string id, Action<JsonObject>? mutate = null) {
        var obj = new JsonObject {
            ["schemaVersion"] = 2,
            ["id"] = id,
            ["version"] = "1.0.0",
            ["title"] = $"Plan {id}",
            ["description"] = $"Description for {id}",
            ["group"] = "TestGroup",
            ["risk"] = "Low",
            ["tier"] = "Standard",
            ["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "fs.path",
                ["ensure"] = "absent",
                ["with"] = new JsonObject { ["path"] = "Windows/Web/Wallpaper" },
            }),
        };
        mutate?.Invoke(obj);
        var path = Path.Combine(directory, $"{id.Replace('.', '_')}.json");
        File.WriteAllText(path, obj.ToJsonString());
        return path;
    }

    public static string CreateTempDirectory() {
        var path = Path.Combine(Path.GetTempPath(), "tinywin2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class PlanDefinitionTests {
    [Test]
    public async Task ParsesValidPlanWithArguments() {
        var obj = new JsonObject {
            ["schemaVersion"] = 2,
            ["id"] = "service.workstation",
            ["version"] = "1.2.3",
            ["title"] = "Workstation",
            ["description"] = "desc",
            ["group"] = "Networking",
            ["risk"] = "High",
            ["tier"] = "Expert",
            ["requires"] = new JsonArray("base.thing"),
            ["conflicts"] = new JsonArray("other.thing"),
            ["arguments"] = new JsonArray(new JsonObject {
                ["name"] = "startMode",
                ["type"] = "enum",
                ["label"] = "启动方式",
                ["default"] = "delayed",
                ["options"] = new JsonArray(
                    new JsonObject { ["value"] = "delayed", ["label"] = "延迟" },
                    new JsonObject { ["value"] = "manual", ["label"] = "手动" }),
            }),
            ["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "registry.service",
                ["ensure"] = "present",
                ["with"] = new JsonObject { ["services"] = new JsonArray("LanmanWorkstation") },
            }),
        };
        var plan = PlanDefinition.FromJson(obj);
        await Assert.That(plan.Id).IsEqualTo("service.workstation");
        await Assert.That(plan.Group).IsEqualTo("Networking");
        await Assert.That(plan.Arguments.Count).IsEqualTo(1);
        await Assert.That(plan.Arguments[0].Default!.GetValue<string>()).IsEqualTo("delayed");
        await Assert.That(plan.Execs.Count).IsEqualTo(1);
        await Assert.That(plan.Execs[0].Ensure).IsEqualTo(Ensure.Present);
    }

    [Test]
    public async Task MissingRequiredFieldReportsError() {
        var obj = new JsonObject {
            ["schemaVersion"] = 2,
            ["id"] = "ok.id",
            ["version"] = "1.0.0",
            // title/description/group/execs missing
        };
        var ex = Assert.Throws<PlanValidationException>(() => PlanDefinition.FromJson(obj))!;
        await Assert.That(ex.Message).Contains("'title'");
        await Assert.That(ex.Message).Contains("'execs'");
    }

    [Test]
    public async Task RejectsWrongSchemaVersion() {
        var obj = new JsonObject { ["schemaVersion"] = 1, ["id"] = "a.b", ["version"] = "1.0.0" };
        var ex = Assert.Throws<PlanValidationException>(() => PlanDefinition.FromJson(obj))!;
        await Assert.That(ex.Message).Contains("schemaVersion");
    }

    [Test]
    public async Task EnumArgumentWithoutOptionsIsRejected() {
        var obj = new JsonObject {
            ["schemaVersion"] = 2,
            ["id"] = "a.b",
            ["version"] = "1.0.0",
            ["title"] = "t",
            ["description"] = "d",
            ["group"] = "g",
            ["arguments"] = new JsonArray(new JsonObject {
                ["name"] = "mode",
                ["type"] = "enum",
                ["options"] = new JsonArray(),
            }),
            ["execs"] = new JsonArray(),
        };
        var ex = Assert.Throws<PlanValidationException>(() => PlanDefinition.FromJson(obj))!;
        await Assert.That(ex.Message).Contains("enum argument requires at least one option");
    }

    [Test]
    public async Task DefaultMustBeDeclaredOption() {
        var obj = new JsonObject {
            ["schemaVersion"] = 2,
            ["id"] = "a.b",
            ["version"] = "1.0.0",
            ["title"] = "t",
            ["description"] = "d",
            ["group"] = "g",
            ["execs"] = new JsonArray(),
            ["arguments"] = new JsonArray(new JsonObject {
                ["name"] = "mode",
                ["type"] = "enum",
                ["default"] = "nonexistent",
                ["options"] = new JsonArray(new JsonObject { ["value"] = "yes" }),
            }),
        };
        var ex = Assert.Throws<PlanValidationException>(() => PlanDefinition.FromJson(obj))!;
        await Assert.That(ex.Message).Contains("not one of the declared options");
    }
}

public sealed class PlanCatalogTests : IDisposable {
    private readonly string _directory = TestPlans.CreateTempDirectory();

    [Test]
    public async Task LoadsDirectoryAndRecordsHashes() {
        TestPlans.WritePlan(_directory, "alpha.one");
        TestPlans.WritePlan(_directory, "alpha.two");
        var catalog = PlanCatalog.LoadDirectory(_directory);
        await Assert.That(catalog.Plans.Count).IsEqualTo(2);
        await Assert.That(catalog.ById.ContainsKey("alpha.two")).IsTrue();
        await Assert.That(string.IsNullOrEmpty(catalog.Get("alpha.one").Sha256!)).IsFalse();
    }

    [Test]
    public async Task DuplicateIdsAreRejected() {
        // WritePlan derives the file name from the id, so park the first copy
        // under a different name before writing the colliding second file.
        TestPlans.WritePlan(_directory, "dup.thing", o => o["group"] = "A");
        File.Move(
            Path.Combine(_directory, "dup_thing.json"),
            Path.Combine(_directory, "dup_thing_copy.json"));
        TestPlans.WritePlan(_directory, "dup.thing", o => o["group"] = "B");
        var ex = Assert.Throws<PlanValidationException>(() => PlanCatalog.LoadDirectory(_directory))!;
        await Assert.That(ex.Message).Contains("duplicate plan id 'dup.thing'");
    }

    [Test]
    public async Task UnknownRequiresAreRejected() {
        TestPlans.WritePlan(_directory, "dep.user", o => o["requires"] = new JsonArray("missing.dep"));
        var ex = Assert.Throws<PlanValidationException>(() => PlanCatalog.LoadDirectory(_directory))!;
        await Assert.That(ex.Message).Contains("unknown plan 'missing.dep'");
    }

    public void Dispose() {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
