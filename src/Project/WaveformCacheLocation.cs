using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Project;

/// <summary>
/// <see cref="IWaveformCacheLocation"/> over the current project (D024 Step 9.5, PO-W4): saved
/// <c>&lt;project folder&gt;/cache/waveforms</c>, never saved <c>&lt;unsaved root&gt;/&lt;project id&gt;/waveforms</c> —
/// next to the thumbnails, with their life cycle (<see cref="MediaCacheLocation"/>: carry-over on Save / Save As, the
/// startup cleanup) and only ever touching <c>*.peaks</c> files.
/// </summary>
public sealed class WaveformCacheLocation : MediaCacheLocation, IWaveformCacheLocation
{
    public const string WaveformsFolderName = "waveforms";

    public WaveformCacheLocation(IProjectService projects, RecoveryStore recovery, string unsavedRoot, ILogger<WaveformCacheLocation> logger)
        : base(projects, recovery, unsavedRoot, WaveformsFolderName, "*.peaks", "waveform", logger)
    {
    }

    /// <summary><c>&lt;project folder&gt;/cache/waveforms</c>.</summary>
    public static string SavedFolder(string projectFolderPath) => SavedFolder(projectFolderPath, WaveformsFolderName);
}
