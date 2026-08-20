using System.Text.Json.Nodes;
using TinyWin2.Core.Logging;

namespace TinyWin2.Core.Tests;

public sealed class BuildLogTests {
    [Test]
    public async Task ToJsonMatchesTheJsonlWireContract() {
        var log = new BuildLog { Phase = "plan", PlanId = "fs.inetpub" };
        var events = new List<BuildEvent>();
        using var token = log.Attach(events.Add);
        log.Warn("hello 世界", layerIndex: 3, data: new JsonObject { ["progress"] = 55 });

        var json = events[0].ToJson();
        await Assert.That(json["seq"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(DateTimeOffset.Parse(json["ts"]!.GetValue<string>())).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        await Assert.That(json["level"]!.GetValue<string>()).IsEqualTo("warn");
        await Assert.That(json["phase"]!.GetValue<string>()).IsEqualTo("plan");
        await Assert.That(json["message"]!.GetValue<string>()).IsEqualTo("hello 世界");
        await Assert.That(json["planId"]!.GetValue<string>()).IsEqualTo("fs.inetpub");
        await Assert.That(json["layerIndex"]!.GetValue<int>()).IsEqualTo(3);
        await Assert.That(json["data"]!["progress"]!.GetValue<int>()).IsEqualTo(55);
    }

    [Test]
    public async Task PerCallContextOverridesTheAmbientOne() {
        var log = new BuildLog { Phase = "plan", PlanId = "ambient.plan" };
        var events = new List<BuildEvent>();
        using var token = log.Attach(events.Add);
        log.Info("a");
        log.Info("b", planId: "explicit.plan", layerIndex: 7);
        await Assert.That(events[0].PlanId).IsEqualTo("ambient.plan");
        await Assert.That(events[0].LayerIndex).IsNull();
        await Assert.That(events[1].PlanId).IsEqualTo("explicit.plan");
        await Assert.That(events[1].LayerIndex).IsEqualTo(7);
    }

    [Test]
    public async Task SequenceNumbersAreMonotonic() {
        var log = new BuildLog();
        var events = new List<BuildEvent>();
        using var token = log.Attach(events.Add);
        log.Debug("1");
        log.Info("2");
        log.Warn("3");
        log.Error("4");
        await Assert.That(events.Select(e => e.Sequence).ToArray()).IsEquivalentTo([0, 1, 2, 3]);
        await Assert.That(events.Select(e => e.Level).ToArray())
            .IsEquivalentTo([BuildEventLevel.Debug, BuildEventLevel.Info, BuildEventLevel.Warn, BuildEventLevel.Error]);
    }

    [Test]
    public async Task DisposingTheTokenDetachesTheSink() {
        var log = new BuildLog();
        var received = new List<BuildEvent>();
        var token = log.Attach(received.Add);
        log.Info("kept");
        token.Dispose();
        log.Info("dropped");
        await Assert.That(received).Count().IsEqualTo(1);
        await Assert.That(received[0].Message).IsEqualTo("kept");
    }

    [Test]
    public async Task ABrokenSinkNeverTakesDownTheBuild() {
        var log = new BuildLog();
        var received = new List<BuildEvent>();
        log.Attach(_ => throw new InvalidOperationException("sink bug"));
        using var token = log.Attach(received.Add);
        log.Info("survives");
        await Assert.That(received).Count().IsEqualTo(1);
        await Assert.That(received[0].Message).IsEqualTo("survives");
    }
}
