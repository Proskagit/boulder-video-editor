namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Where one kind of media cache (thumbnails, waveforms — D024 Steps 9.4 / 9.5) of the current project lives: a saved
/// project keeps it in its own folder, <c>&lt;project&gt;/cache/&lt;kind&gt;</c>; a project that was never saved in an
/// application-wide folder of its own, <c>…/cache/unsaved/&lt;projectId&gt;/&lt;kind&gt;</c>. Nothing about these
/// caches is stored in <c>project.json</c>.
/// </summary>
public interface IMediaCacheLocation
{
    /// <summary>The cache folder of the current project (not created until a file is written).</summary>
    string CurrentFolder { get; }

    /// <summary>Raised on the UI thread when <see cref="CurrentFolder"/> changed: another project became current
    /// (New / Open / Recover) or the project was saved to another folder (first Save, Save As).</summary>
    event EventHandler? Changed;

    /// <summary>At startup: removes this kind's caches of unsaved projects that can't come back (no recovery file).
    /// Best effort — never throws.</summary>
    Task CleanUpUnsavedAsync();
}

/// <summary>Where the thumbnails of a project are cached (D024 Step 9.4): <c>&lt;project&gt;/cache/thumbnails</c>, or
/// <c>…/cache/unsaved/&lt;projectId&gt;/thumbnails</c> for a project that was never saved.</summary>
public interface IThumbnailCacheLocation : IMediaCacheLocation
{
}

/// <summary>Where the waveforms of a project are cached (D024 Step 9.5, PO-W4): <c>&lt;project&gt;/cache/waveforms</c>,
/// or <c>…/cache/unsaved/&lt;projectId&gt;/waveforms</c> for a project that was never saved — the thumbnails' life
/// cycle.</summary>
public interface IWaveformCacheLocation : IMediaCacheLocation
{
}
