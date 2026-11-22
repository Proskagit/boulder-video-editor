using System.Collections.Immutable;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

/// <summary>Adding clips, texts and templates to the timeline (split out of TimelineEditService.cs, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineEditService
{
    // --- Add -------------------------------------------------------------------

    public string? GetAddBlockReason(MediaAsset asset)
    {
        if (asset.IsMissing)
            return string.Format(Messages.Edit_FileMissing, asset.FileName);

        if (asset.Kind == MediaKind.Image)
            return null;

        return asset.AnalysisStatus switch
        {
            MediaAnalysisStatus.Pending or MediaAnalysisStatus.Analyzing =>
                string.Format(Messages.Edit_StillAnalyzing, asset.FileName),
            MediaAnalysisStatus.Failed =>
                string.Format(Messages.Edit_DurationUnknownBecause, asset.FileName, asset.AnalysisError ?? Messages.Edit_AnalysisFailed),
            _ when asset.Metadata is not { } m || m.Duration <= MediaTime.Zero =>
                string.Format(Messages.Edit_DurationUnknown, asset.FileName),
            _ => null
        };
    }

    public TimelineEditResult AddClip(Guid mediaAssetId, Guid? trackId = null, MediaTime? start = null, bool insertOnDrop = false)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (PlanAdd(plan, mediaAssetId, trackId, start, out var clip, out var info, insertOnDrop) is { } failed) return TimelineEditResult.Fail(failed);
        var asset = FindAsset(mediaAssetId)!;
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantAddFile, asset.FileName, error));

        Commit(plan, "Add Clip");
        if (info is not null) _logger.LogInformation("{Message}", info);
        return TimelineEditResult.Ok(new[] { clip!.Id }, Join(info, TransitionNote(plan)));
    }

    /// <summary>
    /// D057 §3.2: several files at once — each at the end of its kind's first track, one after another in the given order
    /// (the caller orders them by their recording time), one plan, one Undo step; any file that cannot be added refuses the
    /// whole operation (nothing is added). The first video fixes the frame rate as a single add does (D007).
    /// </summary>
    public TimelineEditResult AddClips(IReadOnlyList<Guid> mediaAssetIds) => AddClips(mediaAssetIds, null, null, false);

    /// <summary>
    /// D057 §3.2 with a drop (the owner's acceptance of 2026-11-10): the files of <paramref name="trackId"/>'s kind go one
    /// after another from <paramref name="start"/> (the first by the drop's rule of D057 §3.12 with
    /// <paramref name="insertOnDrop"/>, D037 §3 pushing what is in the way); the files of the other kind at the end of their
    /// kind's first track. One plan, one Undo step, all or nothing. Without a track and a start: <see cref="AddClips(IReadOnlyList{Guid})"/>.
    /// </summary>
    public TimelineEditResult AddClips(IReadOnlyList<Guid> mediaAssetIds, Guid? trackId, MediaTime? start, bool insertOnDrop)
    {
        ArgumentNullException.ThrowIfNull(mediaAssetIds);
        if (mediaAssetIds.Count == 0) return TimelineEditResult.Fail(Messages.Edit_MediaNotInProject);
        if (mediaAssetIds.Count == 1) return AddClip(mediaAssetIds[0], trackId, start, insertOnDrop);
        var (plan, ids, note, error) = PlanAddMany(mediaAssetIds, trackId, start, insertOnDrop);
        if (error is not null) return TimelineEditResult.Fail(error);

        Commit(plan!, "Add Clips");
        if (note is not null) _logger.LogInformation("{Message}", note);
        return TimelineEditResult.Ok(ids, Join(note, TransitionNote(plan!)));
    }

    /// <summary>The owner's acceptance of 2026-11-10: a dry run of a drop of <paramref name="mediaAssetIds"/> for the drag's
    /// ghost — where the files of the track's kind would lie and whether the drop would move other clips; null when it would
    /// be refused. Information only: it never refuses or changes the drop.</summary>
    public DropPreview? PreviewAddClips(IReadOnlyList<Guid> mediaAssetIds, Guid trackId, MediaTime start, bool insertOnDrop)
    {
        if (mediaAssetIds.Count == 0) return null;
        var (plan, ids, _, error) = PlanAddMany(mediaAssetIds, trackId, start, insertOnDrop);
        if (error is not null || plan is null) return null;
        var added = ids.ToHashSet();
        var track = FindTrack(trackId);
        var placed = plan.EffectiveClips(track!).Where(c => added.Contains(c.Clip.Id)).Select(c => c.State).ToList();
        if (placed.Count == 0) return null;
        var moves = plan.Updates.Any(u => !added.Contains(u.Clip.Id));
        return new DropPreview(placed.Min(s => s.Start), placed.Max(s => s.End), moves);
    }

    private (EditPlan? Plan, List<Guid> Ids, string? Note, string? Error) PlanAddMany(IReadOnlyList<Guid> mediaAssetIds, Guid? trackId,
        MediaTime? start, bool insertOnDrop)
    {
        var plan = new EditPlan(Sequence, Settings);
        var ids = new List<Guid>();
        string? note = null;
        var track = trackId is { } tid ? FindTrack(tid) : null;
        if (trackId is not null && track is null) return (null, ids, null, Messages.Edit_TrackGone);
        MediaTime? next = start;
        var first = true;
        foreach (var id in mediaAssetIds)
        {
            var onTrack = track is not null && FindAsset(id) is { } asset &&
                          (asset.Kind == MediaKind.Audio ? TrackType.Audio : TrackType.Video) == track.Type;
            var failed = onTrack
                ? PlanAdd(plan, id, track!.Id, next, out var clip, out var info, insertOnDrop && first)
                : PlanAdd(plan, id, null, null, out clip, out info);
            if (failed is not null) return (null, ids, null, failed);
            ids.Add(clip!.Id);
            note ??= info;
            if (onTrack)
            {
                first = false;
                next = clip.TimelineEnd;
            }
        }
        if (Validate(plan) is { } error) return (null, ids, null, error);
        return (plan, ids, note, null);
    }

    /// <summary>Plans the add of one file on <paramref name="plan"/> (D007, D037 §3): null when planned, else why not.</summary>
    private string? PlanAdd(EditPlan plan, Guid mediaAssetId, Guid? trackId, MediaTime? start, out Clip? added, out string? info,
        bool insertOnDrop = false)
    {
        added = null;
        info = null;
        static string Failed(string message) => message;
        if (FindAsset(mediaAssetId) is not { } asset)
            return Failed(Messages.Edit_MediaNotInProject);

        if (GetAddBlockReason(asset) is { } blocked)
            return Failed(blocked);

        if (PlanFirstVideoRate(plan, asset, out info) is { } regridError)
            return Failed(string.Format(Messages.Edit_CantAddFile, asset.FileName, regridError));

        var rateForAdd = plan.Rate;
        var kind = asset.Kind == MediaKind.Audio ? TrackType.Audio : TrackType.Video;

        Track? track;
        if (trackId is { } id)
        {
            track = FindTrack(id);
            if (track is null) return Failed(Messages.Edit_TrackGone);
            if (track.Type != kind)
                return Failed(kind == TrackType.Audio ? Messages.Edit_AudioOnAudioTrack : Messages.Edit_VideoOnVideoTrack);
        }
        else
        {
            track = (kind == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks).FirstOrDefault();
            if (track is null) return Failed(kind == TrackType.Video ? Messages.Edit_NoVideoTrack : Messages.Edit_NoAudioTrack);
        }

        if (track.IsLocked)
            return Failed(string.Format(Messages.Edit_TrackLocked, track.Name));

        long frames;
        if (asset.Kind == MediaKind.Image)
        {
            frames = Math.Max(1, DefaultImageDuration.ToNearestFrame(rateForAdd));
        }
        else
        {
            frames = FrameMath.MaxWholeFrames(asset.Metadata!.Duration, rateForAdd);
            if (frames < 1)
                return Failed(string.Format(Messages.Edit_FileShorterThanFrame, asset.FileName));
        }

        long startFrame;
        if (start is { } requested)
        {
            startFrame = Math.Max(0, requested.ToNearestFrame(rateForAdd));
            // D057 §3.12: a new file dropped on a clip goes right after it (D037 §3 pushes the later clips).
            if (insertOnDrop && DropOnClipEnd(plan, track, startFrame, rateForAdd, IsMagneticMain(track)) is { } after) startFrame = after;
        }
        else
        {
            var end = plan.EffectiveClips(track).Select(c => c.State.End).DefaultIfEmpty(MediaTime.Zero).Max();
            startFrame = end.ToNearestFrame(rateForAdd);
        }

        var clip = CreateClip(asset, ClipState.FromFrames(startFrame, startFrame + frames, MediaTime.Zero, rateForAdd));
        plan.Insert(track, clip);

        // D037 §3: a drop onto clips makes room on the right (a drop that starts inside a clip is refused).
        if (MakeRoom(plan, new[] { ((Clip)clip, track) }, string.Format(Messages.Verb_AddFile, asset.FileName)) is { } roomError)
            return Failed(roomError);
        added = clip;
        return null;
    }


    /// <summary>D057 §3.12: the end frame of the clip of <paramref name="track"/> that <paramref name="frame"/> lies strictly
    /// inside, or null; on the magnetic main track (D069 §6) the cut nearest the frame — its start or its end.</summary>
    private static long? DropOnClipEnd(EditPlan plan, Track track, long frame, FrameRate rate, bool nearest = false)
    {
        foreach (var clip in plan.EffectiveClips(track))
        {
            long start = clip.State.Start.ToNearestFrame(rate), end = clip.State.End.ToNearestFrame(rate);
            if (start < frame && frame < end) return nearest ? NearestCut(start, end, frame) : end;
        }
        return null;
    }

    public MediaTime? AddedClipDuration(Guid mediaAssetId)
    {
        if (FindAsset(mediaAssetId) is not { } asset || GetAddBlockReason(asset) is not null) return null;
        var rate = asset.Kind == MediaKind.Video && !Settings.IsFrameRateLocked ? ResolveSourceFrameRate(asset).Rate : Settings.FrameRate;
        var frames = asset.Kind == MediaKind.Image
            ? Math.Max(1, DefaultImageDuration.ToNearestFrame(rate))
            : FrameMath.MaxWholeFrames(asset.Metadata!.Duration, rate);
        return frames < 1 ? null : MediaTime.FromFrame(frames, rate);
    }

    /// <summary>The first video fixes the project frame rate (D007): while the rate is still provisional, a video
    /// <paramref name="asset"/> sets its rate (or the fallback) on <paramref name="plan"/>, locked, the existing clips
    /// re-gridded in the same step; <paramref name="info"/> is the status note. Null when planned (or not needed),
    /// otherwise why the re-grid is impossible. Shared by Add and Insert / Overwrite (D031 SQ12).</summary>
    private string? PlanFirstVideoRate(EditPlan plan, MediaAsset asset, out string? info)
    {
        info = null;
        // D057 §3.2: in a plan of several adds only the first video fixes the rate.
        if (asset.Kind != MediaKind.Video || Settings.IsFrameRateLocked || plan.NewFrameRateLocked) return null;

        var (rate, fromSource) = ResolveSourceFrameRate(asset);
        plan.SetFrameRate(rate, locked: true);

        if (rate != Settings.FrameRate && FrameRateRegrid.Plan(plan, rate, FindAsset) is { } regridError)
            return regridError;

        info = fromSource
            ? string.Format(Messages.Edit_RateSetFrom, FormatRate(rate), asset.FileName)
            : string.Format(Messages.Edit_RateUnknownFallback, asset.FileName, FormatRate(rate));
        return null;
    }

    public TimelineEditResult AddTextClip(MediaTime start, Func<TextProperties, (double Width, double Height)>? measureBox = null) =>
        OverMainTrack("Add Text", () => AddTextClipOnTop(start, measureBox));

    private TimelineEditResult AddTextClipOnTop(MediaTime start, Func<TextProperties, (double Width, double Height)>? measureBox)
    {
        // Titles go over the picture: the topmost video track, never a new one.
        var track = Sequence.VideoTracks.OrderByDescending(t => t.Order).FirstOrDefault();
        if (track is null) return TimelineEditResult.Fail(Messages.Edit_NoVideoTrackPlain);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var startFrame = Math.Max(0, start.ToNearestFrame(rate));
        var frames = Math.Max(1, DefaultTextDuration.ToNearestFrame(rate));

        // Text defaults are the model's (Segoe UI, #FFFFFF, centred); the content is set, and the size from the canvas
        // height (D030 §9, Q14), so a title reads the same on any canvas. D043 §4: bold, a black 60 % background, «Выезд»
        // from below over a 0.4 s fade in, «Проявление» over a 0.3 s fade out (whole frames, D025), at the bottom centre.
        var clip = new TextClip
        {
            Text = DefaultText, FontSize = ProjectSettingsRules.NewTextFontSize(Settings.FrameHeight), Bold = true,
            Background = TextBackground.Default with { IsEnabled = true },
            MotionIn = new TextMotion(TextMotionKind.Slide, TextMotionSide.Bottom), MotionOut = TextMotion.None
        };
        ClipState.FromFrames(startFrame, startFrame + frames, MediaTime.Zero, rate).ApplyTo(clip);
        clip.FadeIn = MediaTime.FromFrame(Math.Clamp(MediaTime.FromSeconds(TextAnimationLimits.DefaultInSeconds).ToNearestFrame(rate), 1, frames), rate);
        clip.FadeOut = MediaTime.FromFrame(Math.Clamp(MediaTime.FromSeconds(TextAnimationLimits.DefaultOutSeconds).ToNearestFrame(rate), 1, frames), rate);
        if (measureBox is not null && TextProperties.Of(clip) is { } text)
        {
            var (width, height) = measureBox(text);
            (clip.PositionX, clip.PositionY) = TextPlacement.Position(new FrameSize(Settings.FrameWidth, Settings.FrameHeight), text,
                VisualProperties.Of(clip)!.Value, width, height, TextGridRow.Bottom, TextGridColumn.Centre);
        }
        plan.Insert(track, clip);

        if (MakeRoom(plan, new[] { ((Clip)clip, track) }, string.Format(Messages.Verb_AddText, track.Name)) is { } roomError)   // D037 §3
            return TimelineEditResult.Fail(roomError);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantAddText, track.Name, error));

        Commit(plan, "Add Text");
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    public TimelineEditResult AddTemplateClip(TemplateKind kind, MediaTime start)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return OverMainTrack("Add Template", () => AddTemplateClipOnTop(kind, start));
    }

    private TimelineEditResult AddTemplateClipOnTop(TemplateKind kind, MediaTime start)
    {

        // D045 §5: over the picture as a text — the topmost video track, never a new one.
        var track = Sequence.VideoTracks.OrderByDescending(t => t.Order).FirstOrDefault();
        if (track is null) return TimelineEditResult.Fail(Messages.Edit_NoVideoTrackPlain);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var startFrame = Math.Max(0, start.ToNearestFrame(rate));
        var frames = Math.Max(1, TemplateRules.DefaultDuration(kind).ToNearestFrame(rate));
        // Phase 35.5: the heart-rate overlay covers the rest of the video clip below the playhead — what it reads its BPM
        // from — or the default length when there is none.
        if (kind == TemplateKind.HeartRate && VideoClipBelow(track, startFrame, rate) is { } below)
            frames = Math.Max(1, below.TimelineEnd.ToFrameFloor(rate) - startFrame);

        // The fields of the kind; the appearance and the disappearance are the clip's fades (whole frames, D025), so a split
        // leaves them on the outer edges; the counter starts at 0.
        var fields = TemplateRules.Defaults(kind);
        var clip = new TemplateClip
        {
            Kind = kind, Date = fields.Date, Route = fields.Route, Grade = fields.Grade, Status = fields.Status,
            AccentHex = fields.AccentHex, Title = fields.Title, Subtitle = fields.Subtitle, Look = fields.Look, Corner = fields.Corner,
            Language = fields.Language
        };
        // D073: «Итоги»'s date line — the training's local date when the project has one.
        if (kind == TemplateKind.Summary && Project.Training?.Workout is { } workout)
            clip.Subtitle = workout.StartUtc.ToOffset(workout.UtcOffset ?? TimeSpan.Zero).ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
        ClipState.FromFrames(startFrame, startFrame + frames, MediaTime.Zero, rate).ApplyTo(clip);
        var (fadeIn, fadeOut) = TemplateRules.Animation(kind);
        clip.FadeIn = MediaTime.FromFrame(Math.Clamp(MediaTime.FromSeconds(fadeIn).ToNearestFrame(rate), 0, frames), rate);
        clip.FadeOut = MediaTime.FromFrame(Math.Clamp(MediaTime.FromSeconds(fadeOut).ToNearestFrame(rate), 0, frames), rate);
        plan.Insert(track, clip);

        if (MakeRoom(plan, new[] { ((Clip)clip, track) }, string.Format(Messages.Verb_AddTemplate, track.Name)) is { } roomError)   // D037 §3
            return TimelineEditResult.Fail(roomError);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantAddTemplate, track.Name, error));

        Commit(plan, "Add Template");
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    /// <summary>The video clip whose picture is right below <paramref name="track"/> at <paramref name="frame"/>: on the
    /// nearest visible track below holding a clip there (the composition's rule, <c>PlaybackSnapshot.PictureBelow</c>).</summary>
    private VideoClip? VideoClipBelow(Track track, long frame, FrameRate rate)
    {
        var time = MediaTime.FromFrame(frame, rate);
        foreach (var below in Sequence.VideoTracks.Where(t => t.Order < track.Order && !t.IsHidden).OrderByDescending(t => t.Order))
        {
            var clip = below.Clips.FirstOrDefault(c => c.TimelineStart <= time && time < c.TimelineEnd);
            if (clip is null) continue;
            return clip as VideoClip;
        }
        return null;
    }
}
