using AiVideoEditor.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Media;
using AiVideoEditor.Media.Thumbnails;
using AiVideoEditor.Media.Waveforms;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.UI.Rendering;
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
        services.AddSingleton<IFfmpegLocator, FfmpegLocator>();
        services.AddSingleton<IVideoDecoder, FfmpegVideoDecoder>();
        services.AddSingleton<IAudioDecoder, FfmpegAudioDecoder>();
        services.AddSingleton<IAudioOutput, WasapiAudioOutput>();
        services.AddSingleton<IReferenceClock, StopwatchReferenceClock>();
        services.AddSingleton<IPlaybackService, PlaybackService>();
        services.AddSingleton<ITimelineEditService, TimelineEditService>();
        services.AddSingleton<IMediaRelinkService, MediaRelinkService>();
        services.AddSingleton(_ => new RecoveryStore(AppPaths.RecoveryFolder));
        services.AddSingleton<IAutosaveService, AutosaveService>();
        // Recent projects (D026 §6): a per-user list in the configuration folder, created only when written.
        services.AddSingleton<IRecentProjectsStore>(sp => new RecentProjectsStore(AppPaths.RecentProjectsFile,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RecentProjectsStore>>()));

        // Export (D023): the service knows only the Core abstractions; the app picks the ffmpeg encoder and
        // the Avalonia offscreen rasterizer (one new instance per export job).
        services.AddSingleton<IExportEncoder, FfmpegExportEncoder>();
        services.AddSingleton<Func<ICompositionRasterizer>>(_ => () => new AvaloniaCompositionRasterizer());
        services.AddSingleton<IExportService, ExportService>();

        // Thumbnails (D024 Step 9.4): the service takes the cache folder from its caller; where that is for the
        // current project (saved: <project>/cache/thumbnails, unsaved: %LOCALAPPDATA%\…\cache\unsaved\<id>\thumbnails)
        // is ThumbnailCacheLocation's.
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IThumbnailCacheLocation>(sp => new ThumbnailCacheLocation(
            sp.GetRequiredService<IProjectService>(), sp.GetRequiredService<RecoveryStore>(), AppPaths.UnsavedCacheRoot,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ThumbnailCacheLocation>>()));

        // Waveforms (D024 Step 9.5): the service decodes through the app's audio decoder; the cache follows the
        // thumbnails' model next to them — <project>/cache/waveforms, unsaved …\cache\unsaved\<id>\waveforms.
        services.AddSingleton<IWaveformService, WaveformService>();
        services.AddSingleton<IWaveformCacheLocation>(sp => new WaveformCacheLocation(
            sp.GetRequiredService<IProjectService>(), sp.GetRequiredService<RecoveryStore>(), AppPaths.UnsavedCacheRoot,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WaveformCacheLocation>>()));

        // --- UI-only services ---------------------------------------------------
        services.AddSingleton<IFilePickerService, AvaloniaFilePickerService>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        services.AddSingleton<IFontCatalog, AvaloniaFontCatalog>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<EditingLock>();
        services.AddSingleton<InOutRangeService>();   // the session's In / Out range (D030 §8)
        services.AddSingleton<SourceViewerService>();  // the Source viewer's asset and source In / Out (D031)
        services.AddSingleton<IExportProgressDialog, AvaloniaExportProgressDialog>();
        services.AddSingleton<ExportWorkflow>();
        services.AddSingleton<MediaAnalysisCoordinator>();
        services.AddSingleton<ThumbnailCoordinator>();
        services.AddSingleton<WaveformCoordinator>();
        services.AddSingleton<MediaAvailabilityMonitor>();
        services.AddSingleton<MediaImportWorkflow>();
        services.AddSingleton<MediaRelinkWorkflow>();
        services.AddSingleton<ProjectFileWorkflow>();
        services.AddSingleton<IProjectSettingsDialog, AvaloniaProjectSettingsDialog>();
        services.AddSingleton<ProjectSettingsWorkflow>();

        // --- UI view models -------------------------------------------------------
        services.AddTransient<RecentProjectsViewModel>();
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
