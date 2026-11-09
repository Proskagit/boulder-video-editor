using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Telemetry;

/// <summary>
/// D057 §3.2: the order several files go onto the timeline — the chronological order of their recording, by the file's own
/// time of D054 / D055 (the recording-time tag read as the end of the recording, else the file-name time under
/// <c>fileNameOffset</c> — the training's UTC offset when there is one, else the names compared as they are); the files
/// without a time follow in the given order. A stable order: equal times keep the given order. Never reads the system's
/// time zone (D049 §1).
/// </summary>
public static class MediaRecordingOrder
{
    public static IReadOnlyList<MediaAsset> Order(IReadOnlyList<MediaAsset> assetsInListOrder, TimeSpan? fileNameOffset)
    {
        ArgumentNullException.ThrowIfNull(assetsInListOrder);
        var offset = fileNameOffset ?? TimeSpan.Zero;
        var timed = new List<(MediaAsset Asset, DateTimeOffset Time, int Index)>();
        var untimed = new List<MediaAsset>();
        for (var i = 0; i < assetsInListOrder.Count; i++)
        {
            var asset = assetsInListOrder[i];
            if (TimeOf(asset, offset) is { } time) timed.Add((asset, time, i));
            else untimed.Add(asset);
        }
        return timed.OrderBy(t => t.Time).ThenBy(t => t.Index).Select(t => t.Asset).Concat(untimed).ToList();
    }

    /// <summary>The instant a file's recording started, or null (an image, no tag and no time in its name).</summary>
    public static DateTimeOffset? TimeOf(MediaAsset asset, TimeSpan fileNameOffset)
    {
        if (asset.Kind == MediaKind.Image) return null;
        var candidates = RecordingZeroEstimator.Candidates(asset, fileNameOffset);
        return candidates.TagMarksEnd ?? candidates.FromFileName;
    }
}
