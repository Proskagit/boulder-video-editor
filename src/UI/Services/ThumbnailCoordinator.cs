using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Makes and keeps the Media Browser's thumbnails of the current project (D024 Step 9.4) — the orchestration between
/// the project's media, <see cref="IThumbnailService"/> and the UI; no bitmaps here (that is the view's, 9.4d).
/// Which assets: every video and image of the project with completed analysis gets a thumbnail made; offline media only
/// shows a cached one (never decoded); audio, pending or failed analysis get none. Analysis itself stays
/// <see cref="MediaAnalysisCoordinator"/>'s — a thumbnail is requested when an asset's analysis has completed
/// (<see cref="IProjectService.MediaAssetsChanged"/>). Generations, dedup, cache hits without a slot and at most
/// <see cref="MaxConcurrentGenerations"/> makes at once: <see cref="MediaCacheCoordinator{T}"/>.
/// </summary>
public sealed class ThumbnailCoordinator : MediaCacheCoordinator<Thumbnail>
{
    /// <summary>Thumbnails made (decoded) at the same time (product owner, PO-4).</summary>
    internal const int MaxConcurrentGenerations = 2;

    private readonly IThumbnailService _thumbnails;
    private readonly IThumbnailCacheLocation _location;

    public ThumbnailCoordinator(IThumbnailService thumbnails, IProjectService projects, IThumbnailCacheLocation location,
        ILogger<ThumbnailCoordinator> logger)
        : base(projects, MaxConcurrentGenerations, "thumbnail", logger)
    {
        _thumbnails = thumbnails;
        _location = location;
        RequestAll();
    }

    /// <summary>Raised (on the caller's context) with the asset id when its thumbnail is ready — only for the current
    /// project.</summary>
    public event EventHandler<Guid>? ThumbnailReady
    {
        add => Ready += value;
        remove => Ready -= value;
    }

    protected override IEnumerable<MediaAsset> Candidates(Core.Entities.Project project) => project.MediaAssets;

    /// <summary>Video or image: a thumbnail can exist (made, or cached for offline media).</summary>
    protected override bool MayHave(MediaAsset asset) =>
        asset.Kind is MediaKind.Video or MediaKind.Image &&
        (asset.IsMissing || asset.AnalysisStatus == MediaAnalysisStatus.Completed && asset.Metadata is not null);

    protected override string CurrentFolder => _location.CurrentFolder;

    protected override Thumbnail? TryGetCached(MediaAsset asset, string folder) => _thumbnails.TryGetCached(asset, folder);

    protected override Task<Thumbnail?> GetOrCreateAsync(MediaAsset asset, string folder, CancellationToken ct) =>
        _thumbnails.GetOrCreateAsync(asset, folder, ct);
}
