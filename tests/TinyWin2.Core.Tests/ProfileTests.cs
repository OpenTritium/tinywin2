using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Plans;
using TinyWin2.Core.Profiles;

namespace TinyWin2.Core.Tests;

public sealed class ProfileTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();

    [Test]
    public async Task ToJsonFromJsonRoundTrips() {
        var args = new JsonObject { ["level"] = 3, ["note"] = "测试" };
        var profile = new Profile("my-profile", "desc", [
            new ProfileSelection("fs.inetpub", false, null),
            new ProfileSelection("dism.feature", true, (JsonObject)args.DeepClone()),
        ]);
        var restored = Profile.FromJson(profile.ToJson());
        await Assert.That(restored.Name).IsEqualTo("my-profile");
        await Assert.That(restored.Description).IsEqualTo("desc");
        await Assert.That(restored.Selections).Count().IsEqualTo(2);
        await Assert.That(restored.Selections[0].PlanId).IsEqualTo("fs.inetpub");
        await Assert.That(restored.Selections[0].Enabled).IsFalse();
        await Assert.That(restored.Selections[1].Enabled).IsTrue();
        await Assert.That(restored.Selections[1].Args?["level"]?.GetValue<int>()).IsEqualTo(3);
        await Assert.That(restored.Selections[1].Args?["note"]?.GetValue<string>()).IsEqualTo("测试");
    }

    [Test]
    public async Task MissingNameThrows() {
        var ex = Assert.Throws<JsonException>(() => Profile.FromJson([]))!;
        await Assert.That(ex.Message).Contains("'name'");
    }

    [Test]
    public async Task SelectionWithoutPlanIdThrows() {
        var obj = new JsonObject {
            ["name"] = "p",
            ["selections"] = new JsonArray(new JsonObject { ["enabled"] = true }),
        };
        var ex = Assert.Throws<JsonException>(() => Profile.FromJson(obj))!;
        await Assert.That(ex.Message).Contains("'planId'");
    }

    [Test]
    public async Task EnabledDefaultsToTrueAndNonObjectSelectionsAreSkipped() {
        var obj = new JsonObject {
            ["name"] = "p",
            ["selections"] = new JsonArray(
                new JsonObject { ["planId"] = "a" },
                (JsonNode)"just a string"),
        };
        var profile = Profile.FromJson(obj);
        await Assert.That(profile.Selections).Count().IsEqualTo(1);
        await Assert.That(profile.Selections[0].Enabled).IsTrue();
    }

    [Test]
    public async Task SaveCreatesDirectoryAndLoadReadsBack() {
        var profile = new Profile("round", null, [new ProfileSelection("a.b", true, null)]);
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
        var ex = Assert.Throws<JsonException>(() => ProfileStore.Load(path))!;
        await Assert.That(ex.Message).Contains("not a JSON object");
    }

    [Test]
    public async Task ToPlanSelectionsMapsEnabledAndArgs() {
        var profile = new Profile("p", null, [
            new ProfileSelection("x", true, new JsonObject { ["k"] = "v" }),
            new ProfileSelection("y", false, null),
        ]);
        var selections = ProfileStore.ToPlanSelections(profile);
        await Assert.That(selections[0].PlanId).IsEqualTo("x");
        await Assert.That(selections[0].Enabled).IsTrue();
        await Assert.That(selections[0].Args?["k"]?.GetValue<string>()).IsEqualTo("v");
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
            new ProfileSelection("known.one", true, null),
            new ProfileSelection("ghost.plan", true, null),
            new ProfileSelection("ghost.plan", false, null),
        ]);
        var unknown = ProfileStore.UnknownPlans(profile, catalog);
        await Assert.That(unknown).Count().IsEqualTo(1);
        await Assert.That(unknown[0]).IsEqualTo("ghost.plan");
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
