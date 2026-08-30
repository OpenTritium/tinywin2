using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public static class TestPlans {
    /// <summary>One operation with the minimal spec its resource's binder accepts.</summary>
    public static JsonObject Operation(string resource = "fs.path", string action = "remove") {
        var spec = resource switch {
            "dism.capability" => new JsonObject { ["capabilities"] = new JsonArray("test.capability") },
            "dism.feature" => new JsonObject { ["features"] = new JsonArray("test.feature") },
            "dism.package" => new JsonObject { ["patterns"] = new JsonArray("test.package.*") },
            "appx.provisioned" => new JsonObject { ["patterns"] = new JsonArray("test.appx*") },
            "appx.system" => new JsonObject { ["patterns"] = new JsonArray("test.appx*") },
            "driver.store" => new JsonObject { ["infNames"] = new JsonArray("test.inf") },
            "dism.component-store" => new JsonObject(),
            _ => new JsonObject { ["paths"] = new JsonArray("Windows/Web/Wallpaper") }
        };
        return new() {
            ["resource"] = resource,
            ["action"] = action,
            ["spec"] = spec
        };
    }

    public static void WritePlan(string directory, string id, Action<JsonObject>? mutate = null) {
        var obj = new JsonObject {
            ["schemaVersion"] = 4,
            ["id"] = id,
            ["version"] = "1.0.0",
            ["title"] = $"Plan {id}",
            ["description"] = $"Description for {id}",
            ["category"] = "TestGroup",
            ["riskLevel"] = "Low",
            ["operations"] = new JsonArray(Operation())
        };
        mutate?.Invoke(obj);
        var path = Path.Combine(directory, $"{id.Replace('.', '_')}.json");
        File.WriteAllText(path, obj.ToJsonString());
    }

    public static string CreateTempDirectory() {
        var path = Path.Combine(Path.GetTempPath(), "tinywin2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class PlanDefinitionTests {
    [Test]
    public async Task ParsesValidPlanWithParameters() {
        var obj = new JsonObject {
            ["schemaVersion"] = 4,
            ["id"] = "service.workstation",
            ["version"] = "1.2.3",
            ["title"] = "Workstation",
            ["description"] = "desc",
            ["category"] = "Networking",
            ["riskLevel"] = "High",
            ["requires"] = new JsonArray("base.thing"),
            ["conflicts"] = new JsonArray("other.thing"),
            ["parameters"] = new JsonArray(new JsonObject {
                ["name"] = "startMode",
                ["type"] = "enum",
                ["label"] = "启动方式",
                ["default"] = "delayed",
                ["options"] = new JsonArray(
                    new JsonObject { ["value"] = "delayed", ["label"] = "延迟" },
                    new JsonObject { ["value"] = "manual", ["label"] = "手动" })
            }),
            ["operations"] = new JsonArray(new JsonObject {
                ["resource"] = "registry.service",
                ["action"] = "configure",
                ["spec"] = new JsonObject {
                    ["services"] = new JsonArray("LanmanWorkstation"),
                    ["start"] = "manual"
                }
            })
        };
        var plan = PlanDefinition.FromJson(obj);
        await Assert.That(plan.Id).IsEqualTo("service.workstation");
        await Assert.That(plan.Category).IsEqualTo("Networking");
        await Assert.That(plan.Parameters.Count).IsEqualTo(1);
        await Assert.That(plan.Parameters[0].Default!.GetValue<string>()).IsEqualTo("delayed");
        await Assert.That(plan.Operations[0].Action).IsEqualTo(OperationAction.Configure);
    }

    [Test]
    public async Task ParsesMultipleOperationsAcrossResources() {
        var obj = new JsonObject {
            ["schemaVersion"] = 4,
            ["id"] = "multi.op",
            ["version"] = "1.0.0",
            ["title"] = "t",
            ["description"] = "d",
            ["category"] = "System",
            ["riskLevel"] = "Low",
            ["operations"] = new JsonArray(
                new JsonObject {
                    ["resource"] = "dism.capability",
                    ["action"] = "remove",
                    ["spec"] = new JsonObject { ["capabilities"] = new JsonArray("App.Everything~~~~") }
                },
                new JsonObject {
                    ["resource"] = "dism.feature",
                    ["action"] = "remove",
                    ["spec"] = new JsonObject { ["features"] = new JsonArray("AppEverything") }
                })
        };
        var plan = PlanDefinition.FromJson(obj);
        await Assert.That(plan.Operations.Count).IsEqualTo(2);
        await Assert.That(plan.Operations[0].Resource).IsEqualTo("dism.capability");
        await Assert.That(plan.Operations[1].Resource).IsEqualTo("dism.feature");
    }

    [Test]
    public async Task MissingRequiredFieldReportsError() {
        var obj = new JsonObject { ["schemaVersion"] = 4, ["id"] = "ok.id", ["version"] = "1.0.0" };
        var ex = Assert.Throws<Exception>(() => PlanDefinition.FromJson(obj));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("'title'");
        await Assert.That(ex.Message).Contains("'operations'");
    }

    [Test]
    public async Task RejectsWrongSchemaVersion() {
        var obj = new JsonObject { ["schemaVersion"] = 3, ["id"] = "a.b", ["version"] = "1.0.0" };
        var ex = Assert.Throws<Exception>(() => PlanDefinition.FromJson(obj));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("schemaVersion");
    }

    [Test]
    public async Task RejectsEmptyOperationsArray() {
        var obj = new JsonObject {
            ["schemaVersion"] = 4,
            ["id"] = "a.b",
            ["version"] = "1.0.0",
            ["title"] = "t",
            ["description"] = "d",
            ["category"] = "g",
            ["operations"] = new JsonArray()
        };
        var ex = Assert.Throws<Exception>(() => PlanDefinition.FromJson(obj));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("at least one operation");
    }

    [Test]
    public async Task RejectsInvalidSecondOperation() {
        var obj = new JsonObject {
            ["schemaVersion"] = 4,
            ["id"] = "a.b",
            ["version"] = "1.0.0",
            ["title"] = "t",
            ["description"] = "d",
            ["category"] = "g",
            ["operations"] = new JsonArray(
                TestPlans.Operation(),
                new JsonObject { ["resource"] = "no executer", ["action"] = "remove" })
        };
        var ex = Assert.Throws<Exception>(() => PlanDefinition.FromJson(obj));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("operations[1]");
    }

    [Test]
    public async Task EnumParameterWithoutOptionsIsRejected() {
        var obj = new JsonObject {
            ["schemaVersion"] = 4,
            ["id"] = "a.b",
            ["version"] = "1.0.0",
            ["title"] = "t",
            ["description"] = "d",
            ["category"] = "g",
            ["parameters"] = new JsonArray(new JsonObject {
                ["name"] = "mode",
                ["type"] = "enum",
                ["options"] = new JsonArray()
            }),
            ["operations"] = new JsonArray(TestPlans.Operation())
        };
        var ex = Assert.Throws<Exception>(() => PlanDefinition.FromJson(obj));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("enum parameter requires at least one option");
    }

    [Test]
    public async Task DefaultMustBeDeclaredOption() {
        var obj = new JsonObject {
            ["schemaVersion"] = 4,
            ["id"] = "a.b",
            ["version"] = "1.0.0",
            ["title"] = "t",
            ["description"] = "d",
            ["category"] = "g",
            ["riskLevel"] = "Low",
            ["operations"] = new JsonArray(TestPlans.Operation()),
            ["parameters"] = new JsonArray(new JsonObject {
                ["name"] = "mode",
                ["type"] = "enum",
                ["default"] = "nonexistent",
                ["options"] = new JsonArray(new JsonObject { ["value"] = "yes" })
            })
        };
        var ex = Assert.Throws<Exception>(() => PlanDefinition.FromJson(obj));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("not one of the declared options");
    }

    [Test]
    public async Task UnknownPlanFieldIsRejected() {
        var obj = new JsonObject {
            ["schemaVersion"] = 4,
            ["id"] = "a.b",
            ["version"] = "1.0.0",
            ["title"] = "t",
            ["description"] = "d",
            ["category"] = "g",
            ["operations"] = new JsonArray(TestPlans.Operation()),
            ["legacyField"] = true
        };
        var ex = Assert.Throws<Exception>(() => PlanDefinition.FromJson(obj));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("unknown field 'legacyField'");
    }
}

public sealed class PlanCatalogTests : IDisposable {
    private readonly string _directory = TestPlans.CreateTempDirectory();

    public void Dispose() {
        try {
            Directory.Delete(_directory, true);
        }
        catch {
            /* best effort */
        }
    }

    [Test]
    public async Task LoadsDirectoryAndRecordsHashes() {
        TestPlans.WritePlan(_directory, "alpha.one");
        TestPlans.WritePlan(_directory, "alpha.two");
        var catalog = PlanCatalog.LoadDirectory(_directory);
        await Assert.That(catalog.Plans.Count).IsEqualTo(2);
        await Assert.That(catalog.ById.ContainsKey("alpha.two")).IsTrue();
        await Assert.That(catalog.Get("alpha.one").Hash).Length().IsEqualTo(16);
    }

    [Test]
    public async Task PlanHashIgnoresEditorSchemaMetadata() {
        TestPlans.WritePlan(_directory, "alpha.one");
        var path = Path.Combine(_directory, "alpha_one.json");
        var withoutSchema = PlanCatalog.LoadDirectory(_directory).Get("alpha.one").Hash;

        var plan = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        plan["$schema"] = "../schemas/plan-v4.schema.json";
        await File.WriteAllTextAsync(path, plan.ToJsonString());

        var withSchema = PlanCatalog.LoadDirectory(_directory).Get("alpha.one").Hash;
        await Assert.That(withSchema).IsEqualTo(withoutSchema);
    }

    [Test]
    public async Task DuplicateIdsAreRejected() {
        TestPlans.WritePlan(_directory, "dup.thing", o => o["category"] = "A");
        File.Move(Path.Combine(_directory, "dup_thing.json"), Path.Combine(_directory, "dup_thing_copy.json"));
        TestPlans.WritePlan(_directory, "dup.thing", o => o["category"] = "B");
        var ex = Assert.Throws<Exception>(() => PlanCatalog.LoadDirectory(_directory));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("duplicate plan id 'dup.thing'");
    }

    [Test]
    public async Task UnknownRequiresAreRejected() {
        TestPlans.WritePlan(_directory, "dep.user", o => o["requires"] = new JsonArray("missing.dep"));
        var ex = Assert.Throws<Exception>(() => PlanCatalog.LoadDirectory(_directory));
        await Assert.That(ex.GetType()).IsEqualTo(typeof(PlanValidationException));
        await Assert.That(ex.Message).Contains("unknown plan 'missing.dep'");
    }
}
