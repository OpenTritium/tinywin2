using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class BuildPlanResolverTests : IDisposable {
    private readonly string _directory = TestPlans.CreateTempDirectory();

    private PlanCatalog Catalog(params string[] ids) {
        foreach (var id in ids) {
            TestPlans.WritePlan(_directory, id);
        }
        return PlanCatalog.LoadDirectory(_directory);
    }

    [Test]
    public async Task PlanGranularityMakesOneStepPerPlan() {
        var catalog = Catalog("a.one", "a.two", "b.three");
        var plan = BuildPlanResolver.Resolve(catalog,
            [new PlanSelection("a.one"), new PlanSelection("a.two"), new PlanSelection("b.three")],
            LayerGranularity.Plan);

        await Assert.That(plan.Steps.Count).IsEqualTo(3);
        await Assert.That(plan.Steps[0].IsComposite).IsFalse();
        await Assert.That(plan.PlanIds.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GroupGranularityBundlesByGroupInFirstAppearanceOrder() {
        var catalog = Catalog2(
            ("x.first", "GroupB"),
            ("x.second", "GroupA"),
            ("x.third", "GroupB"));

        var plan = BuildPlanResolver.Resolve(catalog,
            [new PlanSelection("x.second"), new PlanSelection("x.first"), new PlanSelection("x.third")],
            LayerGranularity.Group);

        await Assert.That(plan.Steps.Count).IsEqualTo(2);
        await Assert.That(plan.Steps[0].Id).IsEqualTo("group:GroupA");
        await Assert.That(plan.Steps[0].Plans.Count).IsEqualTo(1);
        await Assert.That(plan.Steps[1].Id).IsEqualTo("group:GroupB");
        await Assert.That(plan.Steps[1].Plans.Count).IsEqualTo(2);
        await Assert.That(plan.Steps[1].IsComposite).IsTrue();
    }

    [Test]
    public async Task RequiresArePulledInAndRunBeforeDependents() {
        var catalog = Catalog2(
            ("dep.dependent", "GroupA"),
            ("dep.base", "GroupB"));
        // dependent requires base
        TestPlans.WritePlan(_directory, "dep.dependent", o => o["requires"] = new JsonArray("dep.base"));
        var catalogWithDeps = PlanCatalog.LoadDirectory(_directory);

        var plan = BuildPlanResolver.Resolve(catalogWithDeps,
            [new PlanSelection("dep.dependent")],
            LayerGranularity.Plan);

        await Assert.That(plan.PlanIds.Count).IsEqualTo(2);
        await Assert.That(plan.PlanIds[0]).IsEqualTo("dep.base");
        await Assert.That(plan.PlanIds[1]).IsEqualTo("dep.dependent");
    }

    [Test]
    public async Task DependencyCycleIsRejected() {
        TestPlans.WritePlan(_directory, "cyc.a", o => o["requires"] = new JsonArray("cyc.b"));
        TestPlans.WritePlan(_directory, "cyc.b", o => o["requires"] = new JsonArray("cyc.a"));
        var catalog = PlanCatalog.LoadDirectory(_directory);

        var ex = Assert.Throws<PlanResolutionException>(
            () => BuildPlanResolver.Resolve(catalog, [new PlanSelection("cyc.a")], LayerGranularity.Plan))!;

        await Assert.That(ex.Message).Contains("cycle");
    }

    [Test]
    public async Task ConflictsBetweenEnabledPlansAreRejected() {
        TestPlans.WritePlan(_directory, "con.a", o => o["conflicts"] = new JsonArray("con.b"));
        TestPlans.WritePlan(_directory, "con.b");
        var catalog = PlanCatalog.LoadDirectory(_directory);

        var ex = Assert.Throws<PlanResolutionException>(
            () => BuildPlanResolver.Resolve(catalog, [new PlanSelection("con.a"), new PlanSelection("con.b")], LayerGranularity.Plan))!;

        await Assert.That(ex.Message).Contains("conflicts with");
    }

    [Test]
    public async Task ExplicitlyDisabledDependencyIsRejected() {
        TestPlans.WritePlan(_directory, "dis.dependent", o => o["requires"] = new JsonArray("dis.dep"));
        TestPlans.WritePlan(_directory, "dis.dep");
        var catalog = PlanCatalog.LoadDirectory(_directory);

        var ex = Assert.Throws<PlanResolutionException>(
            () => BuildPlanResolver.Resolve(catalog,
                [new PlanSelection("dis.dependent"), new PlanSelection("dis.dep", Enabled: false)],
                LayerGranularity.Plan))!;

        await Assert.That(ex.Message).Contains("explicitly disabled");
    }

    [Test]
    public async Task CrossGroupDependencyOrdersGroups() {
        TestPlans.WritePlan(_directory, "g1.provider", o => o["group"] = "GroupLate");
        TestPlans.WritePlan(_directory, "g2.consumer", o => {
            o["group"] = "GroupEarly";
            o["requires"] = new JsonArray("g1.provider");
        });
        var catalog = PlanCatalog.LoadDirectory(_directory);

        var plan = BuildPlanResolver.Resolve(catalog,
            [new PlanSelection("g2.consumer"), new PlanSelection("g1.provider")],
            LayerGranularity.Group);

        await Assert.That(plan.Steps.Count).IsEqualTo(2);
        await Assert.That(plan.Steps[0].Id).IsEqualTo("group:GroupLate");
        await Assert.That(plan.Steps[1].Id).IsEqualTo("group:GroupEarly");
    }

    [Test]
    public async Task CrossGroupCycleMergesIntoSingleStep() {
        TestPlans.WritePlan(_directory, "m1.a", o => {
            o["group"] = "GroupX";
            o["requires"] = new JsonArray("m2.b");
        });
        TestPlans.WritePlan(_directory, "m2.b", o => {
            o["group"] = "GroupY";
            o["requires"] = new JsonArray("m1.a");
        });
        var catalog = PlanCatalog.LoadDirectory(_directory);

        // The plan-level cycle is itself invalid; resolver must reject it clearly.
        var ex = Assert.Throws<PlanResolutionException>(
            () => BuildPlanResolver.Resolve(catalog, [new PlanSelection("m1.a")], LayerGranularity.Group))!;

        await Assert.That(ex.Message).Contains("cycle");
    }

    [Test]
    public async Task ArgumentsValidateAgainstOptions() {
        TestPlans.WritePlan(_directory, "arg.plan", o => {
            o["arguments"] = new JsonArray(new JsonObject {
                ["name"] = "mode",
                ["type"] = "enum",
                ["default"] = "safe",
                ["options"] = new JsonArray(
                    new JsonObject { ["value"] = "safe", ["label"] = "安全" },
                    new JsonObject { ["value"] = "hard", ["label"] = "激进" }),
            });
            o["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "fs.path",
                ["ensure"] = "absent",
                ["with"] = new JsonObject {
                    ["path"] = "X",
                    ["level"] = new JsonObject {
                        ["$map"] = new JsonObject {
                            ["arg"] = "mode",
                            ["cases"] = new JsonObject { ["safe"] = 1, ["hard"] = 2 },
                        },
                    },
                },
            });
        });
        var catalog = PlanCatalog.LoadDirectory(_directory);

        var bad = Assert.Throws<PlanResolutionException>(
            () => BuildPlanResolver.Resolve(catalog,
                [new PlanSelection("arg.plan", Args: new Dictionary<string, JsonNode?> { ["mode"] = "bogus" })],
                LayerGranularity.Plan))!;

        await Assert.That(bad.Message).Contains("argument 'mode'");

        var plan = BuildPlanResolver.Resolve(catalog,
            [new PlanSelection("arg.plan", Args: new Dictionary<string, JsonNode?> { ["mode"] = "hard" })],
            LayerGranularity.Plan);

        var exec = plan.Steps[0].Plans[0].Execs[0];
        await Assert.That(exec.Desired["level"]!.GetValue<int>()).IsEqualTo(2);
    }

    [Test]
    public async Task DefaultArgumentIsAppliedWhenUserOmitsIt() {
        TestPlans.WritePlan(_directory, "def.plan", o => {
            o["arguments"] = new JsonArray(new JsonObject {
                ["name"] = "mode",
                ["type"] = "enum",
                ["default"] = "safe",
                ["options"] = new JsonArray(
                    new JsonObject { ["value"] = "safe" },
                    new JsonObject { ["value"] = "hard" }),
            });
            o["execs"] = new JsonArray(new JsonObject {
                ["resource"] = "fs.path",
                ["ensure"] = "absent",
                ["with"] = new JsonObject { ["p"] = new JsonObject { ["$arg"] = "mode" } },
            });
        });
        var catalog = PlanCatalog.LoadDirectory(_directory);

        var plan = BuildPlanResolver.Resolve(catalog, [new PlanSelection("def.plan")], LayerGranularity.Plan);

        await Assert.That(plan.Steps[0].Plans[0].Execs[0].Desired["p"]!.GetValue<string>()).IsEqualTo("safe");
    }

    [Test]
    public async Task UnknownArgumentNameIsRejected() {
        TestPlans.WritePlan(_directory, "unk.plan");
        var catalog = PlanCatalog.LoadDirectory(_directory);

        var ex = Assert.Throws<PlanResolutionException>(
            () => BuildPlanResolver.Resolve(catalog,
                [new PlanSelection("unk.plan", Args: new Dictionary<string, JsonNode?> { ["nope"] = "x" })],
                LayerGranularity.Plan))!;

        await Assert.That(ex.Message).Contains("not declared");
    }

    private PlanCatalog Catalog2(params (string Id, string Group)[] entries) {
        foreach (var (id, group) in entries) {
            TestPlans.WritePlan(_directory, id, o => o["group"] = group);
        }
        return PlanCatalog.LoadDirectory(_directory);
    }

    public void Dispose() {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
