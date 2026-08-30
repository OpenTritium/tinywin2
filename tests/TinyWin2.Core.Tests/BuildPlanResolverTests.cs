using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class BuildPlanResolverTests : IDisposable {
    private readonly string _directory = TestPlans.CreateTempDirectory();

    /// <summary>fs.path carries the bound-parameter operations these tests resolve; binding is real.</summary>
    private static readonly ExecuterRegistry Registry = new(new FakeProcessRunner());

    private static BuildPlan Resolve(PlanCatalog catalog, PlanSelection[] selections) =>
        BuildPlanResolver.Resolve(catalog, selections, Registry);

    public void Dispose() {
        try {
            Directory.Delete(_directory, true);
        }
        catch {
            /* best effort */
        }
    }

    private PlanCatalog Catalog(params string[] ids) {
        foreach (var id in ids) {
            TestPlans.WritePlan(_directory, id);
        }

        return PlanCatalog.LoadDirectory(_directory);
    }

    [Test]
    public async Task EveryResolvedPlanIsOneAtomicStep() {
        var catalog = Catalog("a.one", "a.two", "b.three");
        var plan = Resolve(catalog,
            [new("a.one"), new("a.two"), new("b.three")]);
        await Assert.That(plan.Steps.Count).IsEqualTo(3);
        await Assert.That(plan.Steps.All(s => s.Plan.Operations.Count == 1)).IsTrue();
        await Assert.That(plan.Steps.All(s => s.Plan.Operations[0].Resource.Length > 0)).IsTrue();
    }

    [Test]
    public async Task MultiOperationPlansResolveEveryOperationInOrder() {
        TestPlans.WritePlan(_directory, "multi.plan", o => {
            o["operations"] = new JsonArray(
                TestPlans.Operation("dism.capability"),
                TestPlans.Operation("dism.feature"));
        });
        var plan = Resolve(PlanCatalog.LoadDirectory(_directory), [new("multi.plan")]);
        await Assert.That(plan.Steps.Count).IsEqualTo(1);
        await Assert.That(plan.Steps[0].Plan.Operations.Select(o => o.Resource))
            .IsEquivalentTo(["dism.capability", "dism.feature"]);
    }

    [Test]
    public async Task RequiresArePulledInAndRunBeforeDependents() {
        TestPlans.WritePlan(_directory, "dep.dependent", o => o["requires"] = new JsonArray("dep.base"));
        TestPlans.WritePlan(_directory, "dep.base");
        var plan = Resolve(PlanCatalog.LoadDirectory(_directory),
            [new("dep.dependent")]);
        await Assert.That(plan.PlanIds).IsEquivalentTo(["dep.base", "dep.dependent"]);
        await Assert.That(plan.Steps.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DependencyCycleIsRejected() {
        TestPlans.WritePlan(_directory, "cyc.a", o => o["requires"] = new JsonArray("cyc.b"));
        TestPlans.WritePlan(_directory, "cyc.b", o => o["requires"] = new JsonArray("cyc.a"));
        var ex = Assert.Throws<PlanResolutionException>(() =>
            Resolve(PlanCatalog.LoadDirectory(_directory), [new("cyc.a")]));
        await Assert.That(ex.Message).Contains("cycle");
    }

    [Test]
    public async Task ConflictsBetweenEnabledPlansAreRejected() {
        TestPlans.WritePlan(_directory, "con.a", o => o["conflicts"] = new JsonArray("con.b"));
        TestPlans.WritePlan(_directory, "con.b");
        var ex = Assert.Throws<PlanResolutionException>(() =>
            Resolve(PlanCatalog.LoadDirectory(_directory),
                [new("con.a"), new("con.b")]));
        await Assert.That(ex.Message).Contains("conflicts with");
    }

    [Test]
    public async Task ExplicitlyDisabledDependencyIsRejected() {
        TestPlans.WritePlan(_directory, "dis.dependent", o => o["requires"] = new JsonArray("dis.dep"));
        TestPlans.WritePlan(_directory, "dis.dep");
        var ex = Assert.Throws<PlanResolutionException>(() =>
            Resolve(PlanCatalog.LoadDirectory(_directory),
                [new("dis.dependent"), new("dis.dep", false)]));
        await Assert.That(ex.Message).Contains("explicitly disabled");
    }

    [Test]
    public async Task CategoryDoesNotChangeAtomicExecutionOrder() {
        TestPlans.WritePlan(_directory, "cat.provider", o => o["category"] = "Late");
        TestPlans.WritePlan(_directory, "cat.consumer", o => {
            o["category"] = "Early";
            o["requires"] = new JsonArray("cat.provider");
        });
        var plan = Resolve(PlanCatalog.LoadDirectory(_directory),
            [new("cat.consumer")]);
        await Assert.That(plan.Steps.Select(s => s.Id)).IsEquivalentTo(["cat.provider", "cat.consumer"]);
    }

    [Test]
    public async Task ParametersValidateAndBindIntoOperationSpec() {
        TestPlans.WritePlan(_directory, "arg.plan", o => {
            o["parameters"] = new JsonArray(new JsonObject {
                ["name"] = "mode",
                ["type"] = "enum",
                ["default"] = "safe",
                ["options"] = new JsonArray(
                    new JsonObject { ["value"] = "safe", ["label"] = "安全" },
                    new JsonObject { ["value"] = "hard", ["label"] = "激进" })
            });
            o["operations"] = new JsonArray(new JsonObject {
                ["resource"] = "fs.path",
                ["action"] = "remove",
                ["spec"] = new JsonObject {
                    ["paths"] = new JsonArray("X"),
                    ["level"] = new JsonObject {
                        ["$map"] = new JsonObject {
                            ["parameter"] = "mode",
                            ["cases"] = new JsonObject { ["safe"] = 1, ["hard"] = 2 }
                        }
                    }
                }
            });
        });
        var catalog = PlanCatalog.LoadDirectory(_directory);
        var bad = Assert.Throws<PlanResolutionException>(() => Resolve(catalog,
            [new("arg.plan", Parameters: new Dictionary<string, JsonNode?> { ["mode"] = "bogus" })]));
        await Assert.That(bad.Message).Contains("parameter 'mode'");

        var plan = Resolve(catalog,
            [new("arg.plan", Parameters: new Dictionary<string, JsonNode?> { ["mode"] = "hard" })]);
        await Assert.That(plan.Steps[0].Plan.Operations[0].Spec.Spec["level"]!.GetValue<int>()).IsEqualTo(2);
    }

    [Test]
    public async Task DefaultParameterIsAppliedWhenUserOmitsIt() {
        TestPlans.WritePlan(_directory, "def.plan", o => {
            o["parameters"] = new JsonArray(new JsonObject {
                ["name"] = "mode",
                ["type"] = "enum",
                ["default"] = "safe",
                ["options"] = new JsonArray(new JsonObject { ["value"] = "safe" },
                    new JsonObject { ["value"] = "hard" })
            });
            o["operations"] = new JsonArray(new JsonObject {
                ["resource"] = "fs.path",
                ["action"] = "remove",
                ["spec"] = new JsonObject { ["paths"] = new JsonArray("X"), ["p"] = new JsonObject { ["$parameter"] = "mode" } }
            });
        });
        var plan = Resolve(PlanCatalog.LoadDirectory(_directory), [new("def.plan")]);
        await Assert.That(plan.Steps[0].Plan.Operations[0].Spec.Spec["p"]!.GetValue<string>()).IsEqualTo("safe");
    }

    [Test]
    public async Task UnknownParameterNameIsRejected() {
        TestPlans.WritePlan(_directory, "unk.plan");
        var ex = Assert.Throws<PlanResolutionException>(() => Resolve(
            PlanCatalog.LoadDirectory(_directory),
            [new("unk.plan", Parameters: new Dictionary<string, JsonNode?> { ["nope"] = "x" })]));
        await Assert.That(ex.Message).Contains("not declared");
    }

    [Test]
    public async Task DuplicateSelectionsAreRejected() {
        var catalog = Catalog("dup.plan");
        var ex = Assert.Throws<PlanResolutionException>(() => Resolve(catalog, [
            new("dup.plan"),
            new("dup.plan", Parameters: new Dictionary<string, JsonNode?>())
        ]));
        await Assert.That(ex.Message).Contains("selected more than once");
    }
}
