using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Moves every existing clip onto a new frame grid when the project frame rate changes — fixed by the first video (D007;
/// clips added before that sit on the provisional grid) or chosen by the user (D028 Step 13.5). Every track, locked and
/// hidden ones too.
/// Each edge goes to its nearest new-grid frame; SourceIn is kept, SourceOut follows
/// the new duration. Adjacency, order and gaps are preserved as closely as the new
/// grid allows. Fix-ups, in order, for the rare cases rounding breaks something:
/// <list type="bullet">
/// <item>a clip that would collapse to 0 frames grows by one frame to the right if the
/// next clip leaves room, else by one frame to the left if the previous clip does;</item>
/// <item>a video/audio clip that would now run past the end of its source is shortened
/// to the largest frame count the source can supply;</item>
/// <item>a clip with a speed keeps its source range and speed and takes as many frames of the new grid as they give
/// (D022);</item>
/// <item>anything else (no room to fix, a clip shorter than one frame) rejects the whole
/// operation — nothing is changed.</item>
/// </list>
/// Every result is still checked by <see cref="TimelineValidator"/> afterwards.
/// </summary>
public static class FrameRateRegrid
{
    /// <summary>Adds the re-grid updates to <paramref name="plan"/>. Returns null on
    /// success or a user-facing reason when the timeline can't be re-gridded.</summary>
    public static string? Plan(EditPlan plan, FrameRate newRate, Func<Guid, MediaAsset?> findAsset)
    {
        foreach (var track in plan.AllTracks)
        {
            var clips = track.Clips.OrderBy(c => c.TimelineStart.Ticks).ToList();
            var starts = clips.Select(c => c.TimelineStart.ToNearestFrame(newRate)).ToArray();
            var prevEnd = 0L;

            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                var startFrame = starts[i];
                var endFrame = clip.TimelineEnd.ToNearestFrame(newRate);
                var nextStart = i + 1 < clips.Count ? starts[i + 1] : long.MaxValue;

                if (startFrame < prevEnd)
                    return string.Format(Messages.Regrid_Order, track.Name);

                if (clip is MediaBackedClip { Speed.IsNormal: false } fast)
                {
                    // D022: the source range and speed stay; the clip is as many frames of the new
                    // grid as the range allows (the same rule as a speed change).
                    var frames = SpeedTiming.FramesFor(fast.SourceOut - fast.SourceIn, fast.Speed, newRate);
                    if (frames < 1)
                        return string.Format(Messages.Regrid_ClipOnTrackShort, track.Name);
                    if (startFrame + frames > nextStart)
                        return string.Format(Messages.Regrid_Overlap, track.Name);

                    prevEnd = startFrame + frames;
                    var regridded = ClipState.FromFrames(startFrame, prevEnd, fast.SourceIn, fast.SourceOut, fast.Speed, newRate);
                    if (regridded != ClipState.Capture(clip))
                        plan.Update(clip, track, regridded);
                    continue;
                }

                if (endFrame <= startFrame)
                {
                    if (startFrame + 1 <= nextStart)
                        endFrame = startFrame + 1;
                    else if (startFrame - 1 >= prevEnd)
                        (startFrame, endFrame) = (startFrame - 1, startFrame);
                    else
                        return string.Format(Messages.Regrid_NoRoom, track.Name);
                }

                var sourceIn = clip is MediaBackedClip media ? media.SourceIn : MediaTime.Zero;
                if (clip is VideoClip or AudioClip)
                {
                    var asset = findAsset(((MediaBackedClip)clip).MediaAssetId);
                    if (asset?.Metadata is not { } metadata || metadata.Duration <= MediaTime.Zero)
                        return Messages.Regrid_SourceUnknown;

                    var maxFrames = FrameMath.MaxWholeFrames(metadata.Duration - sourceIn, newRate);
                    if (maxFrames < 1)
                        return string.Format(Messages.Regrid_ClipFromShort, asset.FileName);
                    if (endFrame - startFrame > maxFrames)
                        endFrame = startFrame + maxFrames;
                }

                if (endFrame > nextStart)
                    return string.Format(Messages.Regrid_Overlap, track.Name);

                starts[i] = startFrame;
                prevEnd = endFrame;

                var after = ClipState.FromFrames(startFrame, endFrame, sourceIn, newRate);
                if (after != ClipState.Capture(clip))
                    plan.Update(clip, track, after);
            }
        }

        return null;
    }
}
