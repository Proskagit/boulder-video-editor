using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Views;
using AiVideoEditor.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AiVideoEditor.App.Composition;

/// <summary>
/// Single place where the object graph is assembled. Kept intentionally small in
/// Phase 0: only what actually has an implementation gets registered. As each
/// subsystem (Media, Timeline, Video, Export, Project, ...) gains a real service in
/// its phase, add its registration here rather than scattering `new` calls around
/// the UI layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAiVideoEditor(this IServiceCollection services)
    {
        // --- Core ------------------------------------------------------------
        services.AddSingleton<IUndoRedoService, UndoRedoService>();

        // --- Phase 2+ registrations go here, e.g.: --------------------------
        //   services.AddSingleton<IVideoEngine, FfmpegVideoEngine>();
        //   services.AddSingleton<IMediaImportService, MediaImportService>();
        //   services.AddSingleton<IThumbnailService, ThumbnailService>();
        //   services.AddSingleton<IProjectService, ProjectService>();
        //   services.AddSingleton<IAutosaveService, AutosaveService>();
        //   services.AddSingleton<IPlaybackService, PlaybackService>();
        //   services.AddSingleton<IExportService, FfmpegExportService>();

        // --- UI ---------------------------------------------------------------
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        return services;
    }
}
