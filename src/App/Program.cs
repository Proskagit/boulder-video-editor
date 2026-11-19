using AiVideoEditor.App.Composition;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Infrastructure.Configuration;
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
    /// <summary>D070 §2: held while the application runs, so its installer (Inno Setup's <c>AppMutex</c>) asks to close it before
    /// it updates or removes the files. Several copies may run at once; the name is per session (no <c>Global\</c>).</summary>
    internal const string RunningMutexName = "BoulderVideoEditor.Running";

    // Avalonia needs a static, parameterless entry point that never touches the
    // service provider before the framework is fully initialized, hence Main is
    // split from BuildAvaloniaApp below (standard Avalonia bootstrapping pattern).
    [STAThread]
    public static void Main(string[] args)
    {
        using var running = new Mutex(false, RunningMutexName);

        // Configure Serilog first so any startup failure (including one thrown by
        // Avalonia itself) is still captured in the application log.
        // D068 §1: 1.0's per-user folder (settings, models, recovery, caches) moves under the new name before
        // anything — the logger included — writes into the new one.
        var migration = AppDataMigration.Result.Nothing;
        Exception? migrationError = null;
        try { migration = AppDataMigration.Run(); }
        catch (Exception ex) { migrationError = ex; }

        Log.Logger = LoggingBootstrapper.CreateLogger();

        try
        {
            Log.Information("Starting Boulder Video Editor.");
            if (migrationError is not null)
                Log.Error(migrationError, "Moving the old per-user folder {Old} failed.", AppPaths.LegacyAppDataRoot);
            else if (!ReferenceEquals(migration, AppDataMigration.Result.Nothing))
                Log.Information("Moved the old per-user folder {Old} into {New}: moved {Moved}, kept {Kept}, removed {Removed}, failures {Failures}.",
                    AppPaths.LegacyAppDataRoot, AppPaths.AppDataRoot, migration.Moved, migration.Kept, migration.OldFolderRemoved, migration.Failures);

            // D071 amendment 3: the proxies of the test builds 1.4.0 / 1.4.1 are no longer used — their folder is removed in the
            // background (best effort; a file in use stays until the next start).
            _ = Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(AppPaths.RetiredProxyCacheFolder))
                    {
                        Directory.Delete(AppPaths.RetiredProxyCacheFolder, recursive: true);
                        Log.Information("Removed the retired proxy cache {Folder}.", AppPaths.RetiredProxyCacheFolder);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "The retired proxy cache {Folder} could not be removed.", AppPaths.RetiredProxyCacheFolder);
                }
            });

            using var host = CreateHost(args);
            App.Services = host.Services;

            // D039 §1: the interface language comes from App:Language ("ru" by default), never from the OS.
            // D057 §3.8: the user's choice in «Настройки…» wins over App:Language.
            var language = host.Services.GetRequiredService<AiVideoEditor.Core.Interfaces.IUserSettingsService>().Current.Language
                ?? host.Services.GetRequiredService<IConfiguration>()["App:Language"] ?? "ru";
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
