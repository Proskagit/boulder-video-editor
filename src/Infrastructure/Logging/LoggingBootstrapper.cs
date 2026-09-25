using AiVideoEditor.Infrastructure.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AiVideoEditor.Infrastructure.Logging;

/// <summary>
/// Well-known values for the "Area" log property, used to route log events to the
/// per-subsystem files described in the project spec (application log, FFmpeg log,
/// error log, export log).
/// </summary>
public static class LogArea
{
    public const string App = "App";
    public const string Ffmpeg = "Ffmpeg";
    public const string Export = "Export";

    /// <summary>
    /// The one routing rule for sources (D024 Step 9.3): everything logged from the Video subsystem — the ffmpeg /
    /// ffprobe processes, decoders and the export encoder — and the ffmpeg / ffprobe locators is FFmpeg diagnostics;
    /// every other source is the application's. An event that already carries an "Area" keeps it.
    /// </summary>
    public static string ForSource(string? sourceContext) =>
        sourceContext is not null &&
        (sourceContext.StartsWith("AiVideoEditor.Video.", StringComparison.Ordinal) ||
         sourceContext is "AiVideoEditor.Infrastructure.FfmpegLocator" or "AiVideoEditor.Infrastructure.FfprobeLocator")
            ? Ffmpeg
            : App;
}

/// <summary>Sets "Area" from the event's source (<see cref="LogArea.ForSource"/>) unless the event has one.</summary>
internal sealed class AreaEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string s } ? s : null;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Area", LogArea.ForSource(source)));
    }
}

/// <summary>
/// Configures Serilog once at startup. Call <see cref="CreateLogger()"/> before the
/// host is built and pass the result into <c>UseSerilog</c> / the logging provider.
/// </summary>
public static class LoggingBootstrapper
{
    public static Serilog.ILogger CreateLogger() => CreateLogger(AppPaths.LogsFolder);

    /// <summary>The application's logger writing into <paramref name="logsFolder"/> (tests use a temporary one).</summary>
    public static Serilog.ILogger CreateLogger(string logsFolder)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.With<AreaEnricher>()

            // Rolling general application log: everything except the FFmpeg diagnostics, which have their own file.
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(e => IsArea(e, LogArea.Ffmpeg))
                .WriteTo.File(
                    Path.Combine(logsFolder, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({Area}) {Message:lj}{NewLine}{Exception}"))

            // FFmpeg-specific log: events of Area=Ffmpeg (see LogArea.ForSource).
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => IsArea(e, LogArea.Ffmpeg))
                .WriteTo.File(
                    Path.Combine(logsFolder, "ffmpeg-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14))

            // Export pipeline log: only events explicitly tagged Area=Export.
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => IsArea(e, LogArea.Export))
                .WriteTo.File(
                    Path.Combine(logsFolder, "export-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14))

            // Errors from any subsystem, collected together for quick triage.
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => e.Level >= LogEventLevel.Error)
                .WriteTo.File(
                    Path.Combine(logsFolder, "errors-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30))

#if DEBUG
            .WriteTo.Debug()
#endif
            .CreateLogger();
    }

    private static bool IsArea(LogEvent e, string area) =>
        e.Properties.TryGetValue("Area", out var a) && a.ToString().Trim('"') == area;
}
