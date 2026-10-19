using AiVideoEditor.App.Composition;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Infrastructure.Logging;
using Avalonia;
using Microsoft.Extensions.Configuration;
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

            using var host = CreateHost(args);
            App.Services = host.Services;

            // D039 §1: the interface language comes from App:Language ("ru" by default), never from the OS.
            var language = host.Services.GetRequiredService<IConfiguration>()["App:Language"] ?? "ru";
            if (!UiLanguage.Apply(language))
                Log.Warning("Unknown App:Language {Language}; the interface is in English.", language);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

            // Defensive only: playback is released before the window closes (MainWindowViewModel). The UI
            // dispatcher has stopped now, so anything that still awaited during the host's disposal below would
            // post its continuation to a context that never runs it again.
            SynchronizationContext.SetSynchronizationContext(null);
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

    private static IHost CreateHost(string[] args)
    {
        // D040 §1: appsettings.json from the application's folder, whatever the current directory.
        var builder = AppHostBuilder.Create(args);

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
