using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class PlanSelectionMergerTests {
    [Test]
    public async Task ProfileDisabledPlanCannotBeReenabledByExplicitSelection() {
        var ex = Assert.Throws<InvalidOperationException>(() => PlanSelectionMerger.Merge(
            [new("fs.recovery-environment", false)],
            [new("fs.recovery-environment")]));

        await Assert.That(ex.Message).Contains("disabled by the selected profile");
    }

    [Test]
    public async Task ExplicitPlanIsAddedWhenProfileDoesNotMentionIt() {
        var selections = PlanSelectionMerger.Merge(
            [new("service.audio")],
            [new("service.workstation")]);

        await Assert.That(selections.Select(selection => selection.PlanId))
            .IsEquivalentTo(["service.audio", "service.workstation"]);
    }
}
