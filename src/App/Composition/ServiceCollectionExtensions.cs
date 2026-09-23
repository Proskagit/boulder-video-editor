using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Media;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.Views;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.Video;
using Microsoft.Extensions.DependencyInjection;

namespace AiVideoEditor.App.Composition;

/// <summary>
/// Single place where the object graph is assembled. Only what actually has an
/// implementation gets registered. As each remaining subsystem (Timeline,
/// Effects, Export, ...) gains a real service in its phase, add its registration
/// here rather than scattering `new` calls around the UI layer.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAiVideoEditor(this IServiceCollection services)
    {
        // --- Core / subsystem services -----------------------------------------
        services.AddSingleton<IUndoRedoService, UndoRedoService>();
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IMediaImportService, MediaImportService>();
        services.AddSingleton<IFfprobeLocator, FfprobeLocator>();
        services.AddSingleton<IMediaAnalysisService, FfprobeMediaAnalysisService>();
        services.AddSingleton<ITimelineEditService, TimelineEditService>();

        // --- Later-phase registrations go here, e.g.: ---------------------------
        //   services.AddSingleton<IThumbnailService, ThumbnailService>();
        //   services.AddSingleton<IAutosaveService, AutosaveService>();
        //   services.AddSingleton<IPlaybackService, PlaybackService>();
        //   services.AddSingleton<IExportService, FfmpegExportService>();

        // --- UI-only services ---------------------------------------------------
        services.AddSingleton<IFilePickerService, AvaloniaFilePickerService>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<MediaAnalysisCoordinator>();
        services.AddSingleton<MediaImportWorkflow>();

        // --- UI view models -------------------------------------------------------
        services.AddTransient<ToolbarViewModel>();
        services.AddTransient<MediaBrowserViewModel>();
        services.AddTransient<PreviewViewModel>();
        services.AddTransient<InspectorViewModel>();
        services.AddTransient<TimelineViewModel>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        return services;
    }
}
