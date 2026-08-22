using System.Text.Json.Nodes;

namespace TinyWin2.Core.Logging;

public enum BuildEventLevel {
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

/// <summary>
///     One structured, serializable build event. The CLI streams these as JSONL
///     (<c>--json-events</c>); the GUI renders them as the live progress/log feed.
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
        ["data"] = Data?.DeepClone()
    };
}

/// <summary>
///     Central structured log. The engine writes through it; sinks (Serilog file/console
///     bridge, JSONL stream for the GUI) subscribe via <see cref="Attach" />. Events are
///     pushed to sinks only — nothing is buffered, so a long build costs no memory.
/// </summary>
public sealed class BuildLog {
    private readonly Lock _gate = new();
    private readonly List<Action<BuildEvent>> _sinks = [];
    private string _phase = "init";
    private string? _planId;
    private int _sequence;

    /// <summary>Ambient context stamped onto every event unless overridden per call.</summary>
    public string Phase {
        set {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) {
                _phase = value;
            }
        }
    }

    public string? PlanId {
        set {
            lock (_gate) {
                _planId = value;
            }
        }
    }

    public void Debug(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
        => Write(BuildEventLevel.Debug, message, planId, layerIndex, data);

    public void Info(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
        => Write(BuildEventLevel.Info, message, planId, layerIndex, data);

    public void Warn(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
        => Write(BuildEventLevel.Warn, message, planId, layerIndex, data);

    public void Error(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
        => Write(BuildEventLevel.Error, message, planId, layerIndex, data);

    private void Write(BuildEventLevel level, string message, string? planId, int? layerIndex, JsonObject? data) {
        BuildEvent evt;
        Action<BuildEvent>[] sinks;
        lock (_gate) {
            evt = new() {
                Sequence = _sequence++,
                Timestamp = DateTimeOffset.UtcNow,
                Level = level,
                Phase = _phase,
                Message = message,
                PlanId = planId ?? _planId,
                LayerIndex = layerIndex,
                Data = data?.DeepClone().AsObject()
            };
            sinks = [.. _sinks];
        }

        foreach (var sink in sinks) {
            try {
                sink(evt);
            }
            catch {
                // A broken sink must never take down the build.
            }
        }
    }

    /// <summary>Attaches a sink; dispose the token to detach. Sinks must not throw.</summary>
    public IDisposable Attach(Action<BuildEvent> sink) {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate) {
            _sinks.Add(sink);
        }

        return new SinkToken(this, sink);
    }

    private void Detach(Action<BuildEvent> sink) {
        lock (_gate) {
            _sinks.Remove(sink);
        }
    }

    private sealed class SinkToken(BuildLog owner, Action<BuildEvent> sink) : IDisposable {
        private int _disposed;

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) {
                owner.Detach(sink);
            }
        }
    }
}
