using AiVideoEditor.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Media;
using AiVideoEditor.Media.Caching;
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
using AiVideoEditor.Vision;
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
        services.AddSingleton<ITrainingImportService, TrainingImportService>();   // Phase Coros C1: the FIT training
        services.AddSingleton<IFfprobeLocator, FfprobeLocator>();
        services.AddSingleton<IMediaAnalysisService, FfprobeMediaAnalysisService>();
        services.AddSingleton<IFfmpegLocator, FfmpegLocator>();
        // HDR → SDR (D033): one LUT store for every decoder (the Preview, the thumbnails, the export).
        // Hold tracking (R2 → E, D4): the analysed camera-motion tracks kept between sessions, keyed by the video's content.
        services.AddSingleton<ICameraMotionCache>(sp => new CameraMotionCacheStore(AppPaths.CameraMotionCacheFolder,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CameraMotionCacheStore>>()));
        services.AddSingleton<IHdrLutStore>(sp => new HdrLutStore(AppPaths.ColorLutFolder,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HdrLutStore>>()));
        services.AddSingleton<IVideoDecoder, FfmpegVideoDecoder>();
        services.AddSingleton<IAudioDecoder, FfmpegAudioDecoder>();
        services.AddSingleton<IAudioOutput, WasapiAudioOutput>();
        services.AddSingleton<IReferenceClock, StopwatchReferenceClock>();
        services.AddSingleton<IPlaybackService, PlaybackService>();
        services.AddSingleton<ITimelineEditService, TimelineEditService>();
        // Phase 37 (D051 §5): the FIT mark actions are the edit service's, one instance.
        services.AddSingleton<IFitMarkEditService>(sp => (TimelineEditService)sp.GetRequiredService<ITimelineEditService>());
        services.AddSingleton<IMediaRelinkService, MediaRelinkService>();
        services.AddSingleton(_ => new RecoveryStore(AppPaths.RecoveryFolder));
        services.AddSingleton<IAutosaveService, AutosaveService>();
        // Recent projects (D026 §6): a per-user list in the configuration folder, created only when written.
        services.AddSingleton<IRecentProjectsStore>(sp => new RecentProjectsStore(AppPaths.RecentProjectsFile,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RecentProjectsStore>>()));
        // The global start calibration (D051 §6, Step 37.2): the second per-user file, created on its first write; one
        // instance serves the store and the estimate the mark commands read.
        services.AddSingleton<IFitCalibrationStore>(sp => new FitCalibrationStore(AppPaths.FitCalibrationFile,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FitCalibrationStore>>()));
        services.AddSingleton<IStartCorrectionSource>(sp => sp.GetRequiredService<IFitCalibrationStore>());
        // Phase 38 (D057 §2): the global settings and the working layout — per-user files, created on their first write.
        services.AddSingleton<IUserSettingsService>(sp => new UserSettingsService(AppPaths.UserSettingsFile,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UserSettingsService>>()));
        services.AddSingleton<IUiStateService>(sp => new UiStateService(AppPaths.UiStateFile,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UiStateService>>()));
        services.AddSingleton<FitCalibrationSaveSync>();     // Step 37.5: the saves' observations
        services.AddSingleton<IFitSettingsDialog, AvaloniaFitSettingsDialog>();
        services.AddSingleton<IAppSettingsDialog, AvaloniaAppSettingsDialog>();   // D057 §3.8: «Настройки…»
        services.AddSingleton<IYouTubeTimestampsDialog, AvaloniaYouTubeTimestampsDialog>();   // D057 §3.9
        services.AddSingleton<YouTubeTimestampsWorkflow>();
        services.AddSingleton<FitSettingsWorkflow>();        // Step 37.6: «Настройки FIT…»
        // D057 §3.8: the user's FFmpeg paths (settings.json) win over the application's (appsettings.json); applied at start.
        services.AddOptions<AiVideoEditor.Infrastructure.Configuration.FfmpegOptions>().PostConfigure<IUserSettingsService>((options, settings) =>
        {
            if (settings.Current.FfmpegPath is { } ffmpeg) options.FfmpegPath = ffmpeg;
            if (settings.Current.FfprobePath is { } ffprobe) options.FfprobePath = ffprobe;
        });

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
        services.AddSingleton<ITextBoxMeasurer, TextBoxMeasure>();     // D043 §3–§4: the text box by the painter's layout
        // AI hold markup (D034): the pinned model from the user's models folder, checked before every first use; the
        // markup frame decoded by the existing decoder.
        services.AddSingleton<IHoldSegmentationService>(sp => new OnnxHoldSegmentationService(
            Path.Combine(AppPaths.ModelsFolder, HoldModel.FolderName),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<OnnxHoldSegmentationService>>()));
        services.AddSingleton<IHoldMarkupFrameSource, HoldMarkupFrameSource>();
        // Phase 31 (D047): automatic hold proposals at 1920×1080 and full-resolution refinement of accepted proposals.
        services.AddSingleton<IHoldProposalService, HoldProposalService>();
        services.AddSingleton<IHoldProposalGenerator, HoldProposalGenerator>();
        services.AddSingleton<IHoldProposalRefiner, HoldProposalRefiner>();
        // D048 §2: the route selection at T — the frames by source time, the transfer to the clean frame.
        services.AddSingleton<IRouteSelectionFrameSource, RouteSelectionFrameSource>();
        services.AddSingleton<RouteSelectionService>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<EditingLock>();
        services.AddSingleton<InOutRangeService>();   // the session's In / Out range (D030 §8)
        services.AddSingleton<SourceViewerService>();  // the Source viewer's asset and source In / Out (D031)
        services.AddSingleton<IExportProgressDialog, AvaloniaExportProgressDialog>();
        services.AddSingleton<ExportWorkflow>();
        services.AddSingleton<MediaAnalysisCoordinator>();
        services.AddSingleton<ThumbnailCoordinator>();
        services.AddSingleton<CameraMotionCoordinator>();   // hold tracking (R2 → E): camera motion of clips with holds
        services.AddSingleton<WaveformCoordinator>();
        services.AddSingleton<MediaAvailabilityMonitor>();
        services.AddSingleton<MediaImportWorkflow>();
        services.AddSingleton<TrainingSessionService>();   // Phase 35: the project's training, its FIT resolved at runtime (D049)
        services.AddSingleton<TrainingImportWorkflow>();
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
        services.AddTransient<HoldMarkupViewModel>();
        services.AddTransient<PreviewTransformViewModel>();
        services.AddTransient<TimelineViewModel>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        return services;
    }
}
