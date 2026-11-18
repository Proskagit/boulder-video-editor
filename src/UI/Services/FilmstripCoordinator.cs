using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// D068 item 10: makes and keeps the filmstrips of the current project's videos for the timeline's clips — the rules of
/// <see cref="MediaCacheCoordinator{T}"/> (generations per project, once per asset, offline media only from the cache), one
/// filmstrip decoded at a time (it never competes with the Preview or the Media Browser's thumbnails for long). Kept in the
/// thumbnails' cache folder.
/// </summary>
public sealed class FilmstripCoordinator : MediaCacheCoordinator<Filmstrip>
{
    private readonly IFilmstripService _filmstrips;
    private readonly IThumbnailCacheLocation _location;

    public FilmstripCoordinator(IFilmstripService filmstrips, IProjectService projects, IThumbnailCacheLocation location,
        ILogger<FilmstripCoordinator> logger)
        : base(projects, maxConcurrent: 1, "filmstrip", logger)
    {
        _filmstrips = filmstrips;
        _location = location;
        RequestAll();
    }

    /// <summary>Raised (on the caller's context) with the asset id when its filmstrip is ready.</summary>
    public event EventHandler<Guid>? FilmstripReady
    {
        add => Ready += value;
        remove => Ready -= value;
    }

    protected override IEnumerable<MediaAsset> Candidates(Project project) => project.MediaAssets;

    protected override bool MayHave(MediaAsset asset) =>
        asset.Kind == MediaKind.Video &&
        (asset.IsMissing || asset.AnalysisStatus == MediaAnalysisStatus.Completed && asset.Metadata is not null);

    protected override string CurrentFolder => _location.CurrentFolder;

    protected override Filmstrip? TryGetCached(MediaAsset asset, string folder) => _filmstrips.TryGetCached(asset, folder);

    protected override Task<Filmstrip?> GetOrCreateAsync(MediaAsset asset, string folder, CancellationToken ct) =>
        _filmstrips.GetOrCreateAsync(asset, folder, ct);
}
