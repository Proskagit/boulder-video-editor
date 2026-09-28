using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project;

/// <summary>
/// <see cref="IThumbnailCacheLocation"/> over the current project (D024 Step 9.4, PO-1 / PO-2 / PO-6): saved
/// <c>&lt;project folder&gt;/cache/thumbnails</c> — the one path for thumbnails in a project folder —, never saved
/// <c>&lt;unsaved root&gt;/&lt;project id&gt;/thumbnails</c>; carry-over on Save / Save As and the startup cleanup as
/// <see cref="MediaCacheLocation"/> describes.
/// </summary>
public sealed class ThumbnailCacheLocation : MediaCacheLocation, IThumbnailCacheLocation
{
    public const string ThumbnailsFolderName = "thumbnails";

    public ThumbnailCacheLocation(IProjectService projects, RecoveryStore recovery, string unsavedRoot, ILogger<ThumbnailCacheLocation> logger)
        : base(projects, recovery, unsavedRoot, ThumbnailsFolderName, "*.thumb", "thumbnail", logger)
    {
    }

    /// <summary><c>&lt;project folder&gt;/cache/thumbnails</c>.</summary>
    public static string SavedFolder(string projectFolderPath) => SavedFolder(projectFolderPath, ThumbnailsFolderName);
}
