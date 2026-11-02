using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry.Research;

namespace AiVideoEditor.UI.Research;

/// <summary>Why a study video cannot be marked through the project.</summary>
public enum StudyAssetProblem
{
    NoLocation,
    NotInProject,
    SizeDiffers,
    Ambiguous,
}

/// <summary>A study video's project asset, or why there is none.</summary>
public readonly record struct StudyAssetMatch(MediaAsset? Asset, StudyAssetProblem? Problem)
{
    public bool Ok => Asset is not null;
}

/// <summary>
/// Which project asset shows a study video (Step 35.3R1): the asset at the study location's full path with the snapshot's
/// size — no match or several block the marking of that video. The file's SHA-256 is checked by the study service
/// (<c>Verified</c>), not here; a file name alone never identifies a video.
/// </summary>
public static class StudyAssetMap
{
    public static StudyAssetMatch Find(SyncStudy study, Guid videoKey, IEnumerable<MediaAsset> assets)
    {
        if (study.Draft.Video(videoKey) is not { } video || study.Location(videoKey) is not { } location)
            return new(null, StudyAssetProblem.NoLocation);
        var path = Full(location.CurrentPath);
        var atPath = assets.Where(a => a.Kind == MediaKind.Video && string.Equals(Full(a.FilePath), path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (atPath.Count == 0) return new(null, StudyAssetProblem.NotInProject);
        if (atPath.Count > 1) return new(null, StudyAssetProblem.Ambiguous);
        return atPath[0].FileSizeBytes == video.SizeBytes ? new(atPath[0], null) : new(null, StudyAssetProblem.SizeDiffers);
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
