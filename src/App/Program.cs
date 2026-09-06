using AiVideoEditor.App.Composition;
using AiVideoEditor.Infrastructure.Logging;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AiVideoEditor.App;

internal static class Program
{
    // Avalonia needs a static, parameterless entry point that never touches the
    // service provider before the framework is fully initialized, hence Main is
    // split from BuildAvaloniaApp below (standard Avalonia bootstrapping pattern).
    [STAThread]
    public static void Main(string[] args)
    {
        // Configure Serilog first so any startup failure (including one thrown by
        // Avalonia itself) is still captured in the application log.
        Log.Logger = LoggingBootstrapper.CreateLogger();

        try
        {
            Log.Information("Starting AI Video Editor.");

            using var host = CreateHost();
            App.Services = host.Services;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly.");
        }
        finally
        {
            Log.Information("Shutting down.");
            Log.CloseAndFlush();
        }
    }

    private static IHost CreateHost()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);

        builder.Services.AddAiVideoEditor();

        return builder.Build();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
