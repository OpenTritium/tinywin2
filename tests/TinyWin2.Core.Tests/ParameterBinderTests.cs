using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Tests;

public sealed class ParameterBinderTests {
    private static IReadOnlyDictionary<string, JsonNode?> Parameters(params (string Key, JsonNode? Value)[] values)
        => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    [Test]
    public async Task BindsDirectParameterReference() {
        var spec = new JsonObject {
            ["path"] = new JsonObject { ["$parameter"] = "targetPath" },
        };
        var operation = new PlanOperation("fs.path", OperationAction.Remove, spec);
        var bound = ParameterBinder.BindOperation(operation.Spec, Parameters(("targetPath", "Windows/Foo")));
        await Assert.That(bound["path"]!.GetValue<string>()).IsEqualTo("Windows/Foo");
    }

    [Test]
    public async Task BindsMapWithCaseAndDefault() {
        var spec = new JsonObject {
            ["start"] = new JsonObject {
                ["$map"] = new JsonObject {
                    ["parameter"] = "startMode",
                    ["cases"] = new JsonObject { ["delayed"] = "delayedAuto", ["manual"] = "manual" },
                    ["default"] = "manual",
                },
            },
        };
        var operation = new PlanOperation("registry.service", OperationAction.Configure, spec);
        var delayed = ParameterBinder.BindOperation(operation.Spec, Parameters(("startMode", "delayed")));
        var unknown = ParameterBinder.BindOperation(operation.Spec, Parameters(("startMode", "unexpected")));
        await Assert.That(delayed["start"]!.GetValue<string>()).IsEqualTo("delayedAuto");
        await Assert.That(unknown["start"]!.GetValue<string>()).IsEqualTo("manual");
    }

    [Test]
    public async Task MapWithoutCaseAndDefaultThrows() {
        var spec = new JsonObject {
            ["start"] = new JsonObject {
                ["$map"] = new JsonObject {
                    ["parameter"] = "startMode",
                    ["cases"] = new JsonObject { ["delayed"] = "delayedAuto" },
                },
            },
        };
        var operation = new PlanOperation("registry.service", OperationAction.Configure, spec);
        var ex = Assert.Throws<ParameterBindingException>(() => ParameterBinder.BindOperation(operation.Spec, Parameters(("startMode", "manual"))));
        await Assert.That(ex.Message).Contains("no case for parameter 'startMode' value 'manual'");
    }

    [Test]
    public async Task BindsNestedArraysAndObjects() {
        var spec = new JsonObject {
            ["services"] = new JsonArray("A", new JsonObject { ["$parameter"] = "extra" }),
            ["nested"] = new JsonObject { ["deep"] = new JsonArray(new JsonObject { ["$parameter"] = "mode" }) },
        };
        var operation = new PlanOperation("registry.service", OperationAction.Configure, spec);
        var bound = ParameterBinder.BindOperation(operation.Spec, Parameters(("extra", "B"), ("mode", "auto")));
        await Assert.That(bound["services"]![1]!.GetValue<string>()).IsEqualTo("B");
        await Assert.That(bound["nested"]!["deep"]![0]!.GetValue<string>()).IsEqualTo("auto");
    }

    [Test]
    public async Task UnknownParameterReferenceThrows() {
        var spec = new JsonObject { ["x"] = new JsonObject { ["$parameter"] = "nope" } };
        var operation = new PlanOperation("fs.path", OperationAction.Remove, spec);
        var ex = Assert.Throws<ParameterBindingException>(() => ParameterBinder.BindOperation(operation.Spec, Parameters()));
        await Assert.That(ex.Message).Contains("unknown parameter 'nope'");
    }

    [Test]
    public async Task BindingDoesNotMutateTheOriginal() {
        var spec = new JsonObject { ["x"] = new JsonObject { ["$parameter"] = "mode" } };
        var operation = new PlanOperation("fs.path", OperationAction.Remove, spec);
        ParameterBinder.BindOperation(operation.Spec, Parameters(("mode", "auto")));
        await Assert.That(spec["x"]!.AsObject().ContainsKey("$parameter")).IsTrue();
    }
}
