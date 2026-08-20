using Serilog;
using Serilog.Events;
using TinyWin2.Core.Logging;

namespace TinyWin2.Core;

public static class SerilogBuildLogExtensions
{
    /// <summary>
    /// Bridges every <see cref="BuildEvent"/> into a Serilog pipeline:
    /// colored console + a rolling-free, UTF-8 log file with the structured
    /// domain fields (phase/planId/layerIndex) as Serilog properties.
    /// Console output is suppressed when the JSONL event stream owns stdout.
    /// </summary>
    /// <returns>The sink token; dispose to flush and close the file.</returns>
    public static IDisposable UseSerilog(
        this BuildLog log,
        string logFilePath,
        bool echoConsole = true,
        LogEventLevel minimumLevel = LogEventLevel.Debug)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.File(
                logFilePath,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                encoding: System.Text.Encoding.UTF8);
        if (echoConsole)
        {
            configuration = configuration.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }
        var logger = configuration.CreateLogger();

        LogEventLevel ToSerilogLevel(BuildEventLevel level) => level switch
        {
            BuildEventLevel.Debug => LogEventLevel.Debug,
            BuildEventLevel.Info => LogEventLevel.Information,
            BuildEventLevel.Warn => LogEventLevel.Warning,
            _ => LogEventLevel.Error,
        };

        var token = log.Attach(evt =>
        {
            var level = ToSerilogLevel(evt.Level);
            if (!logger.IsEnabled(level))
            {
                return;
            }
            logger
                .ForContext("phase", evt.Phase)
                .ForContext("planId", evt.PlanId)
                .ForContext("layerIndex", evt.LayerIndex)
                .Write(level, exception: null, messageTemplate: "{message}", propertyValue: evt.Message);
        });
        return new CompositeDisposable(token, logger);
    }

    private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }
        }
    }
}
