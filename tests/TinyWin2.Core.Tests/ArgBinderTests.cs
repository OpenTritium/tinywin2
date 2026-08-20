using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class ArgBinderTests
{
    private static IReadOnlyDictionary<string, JsonNode?> Args(params (string Key, JsonNode? Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    [Test]
    public async Task BindsDirectArgReference()
    {
        var with = new JsonObject
        {
            ["path"] = new JsonObject { ["$arg"] = "targetPath" },
        };
        var planExec = new PlanExec("fs.path", Ensure.Absent, with);

        var bound = ArgBinder.BindExec(planExec, Args(("targetPath", "Windows/Foo")));

        await Assert.That(bound["path"]!.GetValue<string>()).IsEqualTo("Windows/Foo");
    }

    [Test]
    public async Task BindsMapWithCaseAndDefault()
    {
        var with = new JsonObject
        {
            ["start"] = new JsonObject
            {
                ["$map"] = new JsonObject
                {
                    ["arg"] = "startMode",
                    ["cases"] = new JsonObject { ["delayed"] = "delayedAuto", ["manual"] = "manual" },
                    ["default"] = "manual",
                },
            },
        };
        var planExec = new PlanExec("registry.service", Ensure.Present, with);

        var delayed = ArgBinder.BindExec(planExec, Args(("startMode", "delayed")));
        var unknown = ArgBinder.BindExec(planExec, Args(("startMode", "unexpected")));

        await Assert.That(delayed["start"]!.GetValue<string>()).IsEqualTo("delayedAuto");
        await Assert.That(unknown["start"]!.GetValue<string>()).IsEqualTo("manual");
    }

    [Test]
    public async Task MapWithoutCaseAndDefaultThrows()
    {
        var with = new JsonObject
        {
            ["start"] = new JsonObject
            {
                ["$map"] = new JsonObject
                {
                    ["arg"] = "startMode",
                    ["cases"] = new JsonObject { ["delayed"] = "delayedAuto" },
                },
            },
        };
        var planExec = new PlanExec("registry.service", Ensure.Present, with);

        var ex = Assert.Throws<ArgBindException>(() => ArgBinder.BindExec(planExec, Args(("startMode", "manual"))))!;

        await Assert.That(ex.Message).Contains("no case for argument 'startMode' value 'manual'");
    }

    [Test]
    public async Task BindsNestedArraysAndObjects()
    {
        var with = new JsonObject
        {
            ["services"] = new JsonArray("A", new JsonObject { ["$arg"] = "extra" }),
            ["nested"] = new JsonObject { ["deep"] = new JsonArray(new JsonObject { ["$arg"] = "mode" }) },
        };
        var planExec = new PlanExec("registry.service", Ensure.Present, with);

        var bound = ArgBinder.BindExec(planExec, Args(("extra", "B"), ("mode", "auto")));

        await Assert.That(bound["services"]![1]!.GetValue<string>()).IsEqualTo("B");
        await Assert.That(bound["nested"]!["deep"]![0]!.GetValue<string>()).IsEqualTo("auto");
    }

    [Test]
    public async Task UnknownArgReferenceThrows()
    {
        var with = new JsonObject { ["x"] = new JsonObject { ["$arg"] = "nope" } };
        var planExec = new PlanExec("fs.path", Ensure.Absent, with);

        var ex = Assert.Throws<ArgBindException>(() => ArgBinder.BindExec(planExec, Args()))!;

        await Assert.That(ex.Message).Contains("unknown argument 'nope'");
    }

    [Test]
    public async Task BindingDoesNotMutateTheOriginal()
    {
        var with = new JsonObject { ["x"] = new JsonObject { ["$arg"] = "mode" } };
        var planExec = new PlanExec("fs.path", Ensure.Absent, with);

        ArgBinder.BindExec(planExec, Args(("mode", "auto")));

        await Assert.That(with["x"]!.AsObject().ContainsKey("$arg")).IsTrue();
    }
}
