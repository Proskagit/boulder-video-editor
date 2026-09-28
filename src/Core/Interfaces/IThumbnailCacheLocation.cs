namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Where the thumbnails of a project are cached (D024 Step 9.4): a saved project keeps them in its own folder,
/// <c>&lt;project&gt;/cache/thumbnails</c>; a project that was never saved in an application-wide folder of its own,
/// <c>…/cache/unsaved/&lt;projectId&gt;/thumbnails</c>. Nothing about thumbnails is stored in <c>project.json</c>.
/// </summary>
public interface IThumbnailCacheLocation
{
    /// <summary>The thumbnail cache folder of the current project (not created until a thumbnail is written).</summary>
    string CurrentFolder { get; }

    /// <summary>Raised on the UI thread when <see cref="CurrentFolder"/> changed: another project became current
    /// (New / Open / Recover) or the project was saved to another folder (first Save, Save As).</summary>
    event EventHandler? Changed;

    /// <summary>At startup: removes the caches of unsaved projects that can't come back (no recovery file). Best
    /// effort — never throws.</summary>
    Task CleanUpUnsavedAsync();
}
