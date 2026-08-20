using System.Text.Json.Nodes;
using TinyWin2.Core.Logging;

namespace TinyWin2.Core;

/// <summary>
/// One structured, serializable build event. The CLI streams these as JSONL
/// (<c>--json-events</c>); the GUI renders them as the live progress/log feed.
/// </summary>
public sealed record BuildEvent {
    public int Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public BuildEventLevel Level { get; init; }
    /// <summary>Coarse build phase, e.g. <c>prepare</c>, <c>base-layer</c>, <c>plan</c>, <c>capture</c>, <c>package</c>.</summary>
    public string Phase { get; init; } = "init";
    public string Message { get; init; } = "";
    public string? PlanId { get; init; }
    public int? LayerIndex { get; init; }
    public JsonObject? Data { get; init; }

    public JsonObject ToJson() => new() {
        ["seq"] = Sequence,
        ["ts"] = Timestamp.ToString("O"),
        ["level"] = Level.ToString().ToLowerInvariant(),
        ["phase"] = Phase,
        ["message"] = Message,
        ["planId"] = PlanId,
        ["layerIndex"] = LayerIndex,
        ["data"] = Data?.DeepClone(),
    };
}
