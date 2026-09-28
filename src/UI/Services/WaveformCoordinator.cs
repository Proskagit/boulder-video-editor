using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Makes and keeps the timeline's waveforms of the current project (D024 Step 9.5) — the orchestration between the
/// timeline, <see cref="IWaveformService"/> and the UI; no drawing here (that is the timeline view's, 9.5d).
/// <list type="bullet">
/// <item>Which assets (PO-W3): only media a clip on the timeline uses — any track, also hidden or muted ones (a muted
/// clip is still drawn, dimmed, PO-W2); nothing at import. Requested when the timeline changes (a clip added, moved to
/// the timeline by undo, …) and when media change (an analysis completing).</item>
/// <item>Audio files and video with an audio stream whose analysis completed get a waveform made. Offline media
/// (PO-W5) only ever gets a waveform cached earlier — never decoded; without one it gets none. Images, silent video,
/// pending or failed analysis get none.</item>
/// <item>A clip removed from the timeline doesn't cancel or drop its asset's waveform: an undo may bring it back, and
/// the result is kept for the project.</item>
/// <item>Generations, dedup, cache hits without a slot and at most <see cref="MaxConcurrentGenerations"/> makes at once —
/// slots of its own, independent of the thumbnails' (PO-W4): <see cref="MediaCacheCoordinator{T}"/>.</item>
/// </list>
/// </summary>
public sealed class WaveformCoordinator : MediaCacheCoordinator<Waveform>
{
    /// <summary>Waveforms made (decoded) at the same time (product owner, PO-W4).</summary>
    internal const int MaxConcurrentGenerations = 2;

    private readonly IWaveformService _waveforms;
    private readonly IWaveformCacheLocation _location;

    public WaveformCoordinator(IWaveformService waveforms, IProjectService projects, IWaveformCacheLocation location,
        ILogger<WaveformCoordinator> logger)
        : base(projects, MaxConcurrentGenerations, "waveform", logger)
    {
        _waveforms = waveforms;
        _location = location;
        projects.TimelineChanged += (_, _) => RequestAll();
        RequestAll();
    }

    /// <summary>Raised (on the caller's context) with the asset id when its waveform is ready — only for the current
    /// project.</summary>
    public event EventHandler<Guid>? WaveformReady
    {
        add => Ready += value;
        remove => Ready -= value;
    }

    /// <summary>The media the timeline's clips use, each once.</summary>
    protected override IEnumerable<MediaAsset> Candidates(Core.Entities.Project project)
    {
        var used = project.Timeline.VideoTracks.Concat(project.Timeline.AudioTracks)
            .SelectMany(t => t.Clips).OfType<MediaBackedClip>().Select(c => c.MediaAssetId).ToHashSet();
        return used.Count == 0 ? Array.Empty<MediaAsset>() : project.MediaAssets.Where(a => used.Contains(a.Id));
    }

    /// <summary>Audio or video with sound: made; offline audio or video: a cached one only.</summary>
    protected override bool MayHave(MediaAsset asset) =>
        asset.Kind is MediaKind.Audio or MediaKind.Video &&
        (asset.IsMissing
            ? asset.Kind == MediaKind.Audio || asset.Metadata?.AudioCodec is not null || asset.Metadata is null
            : asset.AnalysisStatus == MediaAnalysisStatus.Completed && asset.Metadata is not null &&
              (asset.Kind == MediaKind.Audio || asset.Metadata.AudioCodec is not null));

    protected override string CurrentFolder => _location.CurrentFolder;

    protected override Waveform? TryGetCached(MediaAsset asset, string folder) => _waveforms.TryGetCached(asset, folder);

    protected override Task<Waveform?> GetOrCreateAsync(MediaAsset asset, string folder, CancellationToken ct) =>
        _waveforms.GetOrCreateAsync(asset, folder, ct);
}
