using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class PlanSelectionMergerTests {
    [Test]
    public async Task ProfileDisabledPlanCannotBeReenabledByExplicitSelection() {
        var ex = Assert.Throws<InvalidOperationException>(() => PlanSelectionMerger.Merge(
            [new("fs.recovery-environment", false)],
            [new("fs.recovery-environment")],
            Catalog("fs.recovery-environment")));

        await Assert.That(ex.Message).Contains("disabled by the selected profile");
    }

    [Test]
    public async Task ExplicitPlanIsAddedWhenProfileDoesNotMentionIt() {
        var selections = PlanSelectionMerger.Merge(
            [new("service.audio")],
            [new("service.workstation")],
            Catalog("service.audio", "service.workstation"));

        await Assert.That(selections.Select(selection => selection.PlanId))
            .IsEquivalentTo(["service.audio", "service.workstation"]);
    }

    [Test]
    public async Task UnknownExplicitPlanIsRejected() {
        var ex = Assert.Throws<ArgumentException>(() => PlanSelectionMerger.Merge(
            [], [new("ghost.plan")], Catalog("known.plan")));

        await Assert.That(ex.Message).Contains("unknown plan 'ghost.plan'");
    }

    [Test]
    public async Task EnsureSelectedAppendsAndHonoursProfileExclusions() {
        var catalog = Catalog("known.plan");
        var selections = new List<PlanSelection>();
        PlanSelectionMerger.EnsureSelected(selections, catalog, "known.plan");
        await Assert.That(selections[0].PlanId).IsEqualTo("known.plan");
        await Assert.That(selections[0].Enabled).IsTrue();

        selections[0] = selections[0] with { Enabled = false };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PlanSelectionMerger.EnsureSelected(selections, catalog, "known.plan"));
        await Assert.That(ex.Message).Contains("disabled by the selected profile");
    }

    private static PlanCatalog Catalog(params string[] ids) => new(
        [.. ids.Select(id => new PlanDefinition {
            Id = id,
            Version = "1.0.0",
            Title = id,
            Description = "d",
            Category = "t",
            Operations = [new PlanOperation("fs.path", OperationAction.Remove, new JsonObject())]
        })]);
}
