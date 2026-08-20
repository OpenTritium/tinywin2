using System.Text.Json.Nodes;

namespace TinyWin2.Core.Logging;

public enum BuildEventLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public interface IBuildLog
{
    string Phase { get; set; }
    int? LayerIndex { get; set; }
    string? PlanId { get; set; }
    void Write(BuildEventLevel level, string message, string? planId = null, int? layerIndex = null, JsonObject? data = null);
    void Debug(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null) => Write(BuildEventLevel.Debug, message, planId, layerIndex, data);
    void Info(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null) => Write(BuildEventLevel.Info, message, planId, layerIndex, data);
    void Warn(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null) => Write(BuildEventLevel.Warn, message, planId, layerIndex, data);
    void Error(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null) => Write(BuildEventLevel.Error, message, planId, layerIndex, data);
    IReadOnlyList<BuildEvent> Events { get; }
}

/// <summary>
/// Central structured log. The engine writes through it; sinks (console echo,
/// JSONL stream for the GUI, in-memory manifest log) subscribe via <see cref="Attach"/>.
/// </summary>
public sealed class BuildLog : IBuildLog
{
    private readonly object _gate = new();
    private readonly List<BuildEvent> _events = [];
    private readonly List<Action<BuildEvent>> _sinks = [];
    private int _sequence;

    public string Phase { get; set; } = "init";
    public int? LayerIndex { get; set; }
    public string? PlanId { get; set; }
    public bool EchoConsole { get; set; }

    public IReadOnlyList<BuildEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    /// <summary>Attaches a sink; dispose the token to detach. Sinks must not throw.</summary>
    public IDisposable Attach(Action<BuildEvent> sink)
    {
        lock (_gate) _sinks.Add(sink);
        return new SinkToken(this, sink);
    }

    public void Debug(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
        => Write(BuildEventLevel.Debug, message, planId, layerIndex, data);

    public void Info(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
        => Write(BuildEventLevel.Info, message, planId, layerIndex, data);

    public void Warn(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
        => Write(BuildEventLevel.Warn, message, planId, layerIndex, data);

    public void Error(string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
        => Write(BuildEventLevel.Error, message, planId, layerIndex, data);

    public void Write(BuildEventLevel level, string message, string? planId = null, int? layerIndex = null, JsonObject? data = null)
    {
        BuildEvent evt;
        Action<BuildEvent>[] sinks;
        lock (_gate)
        {
            evt = new BuildEvent
            {
                Sequence = _sequence++,
                Timestamp = DateTimeOffset.UtcNow,
                Level = level,
                Phase = Phase,
                Message = message,
                PlanId = planId ?? PlanId,
                LayerIndex = layerIndex ?? LayerIndex,
                Data = data,
            };
            _events.Add(evt);
            sinks = _sinks.ToArray();
        }

        if (EchoConsole)
        {
            EchoToConsole(evt);
        }
        foreach (var sink in sinks)
        {
            try
            {
                sink(evt);
            }
            catch
            {
                // A broken sink must never take down the build.
            }
        }
    }

    private static void EchoToConsole(BuildEvent evt)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = evt.Level switch
            {
                BuildEventLevel.Warn => ConsoleColor.Yellow,
                BuildEventLevel.Error => ConsoleColor.Red,
                BuildEventLevel.Debug => ConsoleColor.DarkGray,
                _ => ConsoleColor.Gray,
            };
            var prefix = evt.PlanId is null ? "" : $"[{evt.PlanId}] ";
            Console.WriteLine($"{evt.Timestamp:HH:mm:ss} {evt.Level.ToString().ToUpperInvariant(),5} {prefix}{evt.Message}");
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    public IReadOnlyList<BuildEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    private void Detach(Action<BuildEvent> sink)
    {
        lock (_gate) _sinks.Remove(sink);
    }

    private sealed class SinkToken(BuildLog owner, Action<BuildEvent> sink) : IDisposable
    {
        public void Dispose() => owner.Detach(sink);
    }
}
