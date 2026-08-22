using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;
using TinyWin2.Core.Profiles;

namespace TinyWin2.Core.Tests;

public sealed class ProfileTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();

    [Test]
    public async Task ToJsonFromJsonRoundTrips() {
        var parameters = new JsonObject { ["level"] = 3, ["note"] = "测试" };
        var profile = new Profile("my-profile", "desc", [
            new PlanSelection("fs.inetpub", false),
            new PlanSelection("dism.feature", true, new Dictionary<string, JsonNode?> {
                ["level"] = parameters["level"]!.DeepClone(),
                ["note"] = parameters["note"]!.DeepClone(),
            }),
        ]);
        var restored = Profile.FromJson(profile.ToJson());
        await Assert.That(restored.Name).IsEqualTo("my-profile");
        await Assert.That(restored.Description).IsEqualTo("desc");
        await Assert.That(restored.Selections).Count().IsEqualTo(2);
        await Assert.That(restored.Selections[0].PlanId).IsEqualTo("fs.inetpub");
        await Assert.That(restored.Selections[0].Enabled).IsFalse();
        await Assert.That(restored.Selections[1].Enabled).IsTrue();
        await Assert.That(restored.Selections[1].Parameters?["level"]?.GetValue<int>()).IsEqualTo(3);
        await Assert.That(restored.Selections[1].Parameters?["note"]?.GetValue<string>()).IsEqualTo("测试");
    }

    [Test]
    public async Task MissingNameThrows() {
        var ex = Assert.Throws<JsonException>(() => Profile.FromJson(new JsonObject { ["schemaVersion"] = 3 }));
        await Assert.That(ex.Message).Contains("'name'");
    }

    [Test]
    public async Task SelectionWithoutPlanIdThrows() {
        var obj = new JsonObject {
            ["schemaVersion"] = 3,
            ["name"] = "p",
            ["selections"] = new JsonArray(new JsonObject { ["enabled"] = true }),
        };
        var ex = Assert.Throws<JsonException>(() => Profile.FromJson(obj));
        await Assert.That(ex.Message).Contains("'planId'");
    }

    [Test]
    public async Task NonObjectSelectionsAreRejected() {
        var obj = new JsonObject {
            ["schemaVersion"] = 3,
            ["name"] = "p",
            ["selections"] = new JsonArray(
                new JsonObject { ["planId"] = "a" },
                (JsonNode)"just a string"),
        };
        var ex = Assert.Throws<JsonException>(() => Profile.FromJson(obj));
        await Assert.That(ex.Message).Contains("selection at index 1");
    }

    [Test]
    public async Task DuplicateSelectionsAreRejected() {
        var obj = new JsonObject {
            ["schemaVersion"] = 3,
            ["name"] = "p",
            ["selections"] = new JsonArray(
                new JsonObject { ["planId"] = "a" },
                new JsonObject { ["planId"] = "a" }),
        };
        var ex = Assert.Throws<JsonException>(() => Profile.FromJson(obj));
        await Assert.That(ex.Message).Contains("duplicate selection");
    }

    [Test]
    public async Task NullSelectionFieldsAreRejected() {
        var obj = new JsonObject {
            ["schemaVersion"] = 3,
            ["name"] = "p",
            ["selections"] = new JsonArray(new JsonObject {
                ["planId"] = "a",
                ["enabled"] = null,
            }),
        };
        var ex = Assert.Throws<JsonException>(() => Profile.FromJson(obj));
        await Assert.That(ex.Message).Contains("enabled must be a boolean");
    }

    [Test]
    public async Task SaveCreatesDirectoryAndLoadReadsBack() {
        var profile = new Profile("round", null, [new PlanSelection("a.b")]);
        var path = Path.Combine(_root, "nested", "dir", "profile.json");
        ProfileStore.Save(profile, path);
        await Assert.That(File.Exists(path)).IsTrue();
        var loaded = ProfileStore.Load(path);
        await Assert.That(loaded.Name).IsEqualTo("round");
        await Assert.That(loaded.Selections[0].PlanId).IsEqualTo("a.b");
    }

    [Test]
    public async Task LoadRejectsNonObjectJson() {
        var path = Path.Combine(_root, "bad.json");
        await File.WriteAllTextAsync(path, "[1,2,3]");
        var ex = Assert.Throws<JsonException>(() => ProfileStore.Load(path));
        await Assert.That(ex.Message).Contains("not a JSON object");
    }

    [Test]
    public async Task ToPlanSelectionsMapsEnabledAndParameters() {
        var profile = new Profile("p", null, [
            new PlanSelection("x", true, new Dictionary<string, JsonNode?> { ["k"] = "v" }),
            new PlanSelection("y", false),
        ]);
        var selections = ProfileStore.ToPlanSelections(profile);
        await Assert.That(selections[0].PlanId).IsEqualTo("x");
        await Assert.That(selections[0].Enabled).IsTrue();
        await Assert.That(selections[0].Parameters?["k"]?.GetValue<string>()).IsEqualTo("v");
        await Assert.That(selections[1].Enabled).IsFalse();
    }

    [Test]
    public async Task UnknownPlansReturnsDistinctUnknownIds() {
        var plansDir = Path.Combine(_root, "plans");
        Directory.CreateDirectory(plansDir);
        TestPlans.WritePlan(plansDir, "known.one");
        TestPlans.WritePlan(plansDir, "known.two");
        var catalog = PlanCatalog.LoadDirectory(plansDir);
        var profile = new Profile("p", null, [
            new PlanSelection("known.one"),
            new PlanSelection("ghost.plan"),
        ]);
        var unknown = ProfileStore.UnknownPlans(profile, catalog);
        await Assert.That(unknown).Count().IsEqualTo(1);
        await Assert.That(unknown[0]).IsEqualTo("ghost.plan");
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
