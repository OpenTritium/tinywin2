using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;

namespace TinyWin2.Core.Pipeline;

/// <summary>What one plan's operations did; serialized into the layer manifest unchanged.</summary>
public sealed record PlanExecutionResult(string PlanId, IReadOnlyList<OperationOutcome> Operations) {
    public JsonObject ToJson() => new() {
        ["planId"] = PlanId,
        ["operations"] = new JsonArray(Operations.Select(o => (JsonNode)o.ToJson()).ToArray())
    };
}

/// <summary>The recorded outcome of one bound operation inside a plan step.</summary>
public sealed record OperationOutcome(
    string Resource,
    OperationAction Action,
    bool Skipped,
    IReadOnlyList<ChangeItem> Changes,
    string? SkipReason) {
    public JsonObject ToJson() => new() {
        ["resource"] = Resource,
        ["action"] = Action.ToString().ToLowerInvariant(),
        ["status"] = Skipped ? "skipped" : "applied",
        ["changes"] = new JsonArray(Changes.Select(c => (JsonNode)c.ToJson()).ToArray()),
        ["skipReason"] = SkipReason
    };
}
