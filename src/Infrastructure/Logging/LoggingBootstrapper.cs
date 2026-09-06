using AiVideoEditor.Infrastructure.Configuration;
using Serilog;
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
}

/// <summary>
/// Configures Serilog once at startup. Call <see cref="CreateLogger"/> before the
/// host is built and pass the result into <c>UseSerilog</c> / the logging provider.
/// </summary>
public static class LoggingBootstrapper
{
    public static Serilog.ILogger CreateLogger()
    {
        var logsFolder = AppPaths.LogsFolder;

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Area", LogArea.App)

            // Rolling general application log.
            .WriteTo.File(
                Path.Combine(logsFolder, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({Area}) {Message:lj}{NewLine}{Exception}")

            // FFmpeg-specific log: only events explicitly tagged Area=Ffmpeg.
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => e.Properties.TryGetValue("Area", out var a) && a.ToString().Trim('"') == LogArea.Ffmpeg)
                .WriteTo.File(
                    Path.Combine(logsFolder, "ffmpeg-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14))

            // Export pipeline log: only events explicitly tagged Area=Export.
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => e.Properties.TryGetValue("Area", out var a) && a.ToString().Trim('"') == LogArea.Export)
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
}
