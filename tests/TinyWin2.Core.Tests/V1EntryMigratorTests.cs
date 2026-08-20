using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class V1EntryMigratorTests {
    private static JsonObject V1Entry(string id, string handler, JsonObject parameters, Action<JsonObject>? mutate = null) {
        var entry = new JsonObject {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["version"] = "1.0.0",
            ["title"] = "T-" + id,
            ["description"] = "D-" + id,
            ["category"] = "TestCategory",
            ["risk"] = "Medium",
            ["handler"] = handler,
            ["phase"] = "MountedImage",
            ["parameters"] = parameters,
        };
        mutate?.Invoke(entry);
        return entry;
    }

    [Test]
    public async Task MergesDisableAndConfigurePairsIntoEnumArgPlan() {
        var output = V1EntryMigrator.Migrate([
            ("a.json", V1Entry("registry.delay-workstation-service", "Registry.ConfigureOfflineService",
                new JsonObject { ["services"] = new JsonArray("LanmanWorkstation"), ["startMode"] = "DelayedAuto" },
                o => { o["risk"] = "Medium"; o["selectionTier"] = "Standard"; })),
            ("b.json", V1Entry("registry.disable-workstation-service", "Registry.DisableOfflineService",
                new JsonObject { ["services"] = new JsonArray("LanmanWorkstation") },
                o => { o["risk"] = "High"; o["selectionTier"] = "Expert"; })),
        ]);

        await Assert.That(output.Plans.Count).IsEqualTo(1);
        var plan = output.Plans[0];
        await Assert.That(plan["id"]!.GetValue<string>()).IsEqualTo("service.workstation");
        await Assert.That(output.IdMap["registry.disable-workstation-service"]).IsEqualTo("service.workstation");
        await Assert.That(output.IdMap["registry.delay-workstation-service"]).IsEqualTo("service.workstation");

        var argument = plan["arguments"]![0]!.AsObject();
        await Assert.That(argument["default"]!.GetValue<string>()).IsEqualTo("delayed");
        var options = argument["options"]!.AsArray();
        await Assert.That(options[0]!["risk"]!.GetValue<string>()).IsEqualTo("Medium");
        await Assert.That(options[1]!["risk"]!.GetValue<string>()).IsEqualTo("High");

        // The merged plan validates as a v2 plan and its $map binds correctly.
        var definition = PlanDefinition.FromJson(plan);
        var bound = BuildPlanResolver.Resolve(new PlanCatalog([definition]),
            [new PlanSelection("service.workstation", Args: new Dictionary<string, JsonNode?> { ["startMode"] = "disabled" })],
            LayerGranularity.Plan);
        var exec = bound.Steps[0].Plans[0].Execs[0];
        await Assert.That(exec.Desired["start"]!.GetValue<string>()).IsEqualTo("disabled");
    }

    [Test]
    public async Task StandaloneDisableBecomesFixedDisabledPlan() {
        var output = V1EntryMigrator.Migrate([
            ("spooler.json", V1Entry("registry.disable-print-spooler", "Registry.DisableOfflineService",
                new JsonObject { ["services"] = new JsonArray("Spooler") }, o => o["risk"] = "High")),
        ]);

        var plan = output.Plans[0];
        await Assert.That(plan["id"]!.GetValue<string>()).IsEqualTo("service.print-spooler");
        await Assert.That(plan["tier"]!.GetValue<string>()).IsEqualTo("Expert"); // High risk → Expert (v1 rule baked in)
        var with = plan["execs"]![0]!["with"]!.AsObject();
        await Assert.That(with["start"]!.GetValue<string>()).IsEqualTo("disabled");
    }

    [Test]
    public async Task RegistryValueEntryConvertsHiveTypeAndData() {
        var output = V1EntryMigrator.Migrate([
            ("uac.json", V1Entry("registry.disable-uac", "Registry.SetOfflineValue", new JsonObject
            {
                ["hive"] = "SOFTWARE",
                ["key"] = "Microsoft\\Windows\\CurrentVersion\\Policies\\System",
                ["name"] = "EnableLUA",
                ["type"] = "DWord",
                ["value"] = 0,
            }, o => o["risk"] = "High")),
        ]);

        var plan = output.Plans[0];
        await Assert.That(plan["id"]!.GetValue<string>()).IsEqualTo("registry.uac");
        var with = plan["execs"]![0]!["with"]!.AsObject();
        await Assert.That(with["hive"]!.GetValue<string>()).IsEqualTo("software");
        var value = with["values"]![0]!.AsObject();
        await Assert.That(value["type"]!.GetValue<string>()).IsEqualTo("dword");
        await Assert.That(value["data"]!.GetValue<int>()).IsEqualTo(0);
    }

    [Test]
    public async Task FeatureAndCapabilityEntriesSplitByPrefix() {
        var output = V1EntryMigrator.Migrate([
            ("hv.json", V1Entry("dism.remove-hyper-v", "Dism.RemoveOptionalComponent", new JsonObject
            {
                ["features"] = new JsonArray("Microsoft-Hyper-V"),
                ["removePayload"] = true,
            })),
            ("ocr.json", V1Entry("dism.remove-chinese-ocr", "Dism.RemoveOptionalComponent", new JsonObject
            {
                ["capabilities"] = new JsonArray("Language.OCR~~~zh-CN~0.0.1.0"),
            })),
        ]);

        await Assert.That(output.Plans[0]["id"]!.GetValue<string>()).IsEqualTo("feature.hyper-v");
        await Assert.That(output.Plans[1]["id"]!.GetValue<string>()).IsEqualTo("capability.chinese-ocr");
    }

    [Test]
    public async Task CrossPlanConflictsRewireToNewIds() {
        var output = V1EntryMigrator.Migrate([
            ("a.json", V1Entry("dism.remove-foo", "Dism.RemoveOptionalComponent",
                new JsonObject { ["features"] = new JsonArray("FooFeature") },
                o => o["conflicts"] = new JsonArray("dism.remove-bar"))),
            ("b.json", V1Entry("dism.remove-bar", "Dism.RemoveOptionalComponent",
                new JsonObject { ["features"] = new JsonArray("BarFeature") })),
        ]);

        var plan = output.Plans[0];
        await Assert.That(plan["conflicts"]![0]!.GetValue<string>()).IsEqualTo("feature.bar");
    }

    [Test]
    public async Task MigratedPlansAllValidate() {
        var output = V1EntryMigrator.Migrate([
            ("x1.json", V1Entry("appx.remove-xbox", "Appx.RemoveProvisioned",
                new JsonObject { ["patterns"] = new JsonArray("Microsoft.Xbox*") })),
            ("x2.json", V1Entry("filesystem.remove-inetpub", "Filesystem.RemovePath",
                new JsonObject { ["paths"] = new JsonArray("inetpub") })),
            ("x3.json", V1Entry("driverstore.remove-modem", "DriverStore.RemoveInbox",
                new JsonObject { ["infNames"] = new JsonArray("mdm.inf") })),
            ("x4.json", V1Entry("dism.cleanup-components", "Dism.ComponentCleanup",
                new JsonObject { ["resetBase"] = false })),
            ("x5.json", V1Entry("dism.remove-pkg", "Dism.RemovePackage",
                new JsonObject { ["packagePatterns"] = new JsonArray("^Foo-Package~") })),
        ]);

        foreach (var plan in output.Plans) {
            _ = PlanDefinition.FromJson(plan); // throws on invalid
        }
        await Assert.That(output.Plans.Count).IsEqualTo(5);
    }
}
