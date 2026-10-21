using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.Timeline;

/// <summary>
/// Implements <see cref="ITimelineEditService"/>. Each operation builds an
/// <see cref="EditPlan"/> in frame indices, validates every affected track with
/// <see cref="TimelineValidator"/>, and only then executes one undoable command.
/// Must be called on the UI thread (the project model is not thread-safe).
/// </summary>
public sealed partial class TimelineEditService : ITimelineEditService
{
    /// <summary>Length of a newly added image clip.</summary>
    public static readonly MediaTime DefaultImageDuration = MediaTime.FromSeconds(5);

    /// <summary>Length of a newly added text clip.</summary>
    public static readonly MediaTime DefaultTextDuration = MediaTime.FromSeconds(5);

    /// <summary>Content of a newly added text clip (empty text would not be visible).</summary>
    public static string DefaultText => Messages.Text_Default;

    /// <summary>Source frame rates outside this range are treated as unknown.</summary>
    public const int MinSourceFps = 1;
    public const int MaxSourceFps = 240;

    private readonly IProjectService _projectService;
    private readonly IUndoRedoService _undoRedo;
    private readonly ILogger<TimelineEditService> _logger;

    public TimelineEditService(IProjectService projectService, IUndoRedoService undoRedo, ILogger<TimelineEditService> logger)
    {
        _projectService = projectService;
        _undoRedo = undoRedo;
        _logger = logger;
    }

    private Core.Entities.Project Project => _projectService.Current;
    private Sequence Sequence => Project.Timeline;
    private ProjectSettings Settings => Project.Settings;

    public FrameRate FrameRate => Settings.FrameRate;

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

    public TimelineEditResult AddClip(Guid mediaAssetId, Guid? trackId = null, MediaTime? start = null)
    {
        if (FindAsset(mediaAssetId) is not { } asset)
            return TimelineEditResult.Fail(Messages.Edit_MediaNotInProject);

        if (GetAddBlockReason(asset) is { } blocked)
            return TimelineEditResult.Fail(blocked);

        var plan = new EditPlan(Sequence, Settings);
        if (PlanFirstVideoRate(plan, asset, out var info) is { } regridError)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantAddFile, asset.FileName, regridError));

        var rateForAdd = plan.Rate;
        var kind = asset.Kind == MediaKind.Audio ? TrackType.Audio : TrackType.Video;

        Track? track;
        if (trackId is { } id)
        {
            track = FindTrack(id);
            if (track is null) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
            if (track.Type != kind)
                return TimelineEditResult.Fail(kind == TrackType.Audio ? Messages.Edit_AudioOnAudioTrack : Messages.Edit_VideoOnVideoTrack);
        }
        else
        {
            track = (kind == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks).FirstOrDefault();
            if (track is null) return TimelineEditResult.Fail(kind == TrackType.Video ? Messages.Edit_NoVideoTrack : Messages.Edit_NoAudioTrack);
        }

        if (track.IsLocked)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        long frames;
        if (asset.Kind == MediaKind.Image)
        {
            frames = Math.Max(1, DefaultImageDuration.ToNearestFrame(rateForAdd));
        }
        else
        {
            frames = FrameMath.MaxWholeFrames(asset.Metadata!.Duration, rateForAdd);
            if (frames < 1)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_FileShorterThanFrame, asset.FileName));
        }

        long startFrame;
        if (start is { } requested)
        {
            startFrame = Math.Max(0, requested.ToNearestFrame(rateForAdd));
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
            return TimelineEditResult.Fail(roomError);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantAddFile, asset.FileName, error));

        Commit(plan, "Add Clip");
        if (info is not null) _logger.LogInformation("{Message}", info);
        return TimelineEditResult.Ok(new[] { clip.Id }, Join(info, TransitionNote(plan)));
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
        if (asset.Kind != MediaKind.Video || Settings.IsFrameRateLocked) return null;

        var (rate, fromSource) = ResolveSourceFrameRate(asset);
        plan.SetFrameRate(rate, locked: true);

        if (rate != Settings.FrameRate && FrameRateRegrid.Plan(plan, rate, FindAsset) is { } regridError)
            return regridError;

        info = fromSource
            ? string.Format(Messages.Edit_RateSetFrom, FormatRate(rate), asset.FileName)
            : string.Format(Messages.Edit_RateUnknownFallback, asset.FileName, FormatRate(rate));
        return null;
    }

    public TimelineEditResult AddTextClip(MediaTime start, Func<TextProperties, (double Width, double Height)>? measureBox = null)
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

        // D045 §5: over the picture as a text — the topmost video track, never a new one.
        var track = Sequence.VideoTracks.OrderByDescending(t => t.Order).FirstOrDefault();
        if (track is null) return TimelineEditResult.Fail(Messages.Edit_NoVideoTrackPlain);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var startFrame = Math.Max(0, start.ToNearestFrame(rate));
        var frames = Math.Max(1, TemplateRules.DefaultDuration(kind).ToNearestFrame(rate));

        // The fields of the kind; the appearance and the disappearance are the clip's fades (whole frames, D025), so a split
        // leaves them on the outer edges; the counter starts at 0.
        var fields = TemplateRules.Defaults(kind);
        var clip = new TemplateClip
        {
            Kind = kind, Date = fields.Date, Route = fields.Route, Grade = fields.Grade, Status = fields.Status,
            AccentHex = fields.AccentHex, Title = fields.Title, Subtitle = fields.Subtitle
        };
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

    // --- Move ------------------------------------------------------------------

    public TimelineEditResult MoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null)
    {
        var (plan, clips, error) = PlanMove(clipIds, frameDelta, targetTrackId);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return TimelineEditResult.Unchanged();

        Commit(plan, clips.Count == 1 ? "Move Clip" : "Move Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
    }

    public string? CanMoveClips(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null) =>
        PlanMove(clipIds, frameDelta, targetTrackId).Error;

    public MovePreview? PreviewMove(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId = null)
    {
        var (plan, _, error) = PlanMove(clipIds, frameDelta, targetTrackId);
        if (error is not null) return null;
        var clips = plan?.Updates.ToDictionary(u => u.Clip.Id, u => (u.After.Start, u.After.End))
            ?? new Dictionary<Guid, (MediaTime Start, MediaTime End)>();
        return new MovePreview(clips);
    }

    /// <summary>Plan + validation for a move. Plan is null (and Error null) when nothing would change.</summary>
    private (EditPlan? Plan, List<Clip> Clips, string? Error) PlanMove(IReadOnlyCollection<Guid> clipIds, long frameDelta, Guid? targetTrackId)
    {
        if (clipIds.Count == 0) return (null, new List<Clip>(), null);

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is { } resolveError) return (null, clips, resolveError);
        if (CheckEditable(plan, clips) is { } editError) return (null, clips, editError);

        Track? target = null;
        if (targetTrackId is { } tid)
        {
            target = FindTrack(tid);
            if (target is null) return (null, clips, Messages.Edit_TrackGone);
            // AQ7: only the clips of the target's kind change track; a linked clip's partner of the other kind (selected
            // with it, AQ1) moves in time on its own track — taken along by the move rule (PlanPartnerShifts).
            var others = clips.Where(c => !TimelineValidator.IsCompatible(c, target.Type)).ToList();
            if (others.Count > 0 && others.Count < clips.Count && others.All(o => clips.Contains(PartnerOf(plan, o)!)))
                clips = clips.Except(others).ToList();
            if (clips.Select(plan.TrackOf).Distinct().Count() != 1)
                return (null, clips, Messages.Edit_SeveralTracksInTime);
            if (target.IsLocked) return (null, clips, string.Format(Messages.Edit_TrackLocked, target.Name));
            if (target == plan.TrackOf(clips[0])) target = null;
        }

        if (frameDelta == 0 && target is null) return (null, clips, null);

        foreach (var clip in clips)
        {
            if (PlanShift(plan, clip, target ?? plan.TrackOf(clip), frameDelta) is { } shiftError)
                return (null, clips, shiftError);
        }

        // D037 §3: the moved clips and the partners that follow them are placed; the clips on their right make room.
        if (PlanPartnerShifts(plan) is { } partnerError)
            return (null, clips, string.Format(Messages.Edit_CantMove, partnerError));
        if (MakeRoom(plan, PlacedByUpdates(plan), Messages.Verb_Move) is { } roomError)
            return (null, clips, roomError);
        if (Validate(plan) is { } error)
            return (null, clips, string.Format(Messages.Edit_CantMove, error));

        return (plan, clips, null);
    }

    /// <summary>Plans one clip moved by <paramref name="frameDelta"/> whole frames onto <paramref name="toTrack"/> — the
    /// move's timing rule, shared by Move and the ripple edits (D027 §2): the frame count, speed and source range go
    /// along. Null when planned, otherwise why not.</summary>
    private string? PlanShift(EditPlan plan, Clip clip, Track toTrack, long frameDelta)
    {
        if (!TimelineValidator.IsCompatible(clip, toTrack.Type))
            return clip is AudioClip ? Messages.Edit_AudioOnAudioTrack : Messages.Edit_VideoOnVideoTrack;
        var (after, error) = ShiftedState(clip, frameDelta, plan.Rate);
        if (error is not null) return error;
        plan.Update(clip, toTrack, after);
        return null;
    }

    /// <summary>The timing <paramref name="clip"/> has when moved by <paramref name="frameDelta"/> whole frames (the move's
    /// rule, also used for pasted copies), or why it can't move.</summary>
    private (ClipState State, string? Error) ShiftedState(Clip clip, long frameDelta, FrameRate rate) =>
        ShiftedState(clip, ClipState.Capture(clip), frameDelta, rate);

    /// <summary>The move's rule applied to <paramref name="state"/> (the timing <paramref name="clip"/> has, or is planned
    /// to have): also used by the ripple trim (D030 §5), which moves a clip it has just trimmed.</summary>
    private (ClipState State, string? Error) ShiftedState(Clip clip, ClipState state, long frameDelta, FrameRate rate)
    {
        var startFrame = state.Start.ToNearestFrame(rate) + frameDelta;
        var endFrame = state.End.ToNearestFrame(rate) + frameDelta;
        if (startFrame < 0)
            return (default, Messages.Edit_BeforeTimelineStart);

        if (clip is MediaBackedClip && !state.Speed.IsNormal)
        {
            // D022: the source range and speed move along unchanged; so does the frame count.
            return (ClipState.FromFrames(startFrame, endFrame, state.SourceIn, state.SourceOut, state.Speed, rate), null);
        }

        var sourceIn = clip is MediaBackedClip ? state.SourceIn : MediaTime.Zero;
        var after = ClipState.FromFrames(startFrame, endFrame, sourceIn, rate);

        // The tick length of the same frame count can differ by one tick between
        // positions. If that would push SourceOut one tick past the source end
        // (possible only for the right half of a split), slip SourceIn back by
        // the excess — a sub-frame, invisible adjustment — instead of failing.
        if (SourceDuration(clip) is { } sourceDuration && after.SourceOut > sourceDuration)
        {
            var excess = after.SourceOut - sourceDuration;
            if (after.SourceIn < excess)
                return (default, Messages.Edit_ClipPastSourceEnd);
            after = after with { SourceIn = after.SourceIn - excess, SourceOut = sourceDuration };
        }

        return (after, null);
    }

    // --- Copy / paste / duplicate (D027 §5) --------------------------------------

    public TimelineClipboard? CopyClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return null;
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is not null) return null;
        var entries = clips.OrderBy(c => c.TimelineStart)
            .Select(c => new TimelineClipboardEntry(CopyWithLink(c), plan.TrackOf(c).Id))
            .ToList();
        return new TimelineClipboard(plan.Rate, CurrentCanvas, entries);
    }

    public TimelineEditResult PasteClips(TimelineClipboard clipboard, MediaTime at)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.Count == 0) return TimelineEditResult.Unchanged();
        var plan = new EditPlan(Sequence, Settings);
        return PasteInto(plan, clipboard, Math.Max(0, at.ToNearestFrame(plan.Rate)), clipboard.Count == 1 ? "Paste Clip" : "Paste Clips", Messages.Verb_Paste);
    }

    public TimelineEditResult DuplicateClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return TimelineEditResult.Unchanged();
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        if (CheckEditable(plan, clips) is { } editError) return TimelineEditResult.Fail(editError);

        var clipboard = CopyClips(clipIds)!;
        var end = clips.Max(c => c.TimelineEnd.ToNearestFrame(plan.Rate));
        return PasteInto(plan, clipboard, end, clips.Count == 1 ? "Duplicate Clip" : "Duplicate Clips", Messages.Verb_Duplicate);
    }

    /// <summary>New copies of the clipboard's clips, the earliest at <paramref name="atFrame"/>, each on its own track,
    /// planned, validated and committed as one step — or the reason nothing was added.</summary>
    private TimelineEditResult PasteInto(EditPlan plan, TimelineClipboard clipboard, long atFrame, string description, string verb)
    {
        var rate = plan.Rate;
        if (clipboard.FrameRate != rate)
            return TimelineEditResult.Fail(Messages.Edit_RateChangedSinceCopy);
        if (clipboard.Canvas != CurrentCanvas)
            return TimelineEditResult.Fail(Messages.Edit_SizeChangedSinceCopy);

        var earliest = clipboard.Entries.Min(e => e.Clip.TimelineStart.ToNearestFrame(rate));
        // D032 R5: both clips of a link copied → the copies form a new link; one alone is pasted unlinked.
        var newLinks = clipboard.Entries.Select(e => (e.Clip as MediaBackedClip)?.LinkId).OfType<Guid>()
            .GroupBy(l => l).Where(g => g.Count() == 2).ToDictionary(g => g.Key, _ => Guid.NewGuid());
        var ids = new List<Guid>();
        var placed = new List<(Clip Clip, Track Track)>();
        foreach (var entry in clipboard.Entries)
        {
            if (FindTrack(entry.TrackId) is not { } track)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_CopiedTrackGone, verb));
            if (track.IsLocked)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
            if (entry.Clip is MediaBackedClip media && FindAsset(media.MediaAssetId) is null)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_CopiedMediaGone, verb));

            var copy = CloneClip(entry.Clip);                      // a new id; the same asset, never the file
            if (copy is MediaBackedClip linked && entry.Clip is MediaBackedClip { LinkId: { } link } && newLinks.TryGetValue(link, out var newLink))
                linked.LinkId = newLink;
            var (state, error) = ShiftedState(copy, atFrame - earliest, rate);
            if (error is not null) return TimelineEditResult.Fail($"Can't {verb}: {error}");
            state.ApplyTo(copy);
            plan.Insert(track, copy);
            ids.Add(copy.Id);
            placed.Add((copy, track));
        }

        // D037 §3 (Q10): the clips on the right make room; a copy starting inside a clip is refused.
        if (MakeRoom(plan, placed, verb) is { } roomError) return TimelineEditResult.Fail(roomError);

        // Any other broken rule rejects the whole paste (D027 §5); no dissolve is created.
        if (Validate(plan) is { } invalid) return TimelineEditResult.Fail($"Can't {verb}: {invalid}");

        Commit(plan, description);
        return TimelineEditResult.Ok(ids);
    }

    // --- Trim ------------------------------------------------------------------

    public TimelineEditResult TrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime, bool alone = false)
    {
        var (plan, _, error) = PlanTrim(clipId, edge, edgeTime, alone);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return TimelineEditResult.Unchanged();

        Commit(plan, "Trim Clip");
        return TimelineEditResult.Ok(new[] { clipId }, TransitionNote(plan));
    }

    public (MediaTime Start, MediaTime End)? PreviewTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime, bool alone = false)
    {
        var (_, after, error) = PlanTrim(clipId, edge, edgeTime, alone);
        return error is null && after is { } state ? (state.Start, state.End) : null;
    }

    /// <summary>Clamped trim result. Plan is null when nothing would change; After is
    /// the resulting timing either way (null on error).</summary>
    private (EditPlan? Plan, ClipState? After, string? Error) PlanTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime, bool alone)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return (null, null, resolveError);
        if (CheckEditable(plan, clips) is { } editError) return (null, null, editError);

        var clip = clips[0];
        var track = plan.TrackOf(clip);
        var rate = plan.Rate;

        // D025 §5: a trim of the far edge keeps the part of the dissolve on the other edge inside the clip (the trimmed
        // edge's own dissolve, if any, goes when its cut opens).
        var (partAtStart, partAtEnd) = DissolveParts(track, clip, rate);
        var minFrames = Math.Max(1, edge == ClipEdge.End ? partAtStart : partAtEnd);

        var before = ClipState.Capture(clip);
        var after = TrimmedState(clip, track, edge, edgeTime.ToNearestFrame(rate), minFrames, rate);

        // AQ3: the partner's same edge moves by the same frames, by the same rule; where one of them is limited, both take
        // the smaller change. Alt + edge (alone) trims the clip by itself: a J / L cut (R6).
        var (partner, partnerError) = FollowingPartner(plan, clip, alone);
        if (partnerError is not null) return (null, null, partnerError);
        ClipState? partnerAfter = null;
        if (partner is not null)
        {
            var partnerTrack = plan.TrackOf(partner);
            var partnerBefore = ClipState.Capture(partner);
            var (partnerAtStart, partnerAtEnd) = DissolveParts(partnerTrack, partner, rate);
            var partnerMin = Math.Max(1, edge == ClipEdge.End ? partnerAtStart : partnerAtEnd);
            for (var round = 0; round < 3; round++)
            {
                var delta = EdgeFrame(after, edge, rate) - EdgeFrame(before, edge, rate);
                partnerAfter = TrimmedState(partner, partnerTrack, edge, EdgeFrame(partnerBefore, edge, rate) + delta, partnerMin, rate);
                var partnerDelta = EdgeFrame(partnerAfter.Value, edge, rate) - EdgeFrame(partnerBefore, edge, rate);
                if (partnerDelta == delta) break;
                after = TrimmedState(clip, track, edge, EdgeFrame(before, edge, rate) + partnerDelta, minFrames, rate);
            }
            if (EdgeFrame(partnerAfter!.Value, edge, rate) - EdgeFrame(partnerBefore, edge, rate) != EdgeFrame(after, edge, rate) - EdgeFrame(before, edge, rate))
                return (null, null, string.Format(Messages.Edit_CantTrim, PartnerCantFollow));
            if (partnerAfter != partnerBefore) plan.Update(partner, partnerTrack, partnerAfter.Value);
        }

        if (after == before) return (null, after, null);

        plan.Update(clip, track, after);
        if (Validate(plan) is { } error)
            return (null, null, string.Format(Messages.Edit_CantTrim, error));

        return (plan, after, null);
    }

    /// <summary>The timing of <paramref name="clip"/> with <paramref name="edge"/> trimmed to
    /// <paramref name="targetFrame"/>, clamped to the neighbours on <paramref name="track"/>, the source (start ≥ 0, its
    /// end) and <paramref name="minFrames"/> — the trim's one rule (D008 / D022 / D025), shared by the edge drag and the
    /// trim to the playhead (D030 §5).</summary>
    private ClipState TrimmedState(Clip clip, Track track, ClipEdge edge, long targetFrame, long minFrames, FrameRate rate) =>
        TrimmedState(clip, ClipState.Capture(clip), track.Clips.Where(c => c != clip).ToList(), edge, targetFrame, minFrames, rate);

    /// <summary>The same rule for <paramref name="clip"/> at the timing <paramref name="state"/> (planned, or its own) and
    /// the neighbours <paramref name="others"/> — none for a ripple, whose neighbours move instead of limiting it.</summary>
    private ClipState TrimmedState(Clip clip, ClipState state, IReadOnlyList<Clip> others, ClipEdge edge, long targetFrame,
        long minFrames, FrameRate rate)
    {
        var startFrame = state.Start.ToNearestFrame(rate);
        var endFrame = state.End.ToNearestFrame(rate);
        var sourceIn = clip is MediaBackedClip ? state.SourceIn : MediaTime.Zero;
        var sourceDuration = SourceDuration(clip);

        ClipState after;
        if (clip is MediaBackedClip && !state.Speed.IsNormal)
        {
            after = PlanTrimAtSpeed(state, edge, startFrame, endFrame, targetFrame, others, sourceDuration, rate, minFrames);
        }
        else if (edge == ClipEdge.End)
        {
            var hi = others.Where(c => c.TimelineStart >= state.End)
                .Select(c => c.TimelineStart.ToNearestFrame(rate))
                .DefaultIfEmpty(long.MaxValue).Min();
            if (sourceDuration is { } duration)
                hi = Math.Min(hi, startFrame + FrameMath.MaxWholeFrames(duration - sourceIn, rate));

            var newEnd = Math.Clamp(targetFrame, startFrame + minFrames, Math.Max(startFrame + minFrames, hi));
            after = ClipState.FromFrames(startFrame, newEnd, sourceIn, rate);
        }
        else
        {
            var lo = others.Where(c => c.TimelineEnd <= state.Start)
                .Select(c => c.TimelineEnd.ToNearestFrame(rate))
                .DefaultIfEmpty(0).Max();
            if (sourceDuration is not null)
                lo = Math.Max(lo, FrameMath.CeilingFrame(state.Start - sourceIn, rate)); // keeps SourceIn ≥ 0

            var newStart = Math.Clamp(targetFrame, Math.Min(lo, endFrame - minFrames), endFrame - minFrames);
            var newStartTime = MediaTime.FromFrame(newStart, rate);

            // Video/audio: the in-point follows the edge so the content under the
            // playhead doesn't shift. Images have no source timing: SourceIn stays 0.
            var newSourceIn = sourceDuration is not null ? sourceIn + (newStartTime - state.Start) : sourceIn;
            after = ClipState.FromFrames(newStart, endFrame, newSourceIn, rate);
        }
        return after;
    }

    /// <summary>
    /// Trim of a clip at a speed other than 1× (D022), in the same one rounding rule
    /// (<see cref="SpeedTiming.SourceLength"/>): the end edge sets SourceOut = SourceIn +
    /// SourceLength(N); the start edge moves SourceIn by SourceLength(Δ) frames' worth (so the content
    /// under the playhead stays) and keeps SourceOut, normalized only if the invariant is missed by
    /// the rounding. Limits: neighbours, <paramref name="minFrames"/> (one frame, or a dissolve's part), SourceIn ≥ 0
    /// and the end of the source.
    /// </summary>
    private static ClipState PlanTrimAtSpeed(ClipState clip, ClipEdge edge, long startFrame, long endFrame, long targetFrame,
        IReadOnlyList<Clip> others, MediaTime? sourceDuration, FrameRate rate, long minFrames)
    {
        var speed = clip.Speed;
        if (edge == ClipEdge.End)
        {
            var hi = others.Where(c => c.TimelineStart >= clip.End)
                .Select(c => c.TimelineStart.ToNearestFrame(rate))
                .DefaultIfEmpty(long.MaxValue).Min();
            if (sourceDuration is { } duration)
                hi = Math.Min(hi, startFrame + SpeedTiming.FramesFor(duration - clip.SourceIn, speed, rate));

            var newEnd = Math.Clamp(targetFrame, startFrame + minFrames, Math.Max(startFrame + minFrames, hi));
            var sourceOut = clip.SourceIn + SpeedTiming.SourceLength(newEnd - startFrame, speed, rate);
            return ClipState.FromFrames(startFrame, newEnd, clip.SourceIn, sourceOut, speed, rate);
        }

        var lo = others.Where(c => c.TimelineEnd <= clip.Start)
            .Select(c => c.TimelineEnd.ToNearestFrame(rate))
            .DefaultIfEmpty(0).Max();
        lo = Math.Max(lo, startFrame - SpeedTiming.FramesFor(clip.SourceIn, speed, rate)); // keeps SourceIn ≥ 0

        var newStart = Math.Clamp(targetFrame, Math.Min(lo, endFrame - minFrames), endFrame - minFrames);
        var delta = newStart - startFrame;
        var sourceIn = delta >= 0
            ? clip.SourceIn + SpeedTiming.SourceLength(delta, speed, rate)
            : clip.SourceIn - SpeedTiming.SourceLength(-delta, speed, rate);
        var (normalizedIn, normalizedOut) = ClipState.Normalize(endFrame - newStart, sourceIn, clip.SourceOut, speed, rate);
        return ClipState.FromFrames(newStart, endFrame, normalizedIn, normalizedOut, speed, rate);
    }

    // --- Speed (D022) ---------------------------------------------------------------

    public TimelineEditResult SetClipSpeed(Guid clipId, ClipSpeed speed)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        if (clips[0] is not (VideoClip or AudioClip)) return TimelineEditResult.Fail(Messages.Edit_SpeedOnlyMedia);
        if (CheckEditable(plan, clips) is { } editError) return TimelineEditResult.Fail(editError);

        var clip = (MediaBackedClip)clips[0];
        if (clip.Speed == speed) return TimelineEditResult.Unchanged();

        // Back to the speed the clip's current speed step started from, when that step removed dissolves (D025 §5): this
        // is undoing the step — the clip gets its timing back and the dissolves return. (A value typed back, the arrows
        // back, or Ctrl+Z inside the Speed field, which is the field's own text undo, all end here.)
        if (_undoRedo.NextUndo is NotifyingCommand { Inner: SetClipSpeedCommand step } && step.Clip == clip &&
            step.Changes.Count > 0 && step.Before.Speed == speed && step.After == ClipState.Capture(clip))
        {
            _undoRedo.Undo();
            return TimelineEditResult.Ok(new[] { clip.Id },
                step.Changes.OfType<RemoveTransitionCommand>().FirstOrDefault() is { } back
                    ? Tr(back.Track, back.Transition.TransitionTypeId, Messages.Edit_DissolveBack, Messages.Edit_DissolveBack, Messages.Edit_TransitionBackNamed)
                    : null);
        }

        // The speed never changes the source range: the frame count follows from it.
        var rate = plan.Rate;
        var frames = SpeedTiming.FramesFor(clip.SourceOut - clip.SourceIn, speed, rate);
        if (frames < 1) return TimelineEditResult.Fail(string.Format(Messages.Edit_SpeedTooShort, speed));

        var startFrame = clip.TimelineStart.ToNearestFrame(rate);
        var before = ClipState.Capture(clip);
        var after = ClipState.FromFrames(startFrame, startFrame + frames, clip.SourceIn, clip.SourceOut, speed, rate);
        plan.Update(clip, plan.TrackOf(clip), after);

        // AQ6: the partner takes the same speed by the same rule (its own source range gives its frame count).
        var (partner, partnerError) = FollowingPartner(plan, clip);
        if (partnerError is not null) return TimelineEditResult.Fail(partnerError);
        ClipChange? partnerChange = null;
        if (partner is not null && partner.Speed != speed)
        {
            var partnerFrames = SpeedTiming.FramesFor(partner.SourceOut - partner.SourceIn, speed, rate);
            if (partnerFrames < 1) return TimelineEditResult.Fail(string.Format(Messages.Edit_SpeedTooShortLinked, speed));
            var partnerStart = partner.TimelineStart.ToNearestFrame(rate);
            var partnerAfter = ClipState.FromFrames(partnerStart, partnerStart + partnerFrames, partner.SourceIn, partner.SourceOut, speed, rate);
            var partnerTrack = plan.TrackOf(partner);
            plan.Update(partner, partnerTrack, partnerAfter);
            partnerChange = new ClipChange(partner, partnerTrack, ClipState.Capture(partner), partnerTrack, partnerAfter);
        }
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantChangeSpeed, error));

        // A speed change of A moves its end: its dissolve goes (D025 §5), and fades longer than the shorter clip are cut
        // (§2), in the same step — which still merges with the next speed changes of the clip, so one Undo restores the
        // speed, the fades and the dissolve together.
        var changes = plan.BuildPropertyCommands();
        changes.AddRange(plan.BuildTransitionCommands());
        if (partnerChange is not null) changes.Insert(0, new UpdateClipsCommand("Change Speed", new[] { partnerChange }));
        var command = new SetClipSpeedCommand(clip, before, after, changes.Count > 0 ? changes : null);
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id }, TransitionNote(plan));
    }

    // --- Split -----------------------------------------------------------------

    public TimelineEditResult Split(MediaTime at, IReadOnlyCollection<Guid>? clipIds = null)
    {
        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var atFrame = at.ToNearestFrame(rate);

        List<Clip> candidates;
        var explicitSelection = clipIds is { Count: > 0 };
        if (explicitSelection)
        {
            if (Resolve(plan, clipIds!, out var selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);
            candidates = selected;
        }
        else
        {
            candidates = plan.AllTracks.Where(t => !t.IsLocked).SelectMany(t => t.Clips).ToList();
        }

        // AQ5: a linked clip's partner covering the point is split too — refused with the whole split when it is locked.
        var toSplit = WithPartnersCovering(plan, candidates
            .Where(c => c.TimelineStart.ToNearestFrame(rate) < atFrame && atFrame < c.TimelineEnd.ToNearestFrame(rate)), atFrame);

        if (toSplit.Count == 0)
            return TimelineEditResult.Fail(explicitSelection
                ? Messages.Edit_PlayheadNotInside
                : Messages.Edit_NoClipToSplit);

        if (CheckEditable(plan, toSplit) is { } editError) return TimelineEditResult.Fail(editError);

        var ids = new List<Guid>();
        var splits = new List<(Clip Left, Clip Right)>();
        foreach (var clip in toSplit)
        {
            if (!PlanSplitClip(plan, plan.TrackOf(clip), clip, ClipState.Capture(clip), atFrame, out var rightClip))
                return TimelineEditResult.Fail(Tr(plan.TrackOf(clip),
                    ZoneAt(plan.TrackOf(clip), clip, ClipState.Capture(clip), atFrame, plan.Rate)?.TransitionTypeId,
                    Messages.Split_InsideDissolve, Messages.Split_InsideCrossfade, Messages.Split_InsideNamed));
            ids.Add(clip.Id);
            ids.Add(rightClip.Id);
            splits.Add((clip, rightClip));
        }
        LinkSplitParts(splits);

        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_CantSplit, error));

        Commit(plan, toSplit.Count == 1 ? "Split Clip" : "Split Clips");
        return TimelineEditResult.Ok(ids, TransitionNote(plan));
    }

    /// <summary>
    /// The split rule for one clip of <paramref name="track"/> at the timing <paramref name="state"/> (its own, or the one
    /// a plan gives it), cut at <paramref name="atFrame"/> strictly inside it: the left part keeps the clip (its id and
    /// its dissolve at the start), <paramref name="rightClip"/> is a new clip planned as an insert; the source cut follows
    /// D022, the fades stay on the outer edges (D025 §2), a dissolve at the clip's end moves to the right part. Shared by
    /// Split and Insert / Overwrite (D031 SQ6 / SQ9). False — nothing planned — when the cut is inside a dissolve's zone
    /// (D025 §5).
    /// </summary>
    /// <summary>The transition of <paramref name="clip"/> whose zone a split at <paramref name="atFrame"/> would fall
    /// strictly inside (D025 §5), or null.</summary>
    private static Transition? ZoneAt(Track track, Clip clip, ClipState state, long atFrame, FrameRate rate)
    {
        var startFrame = state.Start.ToNearestFrame(rate);
        var endFrame = state.End.ToNearestFrame(rate);
        foreach (var transition in track.Transitions)
        {
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(transition.Duration, rate));
            if ((transition.LeftClipId == clip.Id && atFrame > endFrame - beforeCut) ||
                (transition.RightClipId == clip.Id && atFrame < startFrame + afterCut))
                return transition;
        }
        return null;
    }

    private bool PlanSplitClip(EditPlan plan, Track track, Clip clip, ClipState state, long atFrame, out Clip rightClip)
    {
        var rate = plan.Rate;
        var startFrame = state.Start.ToNearestFrame(rate);
        var endFrame = state.End.ToNearestFrame(rate);
        var sourceIn = clip is MediaBackedClip ? state.SourceIn : MediaTime.Zero;
        var hasSource = SourceDuration(clip) is not null;
        rightClip = null!;

        // D025 §5: never inside a dissolve's zone.
        if (ZoneAt(track, clip, state, atFrame, rate) is not null) return false;

        ClipState left, right;
        if (clip is MediaBackedClip && !state.Speed.IsNormal)
        {
            // D022: the cut in the source is SourceIn + SourceLength(frames left of the split);
            // the right half keeps the original SourceOut (normalized only if rounding misses).
            var cut = state.SourceIn + SpeedTiming.SourceLength(atFrame - startFrame, state.Speed, rate);
            left = ClipState.FromFrames(startFrame, atFrame, state.SourceIn, cut, state.Speed, rate);
            var (rightIn, rightOut) = ClipState.Normalize(endFrame - atFrame, cut, state.SourceOut, state.Speed, rate);
            right = ClipState.FromFrames(atFrame, endFrame, rightIn, rightOut, state.Speed, rate);
        }
        else
        {
            left = ClipState.FromFrames(startFrame, atFrame, sourceIn, rate);
            right = ClipState.FromFrames(atFrame, endFrame, hasSource ? left.SourceOut : sourceIn, rate);
        }

        rightClip = CloneClip(clip);
        right.ApplyTo(rightClip);

        // D045 §3: a template's counter goes on from the cut in the right part.
        if (rightClip is TemplateClip template)
            template.CounterStart += MediaTime.FromFrame(atFrame - startFrame, rate);

        // D025 §2: the fades stay with the outer edges — the right part keeps the fade out, the left part the
        // fade in; the new inner edges have none.
        rightClip.FadeIn = MediaTime.Zero;
        if (clip.FadeOut != MediaTime.Zero)
            plan.SetProperties(clip, new ClipPropertyValues(null, null, null, new FadeProperties(clip.FadeIn, MediaTime.Zero)));

        // A dissolve at the clip's end moves to the right part (the left part keeps the clip's id and its dissolve at
        // the start).
        foreach (var transition in track.Transitions.Where(t => t.LeftClipId == clip.Id))
            plan.UpdateTransition(transition, plan.StateOf(transition) with { LeftClipId = rightClip.Id });

        plan.Update(clip, track, left);
        plan.Insert(track, rightClip);
        return true;
    }

    // --- Trim to the playhead (D030 §5) -----------------------------------------------

    public TimelineEditResult TrimToPlayhead(IReadOnlyCollection<Guid> clipIds, ClipEdge edge, MediaTime playhead, bool ripple)
    {
        if (clipIds.Count == 0)
            return TimelineEditResult.Fail(Messages.Edit_SelectClipToTrim);

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var rate = plan.Rate;
        var at = playhead.ToNearestFrame(rate);

        // Q1: only selected clips with the playhead's frame strictly inside; the others are left as they are (as Split).
        var eligible = WithPartnersCovering(plan, selected
            .Where(c => c.TimelineStart.ToNearestFrame(rate) < at && at < c.TimelineEnd.ToNearestFrame(rate)), at)
            .OrderBy(c => c is VideoClip ? 0 : 1).ToList();   // AQ3: a linked pair's video clip leads a ripple
        if (eligible.Count == 0) return TimelineEditResult.Fail(Messages.Edit_PlayheadNotInside);
        if (CheckEditable(plan, eligible) is { } editError) return TimelineEditResult.Fail(editError);

        var trimmed = new List<Clip>();
        var keptAtCut = 0;      // plain trims not made: the trimmed edge is a dissolve's cut (Q4)
        var stopped = 0;        // trims stopped where a dissolve needs the clip's frames (Q4 / Q6)
        string? keptType = null, stoppedType = null;   // the types the notes name (D044)
        long? playheadAfter = null;
        foreach (var clip in eligible)
        {
            var track = plan.TrackOf(clip);
            var start = clip.TimelineStart.ToNearestFrame(rate);
            var end = clip.TimelineEnd.ToNearestFrame(rate);
            var wanted = edge == ClipEdge.Start ? at - start : end - at;

            // The frames each dissolve keeps inside the clip (D025 §5 zone fit). A plain trim opens the trimmed edge, so a
            // dissolve there could not stay: that edge is not trimmed (Q4). A ripple keeps the trimmed edge's cut — its
            // dissolve stays and keeps its part; the far edge's dissolve always does (Q6).
            if (ripple)
            {
                if (plan.Changes(clip)) continue;    // the partner a linked clip's ripple trim already took along (AQ3)

                // The ripple trim's one planner, shared with the Shift + edge drag (D030 §5 / §6).
                var (changed, wasStopped, rippleError) = PlanRippleTrim(plan, clip, track, edge, at);
                if (rippleError is not null) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantTrim, rippleError));
                if (wasStopped && stopped++ == 0) stoppedType = EdgeTypeOf(track, clip);
                if (!changed) continue;
                if (edge == ClipEdge.Start) playheadAfter = Math.Min(playheadAfter ?? long.MaxValue, start);   // Q5
                trimmed.Add(clip);
                continue;
            }

            var (partAtStart, partAtEnd) = DissolveParts(track, clip, rate);
            long allowed;
            if ((edge == ClipEdge.Start ? partAtStart : partAtEnd) > 0)
            {
                allowed = 0;
                if (keptAtCut++ == 0) keptType = EdgeTypeOf(track, clip, edge);
            }
            else
            {
                var minFrames = Math.Max(1, edge == ClipEdge.Start ? partAtEnd : partAtStart);
                allowed = Math.Min(wanted, Math.Max(0, end - start - minFrames));
                if (allowed < wanted && stopped++ == 0) stoppedType = EdgeTypeOf(track, clip);
            }
            if (allowed == 0) continue;

            // The edge trim's own rule (source mapping, speed — D022) to the allowed frame; inward, so no neighbour limits it.
            plan.Update(clip, track, TrimmedState(clip, track, edge, edge == ClipEdge.Start ? start + allowed : end - allowed, 1, rate));
            trimmed.Add(clip);
        }

        var notes = Join(
            keptAtCut == 0 ? null : IsNamed(keptType)
                ? string.Format(edge == ClipEdge.Start ? Messages.Edit_NotTrimmedStartNamed : Messages.Edit_NotTrimmedEndNamed, TypeName(keptType!))
                : edge == ClipEdge.Start
                ? Messages.Edit_NotTrimmedStart
                : Messages.Edit_NotTrimmedEnd,
            stopped == 0 ? null : StopNote(stoppedType));
        if (trimmed.Count == 0) return TimelineEditResult.Fail(notes!);

        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantTrim, error));

        Commit(plan, (ripple, edge) switch
        {
            (false, ClipEdge.Start) => "Trim Start to Playhead",
            (false, _) => "Trim End to Playhead",
            (true, ClipEdge.Start) => "Ripple Trim Start to Playhead",
            _ => "Ripple Trim End to Playhead"
        });
        return new TimelineEditResult
        {
            Success = true,
            ClipIds = trimmed.Select(c => c.Id).ToList(),
            Message = Join(notes, TransitionNote(plan)),
            Playhead = playheadAfter is { } frame ? MediaTime.FromFrame(frame, rate) : null
        };
    }

    /// <summary>Ripple trim of <paramref name="clip"/>'s <paramref name="edge"/> to <paramref name="targetFrame"/> (D030 §5 /
    /// §6), planned into <paramref name="plan"/> — the one rule of Shift+Q / Shift+W and the Shift + edge drag. The clip
    /// keeps its start; its length changes by whole frames; every clip of its track that starts at or after its old end
    /// moves by the same frames (other tracks, markers, the playhead stay). Inward the clip keeps the frames its dissolves
    /// need (Q4 / Q6: <c>Stopped</c>); outward the source limits it (the edge trim's clamp) and a dissolve on the dragged
    /// edge keeps its source handle (D025 §4, <c>Stopped</c>). <c>Changed</c> false: nothing to do (the same frame, or no
    /// room). The caller checks the lock and validates the plan.</summary>
    private (bool Changed, bool Stopped, string? Error) PlanRippleTrim(EditPlan plan, Clip clip, Track track, ClipEdge edge, long targetFrame)
    {
        var rate = plan.Rate;
        var start = clip.TimelineStart.ToNearestFrame(rate);
        var end = clip.TimelineEnd.ToNearestFrame(rate);
        var change = edge == ClipEdge.End ? targetFrame - end : start - targetFrame;    // frames added (> 0) or removed (< 0)
        if (change == 0) return (false, false, null);

        // AQ3 / AQ4: the partner's same edge changes by the same frames (its start stays, as the clip's does) — limited by
        // the room after it, since its neighbours don't move; both take the smaller change.
        var (partner, partnerError) = FollowingPartner(plan, clip);
        if (partnerError is not null) return (false, false, partnerError);
        if (partner is null) return PlanRippleTrimAlone(plan, clip, track, change, edge);

        var partnerTrack = plan.TrackOf(partner);
        var (_, partnerFrames, partnerStopped, partnerError2) =
            RippleTrimmedState(plan, partner, partnerTrack, edge, change > 0 ? Math.Min(change, RoomAfter(partner, partnerTrack, plan.Rate)) : change);
        if (partnerError2 is not null) return (false, partnerStopped, partnerError2);
        if (partnerFrames == 0) return (false, true, null);
        var result = PlanRippleTrimAlone(plan, clip, track, Math.Abs(partnerFrames) < Math.Abs(change) ? partnerFrames : change, edge);
        if (result.Error is not null || !result.Changed) return result;

        var frames = plan.Updates.Single(u => u.Clip == clip).After.End.ToNearestFrame(plan.Rate) - clip.TimelineEnd.ToNearestFrame(plan.Rate);
        var (partnerAfter, followed, _, followError) = RippleTrimmedState(plan, partner, partnerTrack, edge, frames);
        if (followError is not null) return (false, result.Stopped, followError);
        if (followed != frames) return (false, result.Stopped, PartnerCantFollow);
        plan.Update(partner, partnerTrack, partnerAfter!.Value);
        return (true, result.Stopped || partnerStopped, null);
    }

    /// <summary><see cref="PlanRippleTrim"/> for one clip: <paramref name="change"/> frames added (&gt; 0) or removed at
    /// <paramref name="edge"/>, the later clips of its track moved by the frames the clip's end moves.</summary>
    private (bool Changed, bool Stopped, string? Error) PlanRippleTrimAlone(EditPlan plan, Clip clip, Track track, long change, ClipEdge edge)
    {
        var rate = plan.Rate;
        var end = clip.TimelineEnd.ToNearestFrame(rate);
        var (after, frames, stopped, error) = RippleTrimmedState(plan, clip, track, edge, change);
        if (error is not null) return (false, stopped, error);
        if (after is null) return (false, stopped, null);

        plan.Update(clip, track, after.Value);
        foreach (var later in track.Clips.Where(c => c != clip && c.TimelineStart.ToNearestFrame(rate) >= end))
        {
            if (PlanShift(plan, later, track, frames) is { } shiftError)
                return (false, stopped, shiftError);
        }
        return (true, stopped, null);
    }

    /// <summary>The ripple trim's timing of <paramref name="clip"/> with <paramref name="change"/> frames added (&gt; 0) or
    /// removed at <paramref name="edge"/>: its start stays, its end moves by <c>Frames</c> (after the dissolve and source
    /// limits; <c>After</c> null when that is 0).</summary>
    private (ClipState? After, long Frames, bool Stopped, string? Error) RippleTrimmedState(EditPlan plan, Clip clip, Track track,
        ClipEdge edge, long change)
    {
        var rate = plan.Rate;
        var start = clip.TimelineStart.ToNearestFrame(rate);
        var end = clip.TimelineEnd.ToNearestFrame(rate);
        if (change == 0) return (null, 0, false, null);

        long frames;
        bool stopped;
        if (change < 0)
        {
            var (partAtStart, partAtEnd) = DissolveParts(track, clip, rate);
            var minFrames = Math.Max(1, partAtStart + partAtEnd);
            frames = -Math.Min(-change, Math.Max(0, end - start - minFrames));
            stopped = frames != change && minFrames > 1;    // the dissolve stopped it, not the one-frame minimum
        }
        else
        {
            // A dissolve on the dragged edge needs the source beyond it: A after its end, B before its start (D025 §4) — only
            // the types that need handles (D044 §3: Dissolve, Push; D032: the crossfade).
            var needed = 0L;
            foreach (var t in track.Transitions.Where(t => TransitionRules.RequiresHandles(t.TransitionTypeId)))
            {
                var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate));
                if (edge == ClipEdge.End && t.LeftClipId == clip.Id) needed = afterCut;
                if (edge == ClipEdge.Start && t.RightClipId == clip.Id) needed = beforeCut;
            }
            var room = long.MaxValue;
            if (needed > 0)
            {
                var state = ClipState.Capture(clip);
                var handle = edge == ClipEdge.End
                    ? DissolveHandles.After(clip, state, AssetOf(clip), rate)
                    : DissolveHandles.Before(clip, state, AssetOf(clip), rate);
                room = Math.Max(0, handle - needed);
            }
            frames = Math.Min(change, room);
            stopped = frames != change;
        }
        if (frames == 0) return (null, 0, stopped, null);

        // The edge trim's own rule (source mapping, speed — D022) with no neighbour: they move. For the start, in the
        // clip's own frame of reference — moved right by the frames to add, trimmed back to its start, then moved so that
        // it starts where it did (the move's rule) — so the trimmed timeline never runs before 0.
        ClipState after;
        if (edge == ClipEdge.End)
        {
            after = TrimmedState(clip, ClipState.Capture(clip), Array.Empty<Clip>(), edge, end + frames, 1, rate);
        }
        else
        {
            var added = Math.Max(0, frames);
            var moved = ClipState.Capture(clip);
            if (added > 0)
            {
                var (shifted, moveError) = ShiftedState(clip, moved, added, rate);
                if (moveError is not null) return (null, 0, stopped, moveError);
                moved = shifted;
            }
            after = TrimmedState(clip, moved, Array.Empty<Clip>(), edge, start + Math.Max(0, -frames), 1, rate);
            var back = start - after.Start.ToNearestFrame(rate);
            if (back != 0)
            {
                var (shifted, backError) = ShiftedState(clip, after, back, rate);
                if (backError is not null) return (null, 0, stopped, backError);
                after = shifted;
            }
        }
        frames = after.End.ToNearestFrame(rate) - end;                                   // after the source clamp
        return frames == 0 ? (null, 0, stopped, null) : (after, frames, stopped, null);
    }

    public TimelineEditResult RippleTrimClip(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var note = StopNoteOf(clipId, edge, edgeTime);       // before the trim: which way it goes
        var (plan, stopped, error) = PlanRippleTrimOf(clipId, edge, edgeTime);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return stopped ? TimelineEditResult.Fail(note) : TimelineEditResult.Unchanged();

        Commit(plan, edge == ClipEdge.Start ? "Ripple Trim Start" : "Ripple Trim End");
        return TimelineEditResult.Ok(new[] { clipId }, Join(stopped ? note : null, TransitionNote(plan)));
    }

    /// <summary><see cref="StopNote"/> for a ripple trim of <paramref name="edge"/> of the clip <paramref name="clipId"/> to
    /// <paramref name="edgeTime"/>: outward it is stopped by the handle of the transition at that edge (a type that needs
    /// one), inward by the frames the transitions at either edge keep in the clip.</summary>
    private string StopNoteOf(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
        {
            if (track.Clips.FirstOrDefault(c => c.Id == clipId) is not { } clip) continue;
            var outward = edge == ClipEdge.End ? edgeTime > clip.TimelineEnd : edgeTime < clip.TimelineStart;
            return StopNote(outward ? EdgeTypeOf(track, clip, edge, handlesOnly: true) : EdgeTypeOf(track, clip));
        }
        return StopNote(null);
    }

    public RippleTrimPreview? PreviewRippleTrim(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var (plan, stopped, error) = PlanRippleTrimOf(clipId, edge, edgeTime);
        if (error is not null) return null;
        if (plan is null) return new RippleTrimPreview(new Dictionary<Guid, (MediaTime, MediaTime)>(), stopped);
        var clips = plan.AffectedTracks.SelectMany(plan.EffectiveClips)
            .Where(c => c.State != ClipState.Capture(c.Clip))
            .ToDictionary(c => c.Clip.Id, c => (c.State.Start, c.State.End));
        return new RippleTrimPreview(clips, stopped);
    }

    /// <summary>The ripple drag's plan (validated), or null when nothing would change; refused on a locked track.</summary>
    private (EditPlan? Plan, bool Stopped, string? Error) PlanRippleTrimOf(Guid clipId, ClipEdge edge, MediaTime edgeTime)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return (null, false, resolveError);
        if (CheckEditable(plan, clips) is { } editError) return (null, false, editError);

        var clip = clips[0];
        var (changed, stopped, error) = PlanRippleTrim(plan, clip, plan.TrackOf(clip), edge, edgeTime.ToNearestFrame(plan.Rate));
        if (error is not null) return (null, stopped, string.Format(Messages.Edit_CantTrim, error));
        if (!changed) return (null, stopped, null);
        if (Validate(plan) is { } invalid) return (null, stopped, string.Format(Messages.Edit_CantTrim, invalid));
        return (plan, stopped, null);
    }

    /// <summary>The note of a trim stopped where a transition of <paramref name="typeId"/> needs the clip's frames.</summary>
    private static string StopNote(string? typeId) =>
        IsNamed(typeId) ? string.Format(Messages.Edit_TrimStoppedNamed, TypeName(typeId!)) : Messages.Edit_TrimStoppedDissolve;

    // --- Slip (D030 §7) ------------------------------------------------------------------

    public TimelineEditResult SlipClip(Guid clipId, long frames)
    {
        var (plan, applied, note, error) = PlanSlipOf(clipId, frames);
        if (error is not null) return TimelineEditResult.Fail(error);
        if (plan is null) return note is null ? TimelineEditResult.Unchanged() : TimelineEditResult.Fail(note);

        Commit(plan, "Slip Clip");
        return TimelineEditResult.Ok(new[] { clipId }, note);
    }

    public SlipPreview? PreviewSlip(Guid clipId, long frames)
    {
        var (plan, applied, note, error) = PlanSlipOf(clipId, frames);
        if (error is not null) return null;
        var clip = (MediaBackedClip)Sequence.VideoTracks.Concat(Sequence.AudioTracks).SelectMany(t => t.Clips).First(c => c.Id == clipId);
        var state = SlippedState(clip, applied, Settings.FrameRate);
        return new SlipPreview(state.SourceIn, state.SourceOut, applied, note);
    }

    /// <summary>The slip's plan (validated), the frames it applies and the note of a limit; Plan null when nothing
    /// changes.</summary>
    private (EditPlan? Plan, long Applied, string? Note, string? Error) PlanSlipOf(Guid clipId, long frames, bool alone = false)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return (null, 0, null, resolveError);
        var clip = clips[0];
        if (clip is not (VideoClip or AudioClip)) return (null, 0, null, Messages.Edit_SlipOnlyMedia);
        if (CheckEditable(plan, clips) is { } editError) return (null, 0, null, editError);
        if (SourceDuration(clip) is not { } duration) return (null, 0, null, Messages.Edit_SlipNoLength);

        var media = (MediaBackedClip)clip;
        var rate = plan.Rate;
        var track = plan.TrackOf(clip);

        // A dissolve keeps the source it needs beyond the clip (D025 §4): before its start (B), after its end (A) — only the
        // types that need handles (D044 §3: Dissolve, Push; D032: the crossfade).
        long needBefore = 0, needAfter = 0;
        foreach (var t in track.Transitions.Where(t => TransitionRules.RequiresHandles(t.TransitionTypeId)))
        {
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate));
            if (t.RightClipId == clip.Id) needBefore = beforeCut;
            if (t.LeftClipId == clip.Id) needAfter = afterCut;
        }
        var asset = AssetOf(clip);
        bool SourceOk(long k)
        {
            var st = SlippedState(media, k, rate);
            return st.SourceIn >= MediaTime.Zero && st.SourceOut <= duration;
        }
        bool DissolveOk(long k)
        {
            var st = SlippedState(media, k, rate);
            return (needBefore == 0 || DissolveHandles.Before(clip, st, asset, rate) >= needBefore) &&
                   (needAfter == 0 || DissolveHandles.After(clip, st, asset, rate) >= needAfter);
        }

        // Δ(k) grows with k, so each limit is monotone and the allowed slips are an interval around 0: its ends by binary
        // search, no further than the whole source.
        var bound = SpeedTiming.FramesFor(duration, media.Speed, rate) + 2;
        long Reach(int direction, Func<long, bool> ok)
        {
            if (!ok(0)) return 0;
            long lo = 0, hi = bound;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                if (ok(direction * mid)) lo = mid;
                else hi = mid - 1;
            }
            return direction * lo;
        }
        bool Allowed(long k) => SourceOk(k) && DissolveOk(k);
        var applied = Math.Clamp(frames, Reach(-1, Allowed), Reach(+1, Allowed));

        string? note = null;
        if (applied != frames)
        {
            var sourceEnd = Reach(frames > 0 ? 1 : -1, SourceOk);
            note = applied != sourceEnd
                ? IsNamed(track, EdgeTypeOf(track, clip, handlesOnly: true))
                    ? string.Format(Messages.Edit_SlipStoppedNamed, TypeName(EdgeTypeOf(track, clip, handlesOnly: true)!))
                : Messages.Edit_SlipStoppedDissolve
                : frames > 0 ? Messages.Edit_SlipStoppedEnd
                : Messages.Edit_SlipStoppedStart;
        }

        // AQ7: the partner slips by the same timeline frames, by the same rule and limits; both take the smaller slip.
        if (!alone && applied != 0)
        {
            var (partner, partnerError) = FollowingPartner(plan, clip);
            if (partnerError is not null) return (null, 0, null, partnerError);
            if (partner is not null)
            {
                var (_, partnerApplied, partnerNote, slipError) = PlanSlipOf(partner.Id, applied, alone: true);
                if (slipError is not null) return (null, 0, null, slipError);
                if (partnerApplied != applied)
                {
                    applied = partnerApplied;
                    note = partnerNote;
                }
                if (applied != 0) plan.Update(partner, plan.TrackOf(partner), SlippedState(partner, applied, rate));
            }
        }
        if (applied == 0) return (null, 0, note, null);

        // Only the source mapping changes: the clip's timing, speed, fades and dissolves stay (D030 §7).
        plan.Update(clip, track, SlippedState(media, applied, rate));
        if (Validate(plan) is { } invalid) return (null, 0, null, string.Format(Messages.Edit_CantSlip, invalid));
        return (plan, applied, note, null);
    }

    /// <summary><paramref name="clip"/>'s timing slipped by <paramref name="frames"/> timeline frames (D030 §7, D022):
    /// <c>SourceIn</c> moves by the start trim's own amount — 1× <c>FromFrame(S + k) − FromFrame(S)</c>, another speed
    /// <c>±SourceLength(|k|)</c> — and <c>SourceOut</c> by the same, so the source width, the frame count and D022's
    /// invariant are unchanged; start, duration and speed stay. Positive: later source content.</summary>
    private static ClipState SlippedState(MediaBackedClip clip, long frames, FrameRate rate)
    {
        var state = ClipState.Capture(clip);
        if (frames == 0) return state;
        MediaTime delta;
        if (clip.Speed.IsNormal)
        {
            var start = clip.TimelineStart.ToNearestFrame(rate);
            delta = MediaTime.FromFrame(start + frames, rate) - MediaTime.FromFrame(start, rate);
        }
        else
        {
            var length = SpeedTiming.SourceLength(Math.Abs(frames), clip.Speed, rate);
            delta = frames > 0 ? length : MediaTime.Zero - length;
        }
        return state with { SourceIn = state.SourceIn + delta, SourceOut = state.SourceOut + delta };
    }

    // --- Delete / tracks -------------------------------------------------------

    public TimelineEditResult DeleteClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return TimelineEditResult.Unchanged();

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var clips = WithPartners(plan, selected);   // AQ2: a linked clip goes with its partner

        if (clips.Select(plan.TrackOf).FirstOrDefault(t => t.IsLocked) is { } locked)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, locked.Name));

        foreach (var clip in clips) plan.Remove(clip);
        plan.ReconcileTransitions();   // a deleted clip's dissolves go with it (D025 §5)

        Commit(plan, clips.Count == 1 ? "Delete Clip" : "Delete Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
    }

    public TimelineEditResult AddTrack(TrackType type)
    {
        var track = NewTrack(type);
        _undoRedo.Execute(new NotifyingCommand(new AddTrackCommand(Sequence, track), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    public string? GetDeleteTrackBlockReason(Guid trackId)
    {
        if (FindTrack(trackId) is not { } track) return Messages.Edit_TrackGone;
        if (track.IsLocked) return string.Format(Messages.Edit_TrackLocked, track.Name);
        if (Sequence.VideoTracks.Count + Sequence.AudioTracks.Count <= 1) return Messages.Edit_NeedsOneTrack;
        // D032 R5, AQ12: deleting the track clears the links of its clips' partners — never on a locked track.
        if (LinkedPartners(track).FirstOrDefault(p => p.Track.IsLocked) is { Track: { } lockedTrack })
            return string.Format(Messages.Edit_TrackLocked, lockedTrack.Name);
        return null;
    }

    /// <summary>The clips on other tracks linked to a clip of <paramref name="track"/>, with their tracks.</summary>
    private List<(Track Track, MediaBackedClip Clip)> LinkedPartners(Track track) =>
        Sequence.VideoTracks.Concat(Sequence.AudioTracks).Where(t => t != track)
            .SelectMany(t => t.Clips.OfType<MediaBackedClip>().Select(c => (Track: t, Clip: c)))
            .Where(p => p.Clip.LinkId is { } link && track.Clips.OfType<MediaBackedClip>().Any(c => c.LinkId == link))
            .ToList();

    public TimelineEditResult DeleteTrack(Guid trackId)
    {
        if (GetDeleteTrackBlockReason(trackId) is { } blocked) return TimelineEditResult.Fail(blocked);
        var track = FindTrack(trackId)!;

        // D032 R5: the clips linked to the track's clips lose their link in the same step (a locked one refused above).
        var partners = LinkedPartners(track);

        // The clips and dissolves stay on the track object, so Undo brings them back exactly (D027 §3).
        IUndoableCommand command = new RemoveTrackCommand(Sequence, track);
        if (partners.Count > 0)
            command = new CompositeCommand(command.Description, partners
                .Select(p => (IUndoableCommand)new SetClipLinkCommand(p.Clip, ClipLinkState.Capture(p.Clip),
                    ClipLinkState.Capture(p.Clip) with { LinkId = null }, command.Description))
                .Prepend(command));
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    public TimelineEditResult MoveTrack(Guid trackId, int direction)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction), direction, "Use +1 or −1.");
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        // The layer order as the playback snapshot draws it (PlaybackSnapshotBuilder): by Order, equal orders by their
        // place in the list — lowest first.
        var list = track.Type == TrackType.Video ? Sequence.VideoTracks : Sequence.AudioTracks;
        var ordered = list.Select((t, index) => (t, index)).OrderBy(x => x.t.Order).ThenBy(x => x.index).Select(x => x.t).ToList();
        var position = ordered.IndexOf(track);
        var target = position + direction;
        if (target < 0 || target >= ordered.Count) return TimelineEditResult.Unchanged();

        var neighbour = ordered[target];
        if (neighbour.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLockedNeighbour, neighbour.Name, track.Name));

        List<TrackOrderChange> changes;
        if (track.Order != neighbour.Order)
        {
            changes = new() { new(track, track.Order, neighbour.Order), new(neighbour, neighbour.Order, track.Order) };
        }
        else
        {
            // Equal orders (a file from elsewhere) can't be swapped: number the tracks of this kind 0, 1, … in the new
            // order, which keeps every other track where it is drawn.
            (ordered[position], ordered[target]) = (neighbour, track);
            changes = ordered.Select((t, i) => new TrackOrderChange(t, t.Order, i)).Where(c => c.Before != c.After).ToList();
        }

        _undoRedo.Execute(new NotifyingCommand(new SetTrackOrderCommand(changes), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    // --- Track state (D030 §4) ------------------------------------------------------------
    // Mute and hide are monitoring state, so a locked track takes them too (D030 Q13); the playback snapshot reads the
    // flags, so the Preview and the export follow without any other change.

    public TimelineEditResult SetTrackMuted(Guid trackId, bool muted) => SetTrackState(trackId, TrackStateFlag.Muted, muted);

    public TimelineEditResult SetTrackHidden(Guid trackId, bool hidden)
    {
        if (FindTrack(trackId) is { Type: TrackType.Audio } track)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_AudioTrackNoPicture, track.Name));
        return SetTrackState(trackId, TrackStateFlag.Hidden, hidden);
    }

    public TimelineEditResult SetTrackLocked(Guid trackId, bool locked) => SetTrackState(trackId, TrackStateFlag.Locked, locked);

    private TimelineEditResult SetTrackState(Guid trackId, TrackStateFlag flag, bool value)
    {
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (SetTrackStateCommand.Get(track, flag) == value) return TimelineEditResult.Unchanged();

        _undoRedo.Execute(new NotifyingCommand(new SetTrackStateCommand(track, flag, value), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    // --- Ripple delete and close gap (D027 §2) ---------------------------------

    public TimelineEditResult RippleDeleteClips(IReadOnlyCollection<Guid> clipIds)
    {
        if (clipIds.Count == 0) return TimelineEditResult.Unchanged();

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, clipIds, out var selected) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var clips = WithPartners(plan, selected);   // AQ2: a linked clip goes with its partner
        if (CheckEditable(plan, clips) is { } editError) return TimelineEditResult.Fail(editError);

        var rate = plan.Rate;
        var removed = clips.ToHashSet();

        // What each removed clip closes up: its own length — except a linked pair, whose two tracks both close by the
        // length of its target clip (the one named last, the selection's primary; or the one named): so the later clips of
        // both tracks move by the same frames and later linked pairs keep their offset, J / L cuts included (product
        // owner, 2026-10-12, after the Phase 17 closeout; AQ4 / L5).
        long Frames(Clip c) => c.TimelineEnd.ToNearestFrame(rate) - c.TimelineStart.ToNearestFrame(rate);
        var closes = clips.ToDictionary(c => c, Frames);
        foreach (var clip in selected)
        {
            if (PartnerOf(plan, clip) is { } partner && removed.Contains(partner))
                closes[clip] = closes[partner] = Frames(clip);
        }

        foreach (var group in clips.GroupBy(plan.TrackOf))
        {
            // Per track: a clip that is not removed moves left by what the removed clips that end at or before its start
            // close up. Clips on other tracks, the playhead and the markers stay; gaps that are not removed move along.
            var spans = group.Select(c => (End: c.TimelineEnd.ToNearestFrame(rate), Closes: closes[c])).ToList();
            foreach (var clip in group) plan.Remove(clip);
            foreach (var clip in group.Key.Clips.Where(c => !removed.Contains(c)))
            {
                var start = clip.TimelineStart.ToNearestFrame(rate);
                var shift = spans.Where(s => s.End <= start).Sum(s => s.Closes);
                if (shift > 0 && PlanShift(plan, clip, group.Key, -shift) is { } shiftError)
                    return TimelineEditResult.Fail(string.Format(Messages.Edit_CantRippleDelete, shiftError));
            }
        }

        // A removed clip's dissolves go with it; every other dissolve has both clips moved by the same distance and is
        // kept as it is; none is created where clips now meet (D027 §2, D025 §5). The result is validated like any edit.
        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantRippleDelete, error));

        Commit(plan, clips.Count == 1 ? "Ripple Delete Clip" : "Ripple Delete Clips");
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
    }

    public TimelineEditResult CloseGap(Guid trackId, MediaTime at)
    {
        if (FindTrack(trackId) is not { } track) return TimelineEditResult.Fail(Messages.Edit_TrackGone);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var frame = at.ToFrameFloor(rate);
        var spans = track.Clips.Select(c => (Clip: c, Start: c.TimelineStart.ToNearestFrame(rate), End: c.TimelineEnd.ToNearestFrame(rate))).ToList();
        if (frame < 0 || spans.Any(s => s.Start <= frame && frame < s.End))
            return TimelineEditResult.Fail(Messages.Edit_NoGapThere);
        var following = spans.Where(s => s.Start > frame).ToList();
        if (following.Count == 0)
            return TimelineEditResult.Fail(Messages.Edit_NoGapNoClipFollows);
        var gapEnd = following.Min(s => s.Start);
        var gapStart = spans.Where(s => s.End <= frame).Select(s => s.End).DefaultIfEmpty(0).Max();

        // Every clip from the gap's end on moves left by the gap's length; a dissolve's two clips move together.
        foreach (var s in following)
        {
            if (PlanShift(plan, s.Clip, track, gapStart - gapEnd) is { } shiftError)
                return TimelineEditResult.Fail(string.Format(Messages.Edit_CantCloseGap, shiftError));
        }
        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantCloseGap, error));

        Commit(plan, "Close Gap");
        return TimelineEditResult.Ok(following.Select(s => s.Clip.Id).ToList(), TransitionNote(plan));
    }

    public TimelineEditResult CloseGapBefore(Guid clipId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);
        var clip = clips[0];
        var rate = plan.Rate;
        var startFrame = clip.TimelineStart.ToNearestFrame(rate);
        var track = plan.TrackOf(clip);
        if (startFrame == 0 || track.Clips.Any(c => c != clip && c.TimelineEnd.ToNearestFrame(rate) == startFrame))
            return TimelineEditResult.Fail(Messages.Edit_NoGapBefore);
        return CloseGap(track.Id, MediaTime.FromFrame(startFrame - 1, rate));
    }

    // --- Markers (D027 §6) ------------------------------------------------------

    /// <summary>The marker on <paramref name="frame"/> of the current grid, if any (markers keep their time when the frame
    /// rate changes, so the nearest frame decides).</summary>
    private Marker? MarkerOnFrame(long frame) =>
        Sequence.Markers.FirstOrDefault(m => m.Position.ToNearestFrame(FrameRate) == frame);

    public TimelineEditResult AddMarker(MediaTime at)
    {
        var frame = Math.Max(0, at.ToNearestFrame(FrameRate));
        if (MarkerOnFrame(frame) is not null) return TimelineEditResult.Fail(Messages.Edit_MarkerExists);

        var marker = new Marker { Position = MediaTime.FromFrame(frame, FrameRate) };
        _undoRedo.Execute(new NotifyingCommand(new AddMarkerCommand(Sequence, marker), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, MarkerId = marker.Id };
    }

    public TimelineEditResult RemoveMarkerAt(MediaTime at)
    {
        if (MarkerOnFrame(at.ToNearestFrame(FrameRate)) is not { } marker)
            return TimelineEditResult.Fail(Messages.Edit_NoMarkerAtPlayhead);

        _undoRedo.Execute(new NotifyingCommand(new RemoveMarkerCommand(Sequence, marker), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, MarkerId = marker.Id };
    }

    public MediaTime? NextMarker(MediaTime from)
    {
        var frame = from.ToNearestFrame(FrameRate);
        return Sequence.Markers.Where(m => m.Position.ToNearestFrame(FrameRate) > frame)
            .OrderBy(m => m.Position.Ticks).FirstOrDefault()?.Position;
    }

    public MediaTime? PreviousMarker(MediaTime from)
    {
        var frame = from.ToNearestFrame(FrameRate);
        return Sequence.Markers.Where(m => m.Position.ToNearestFrame(FrameRate) < frame)
            .OrderBy(m => m.Position.Ticks).LastOrDefault()?.Position;
    }

    // --- Media removal (D027 §4) ------------------------------------------------

    private static bool Uses(Clip clip, Guid mediaAssetId) => clip is MediaBackedClip m && m.MediaAssetId == mediaAssetId;

    public int CountClipsUsing(Guid mediaAssetId) =>
        Sequence.VideoTracks.Concat(Sequence.AudioTracks).SelectMany(t => t.Clips).Count(c => Uses(c, mediaAssetId));

    public string? GetRemoveMediaBlockReason(Guid mediaAssetId)
    {
        if (FindAsset(mediaAssetId) is not { } asset) return Messages.Edit_MediaGone;
        if (Sequence.VideoTracks.Concat(Sequence.AudioTracks).FirstOrDefault(t => t.IsLocked && t.Clips.Any(c => Uses(c, mediaAssetId)))
            is { } locked)
            return string.Format(Messages.Edit_UsedOnLockedTrack, asset.FileName, locked.Name);
        return null;
    }

    public TimelineEditResult RemoveMedia(Guid mediaAssetId)
    {
        if (GetRemoveMediaBlockReason(mediaAssetId) is { } blocked) return TimelineEditResult.Fail(blocked);
        var asset = FindAsset(mediaAssetId)!;

        var plan = new EditPlan(Sequence, Settings);
        var clips = plan.AllTracks.SelectMany(t => t.Clips).Where(c => Uses(c, mediaAssetId)).ToList();
        foreach (var clip in clips) plan.Remove(clip);
        plan.ReconcileTransitions();   // the removed clips' dissolves go with them (D025 §5)
        plan.ReconcileLinks();         // and the links of their partners (D032 R5), never on a locked track (AQ12)
        if (plan.ClearedLinks.FirstOrDefault(c => plan.TrackOf(c).IsLocked) is { } lockedPartner)
            return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, plan.TrackOf(lockedPartner).Name));

        // The clips (and dissolves) first, then the asset: no clip ever refers to an asset that isn't in the project.
        var removeAsset = new RemoveMediaAssetCommand(Project.MediaAssets, asset, _projectService.NotifyMediaAssetsChanged);
        IUndoableCommand command = clips.Count == 0
            ? removeAsset
            : new NotifyingCommand(new CompositeCommand("Remove Media", new[] { plan.BuildCommand("Remove Media"), removeAsset }),
                _projectService.NotifyTimelineChanged);
        _undoRedo.Execute(command);
        return TimelineEditResult.Ok(clips.Select(c => c.Id).ToList(), TransitionNote(plan));
    }

    // --- Clip properties -------------------------------------------------------

    public TimelineEditResult SetClipProperties(Guid clipId, ClipPropertyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var plan = new EditPlan(Sequence, Settings);
        if (Resolve(plan, new[] { clipId }, out var clips) is { } resolveError) return TimelineEditResult.Fail(resolveError);

        var clip = clips[0];
        var track = plan.TrackOf(clip);
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        if (ClipPropertyValidator.Validate(clip, change) is { } error)
            return TimelineEditResult.Fail(error);

        FadeProperties? fade = null;
        if (change.Fade is { } requested)
        {
            if (NormalizeFades(clip, requested, plan.Rate, out var normalized) is { } fadeError)
                return TimelineEditResult.Fail(fadeError);
            fade = normalized;
        }

        // Timing is untouched, so no timeline validation is needed: the clip's placement,
        // grid alignment and source range stay exactly as they are.
        var before = ClipPropertyValues.Capture(clip);
        var after = new ClipPropertyValues(
            change.Visual ?? before.Visual,
            change.Audio ?? before.Audio,
            change.Text ?? before.Text,
            fade ?? before.Fade,
            change.Template ?? before.Template);

        var fields = ClipPropertyValues.Diff(before, after);
        if (fields == ClipPropertyFields.None) return TimelineEditResult.Unchanged();

        var command = new SetClipPropertiesCommand(clip, before, after, fields, change.Gesture);
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    // --- Frame rate (D028, Step 13.5) ---------------------------------------------------------------------------------

    public TimelineEditResult SetFrameRate(FrameRate rate)
    {
        if (RateError(rate) is { } rateError) return TimelineEditResult.Fail(rateError);
        if (!ChangesRate(rate)) return TimelineEditResult.Unchanged();
        if (PlanFrameRate(rate, out var plan, out var command) is { } error) return TimelineEditResult.Fail(error);

        _undoRedo.Execute(new NotifyingCommand(command, NotifyRateChanged));
        return TimelineEditResult.Ok(null, RateMessage(rate, plan));
    }

    private static string? RateError(FrameRate rate) => rate.IsValid && ProjectSettingsRules.IsSelectableFrameRate(rate)
        ? null
        : rate.IsValid ? string.Format(Messages.Edit_RateNotOffered, FormatRate(rate)) : Messages.Edit_ThatRateNotOffered;

    /// <summary>A different rate, or the current one of an unlocked project (FR-4: choosing it locks it).</summary>
    private bool ChangesRate(FrameRate rate) => rate != Settings.FrameRate || !Settings.IsFrameRateLocked;

    /// <summary>
    /// The rate change as one command, built without touching the model: the current rate of an unlocked project only
    /// locks it (FR-4, <paramref name="plan"/> null); another rate is an <see cref="EditPlan"/> with the rate locked, the
    /// re-grid of every track and the usual validation. Returns the reason when the change is refused.
    /// </summary>
    private string? PlanFrameRate(FrameRate rate, out EditPlan? plan, out IUndoableCommand command)
    {
        plan = null;
        command = null!;
        if (rate == Settings.FrameRate)
        {
            command = new SetFrameRateCommand(Settings, rate, Settings.IsFrameRateLocked, rate, true);
            return null;
        }

        plan = new EditPlan(Sequence, Settings);
        plan.SetFrameRate(rate, locked: true);
        if (FrameRateRegrid.Plan(plan, rate, FindAsset) is { } regridError)
            return string.Format(Messages.Edit_CantChangeRate, FormatRate(rate), regridError);
        if (Validate(plan) is { } error)
            return string.Format(Messages.Edit_CantChangeRate, FormatRate(rate), error);
        command = plan.BuildCommand("Set Frame Rate");
        return null;
    }

    /// <summary>After a rate change, its Undo and its Redo: the playhead (session state, D015) goes to its nearest frame
    /// of the grid; markers keep their time.</summary>
    private void NotifyRateChanged()
    {
        Sequence.PlayheadPosition = Sequence.PlayheadPosition.SnapToFrame(Settings.FrameRate);
        _projectService.NotifyTimelineChanged();
    }

    private string RateMessage(FrameRate rate, EditPlan? plan) => plan is null
        ? string.Format(Messages.Edit_RateKept, FormatRate(rate))
        : Join(string.Format(Messages.Edit_RateSet, FormatRate(rate)), TransitionNote(plan))!;

    // --- Canvas (D028, Step 13.4) -------------------------------------------------------------------------------------

    private FrameSize CurrentCanvas => new(Settings.FrameWidth, Settings.FrameHeight);

    public TimelineEditResult SetCanvasSize(int width, int height)
    {
        if (ProjectSettingsRules.CanvasError(width, height) is { } sizeError) return TimelineEditResult.Fail(sizeError);
        if (width == Settings.FrameWidth && height == Settings.FrameHeight) return TimelineEditResult.Unchanged();
        if (PlanCanvas(width, height, out var commands) is { } error) return TimelineEditResult.Fail(error);

        _undoRedo.Execute(new NotifyingCommand(new CompositeCommand("Set Frame Size", commands), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(ScaledClips(commands));
    }

    /// <summary>
    /// The canvas change as commands, built without touching the model (CS-1 B): the size, then every video / image /
    /// text clip whose position or font size the contain factor changes — on every track. Each property command carries
    /// only the picture and text groups, so it never writes back a fade (a rate change in the same step may cut one).
    /// Returns the reason when a scaled value leaves the limits.
    /// </summary>
    private string? PlanCanvas(int width, int height, out List<IUndoableCommand> commands)
    {
        var (oldWidth, oldHeight) = (Settings.FrameWidth, Settings.FrameHeight);
        var scale = ProjectSettingsRules.ContainFactor(oldWidth, oldHeight, width, height);

        commands = new List<IUndoableCommand> { new SetCanvasSizeCommand(Settings, oldWidth, oldHeight, width, height) };
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
        {
            foreach (var clip in track.Clips)
            {
                if (VisualProperties.Of(clip) is not { } visual) continue;   // audio: nothing in canvas pixels

                var text = TextProperties.Of(clip);
                var change = new ClipPropertyChange
                {
                    Visual = visual with { PositionX = visual.PositionX * scale, PositionY = visual.PositionY * scale },
                    Text = text is { } t ? t with { FontSize = t.FontSize * scale } : null
                };
                if (ClipPropertyValidator.Validate(clip, change) is { } error)
                    return string.Format(Messages.Edit_CantChangeSize, width, height, Describe(clip), track.Name, error);

                var before = new ClipPropertyValues(visual, null, text);
                var after = new ClipPropertyValues(change.Visual, null, change.Text);
                var fields = ClipPropertyValues.Diff(before, after);
                if (fields != ClipPropertyFields.None)
                    commands.Add(new SetClipPropertiesCommand(clip, before, after, fields));
            }
        }
        return null;
    }

    private static IReadOnlyList<Guid> ScaledClips(IEnumerable<IUndoableCommand> commands) =>
        commands.OfType<SetClipPropertiesCommand>().Select(c => c.Clip.Id).ToList();

    // --- Project settings: canvas and rate together (D028, Step 13.6) -------------------------------------------------

    public TimelineEditResult SetProjectSettings(int width, int height, FrameRate? rate, ExportEncoding? export = null)
    {
        // Every part is checked before anything else, so a refused part never lets another one through alone.
        if (ProjectSettingsRules.CanvasError(width, height) is { } sizeError) return TimelineEditResult.Fail(sizeError);
        if (rate is { } r && RateError(r) is { } rateError) return TimelineEditResult.Fail(rateError);
        if (export?.Validate() is { } exportError) return TimelineEditResult.Fail(exportError);

        var canvasChanges = width != Settings.FrameWidth || height != Settings.FrameHeight;
        var rateChanges = rate is { } wanted && ChangesRate(wanted);
        var exportChanges = export is not null && export != Settings.Export;
        switch ((canvasChanges, rateChanges, exportChanges))
        {
            case (false, false, false): return TimelineEditResult.Unchanged();
            case (true, false, false): return SetCanvasSize(width, height);
            case (false, true, false): return SetFrameRate(rate!.Value);
            case (false, false, true): return SetExportSettings(export!);
        }

        // Several: every check first, on the unchanged model — the re-grid and its validation, then the scaled properties;
        // the export settings were checked above. The parts are independent (the re-grid changes timing and may cut a fade;
        // the canvas changes positions and font sizes, its commands carrying only those groups; the export settings are one
        // value of the project's settings), so the order inside the step doesn't matter for the result; the rate goes first.
        // One step, one notification (with the playhead on the new grid when the rate changes).
        var commands = new List<IUndoableCommand>();
        var messages = new List<string?>();
        EditPlan? plan = null;
        if (rateChanges)
        {
            if (PlanFrameRate(rate!.Value, out plan, out var rateCommand) is { } planError) return TimelineEditResult.Fail(planError);
            commands.Add(rateCommand);
        }
        var canvasCommands = new List<IUndoableCommand>();
        if (canvasChanges)
        {
            if (PlanCanvas(width, height, out canvasCommands) is { } canvasError) return TimelineEditResult.Fail(canvasError);
            commands.AddRange(canvasCommands);
            messages.Add(string.Format(Messages.Edit_FrameSizeSet, width, height));
        }
        if (rateChanges) messages.Add(RateMessage(rate!.Value, plan));
        if (exportChanges)
        {
            commands.Add(new SetExportEncodingCommand(Settings, Settings.Export, export!));
            messages.Add(ExportMessage(export!));
        }

        _undoRedo.Execute(new NotifyingCommand(new CompositeCommand("Change Project Settings", commands),
            rateChanges ? NotifyRateChanged : _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(ScaledClips(canvasCommands), string.Join(" ", messages.Where(m => m is not null)));
    }

    // --- Export settings (D028, Step 13.9) -------------------------------------------------------------------------------

    public TimelineEditResult SetExportSettings(ExportEncoding export)
    {
        ArgumentNullException.ThrowIfNull(export);
        if (export.Validate() is { } error) return TimelineEditResult.Fail(error);
        if (export == Settings.Export) return TimelineEditResult.Unchanged();

        // A project change (EX-1): undoable and dirty; nothing on the timeline changes, so no notification is needed.
        _undoRedo.Execute(new SetExportEncodingCommand(Settings, Settings.Export, export));
        return TimelineEditResult.Ok(null, ExportMessage(export));
    }

    private static string ExportMessage(ExportEncoding e) =>
        string.Format(Messages.Settings_ExportSet, e.Quality switch
        {
            ExportQuality.Maximum => Messages.ExportQuality_Maximum,
            ExportQuality.High => Messages.ExportQuality_High,
            ExportQuality.Standard => Messages.ExportQuality_Standard,
            ExportQuality.Compact => Messages.ExportQuality_Compact,
            _ => e.Quality.ToString()
        }, e.PresetName, e.AudioBitrateKbps);

    /// <summary>How a clip is named in a message: a text clip by its first line, a media clip by its file.</summary>
    private string Describe(Clip clip)
    {
        if (clip is TextClip text)
        {
            var line = text.Text.Split('\n')[0].Trim();
            return string.Format(Messages.Describe_TextClip, line.Length > 30 ? line[..30] + "…" : line);
        }
        if (clip is TemplateClip template)
            return string.Format(Messages.Describe_TemplateClip, TemplateNames.Of(template.Kind));
        var name = clip is MediaBackedClip media ? FindAsset(media.MediaAssetId)?.FileName : null;
        return clip is ImageClip
            ? name is null ? Messages.Describe_ImageClip : string.Format(Messages.Describe_ImageClipOf, name)
            : name is null ? Messages.Describe_VideoClip : string.Format(Messages.Describe_VideoClipOf, name);
    }

    /// <summary>
    /// Checks and normalizes a fade change (D025 §2): each fade is a whole number of frames of the project rate
    /// (nearest, ties up), from 0 to the clip's length, stored as exactly that many frames. A fade that is not
    /// changed is kept as stored (it may exceed a clip trimmed shorter since: clamped when rendered).
    /// </summary>
    private static string? NormalizeFades(Clip clip, FadeProperties requested, FrameRate rate, out FadeProperties normalized)
    {
        normalized = FadeProperties.Of(clip);
        if (requested.FadeIn < MediaTime.Zero || requested.FadeOut < MediaTime.Zero) return Messages.Edit_FadeNegative;

        var frames = TransitionRules.ClipFrames(clip, rate);
        var fadeIn = requested.FadeIn == clip.FadeIn ? clip.FadeIn : Normalize(requested.FadeIn);
        var fadeOut = requested.FadeOut == clip.FadeOut ? clip.FadeOut : Normalize(requested.FadeOut);
        if (fadeIn != clip.FadeIn && TransitionRules.Frames(fadeIn, rate) > frames) return Messages.Edit_FadeInTooLong;
        if (fadeOut != clip.FadeOut && TransitionRules.Frames(fadeOut, rate) > frames) return Messages.Edit_FadeOutTooLong;

        normalized = new FadeProperties(fadeIn, fadeOut);
        return null;

        MediaTime Normalize(MediaTime value) => MediaTime.FromFrame(TransitionRules.Frames(value, rate), rate);
    }

    // --- Dissolves (D025 §3–§5) ------------------------------------------------------

    public TimelineEditResult AddTransition(Guid leftClipId, Guid rightClipId, MediaTime duration)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right) is { } cutError)
            return TimelineEditResult.Fail(cutError);
        var noun = TransitionNoun(track);
        if (CutHas(track, left) is { } cutHas) return TimelineEditResult.Fail(cutHas);

        // The track's kind decides the type: a dissolve on a video track, a crossfade on an audio track (D032 R9).
        var typeId = TransitionRules.TypeFor(track.Type);
        var rate = plan.Rate;
        var frames = TransitionRules.Frames(duration, rate);
        if (RangeError(typeId, frames, rate, noun) is { } rangeError)
            return TimelineEditResult.Fail(rangeError);
        if (TooLong(frames, MaxFrames(track, left, right, rate, except: null, typeId), noun, typeId) is { } tooLong)
            return TimelineEditResult.Fail(tooLong);

        return CommitAdd(plan, track, left, right, typeId, frames, noun);
    }

    public TimelineEditResult AddTransition(Guid leftClipId, Guid rightClipId, string typeId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right, typeId: typeId) is { } cutError)
            return TimelineEditResult.Fail(cutError);
        if (!TransitionRules.Allows(track.Type, typeId)) return TimelineEditResult.Fail(Messages.Transition_WrongType);
        var noun = TransitionNoun(track);
        if (CutHas(track, left) is { } cutHas) return TimelineEditResult.Fail(cutHas);

        // D044 §6: the type's default, or the longest that fits when that is shorter (never below the type's minimum);
        // never another type.
        var rate = plan.Rate;
        var fits = MaxFrames(track, left, right, rate, except: null, typeId);
        if (TransitionRules.ClampFrames(typeId, TransitionRules.DefaultFramesOf(typeId, rate), fits, rate) is not { } frames)
            return TimelineEditResult.Fail(NothingFits(typeId, noun));

        return CommitAdd(plan, track, left, right, typeId, frames, noun);
    }

    private TimelineEditResult CommitAdd(EditPlan plan, Track track, Clip left, Clip right, string typeId, long frames, string noun)
    {
        var transition = new Transition
        {
            TransitionTypeId = typeId, Duration = MediaTime.FromFrame(frames, plan.Rate),
            LeftClipId = left.Id, RightClipId = right.Id, Direction = TransitionRules.DefaultDirection(typeId)
        };
        plan.AddTransition(track, transition);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(IsNamed(track, typeId)
                ? string.Format(Messages.Transition_CantAddNamed, TypeName(typeId), error)
                : string.Format(Tr(noun, Messages.Transition_CantAddDissolve, Messages.Transition_CantAddCrossfade), error));

        var description = typeId is TransitionRules.CrossDissolve or TransitionRules.Crossfade ? $"Add {Title(noun)}" : "Add Transition";
        _undoRedo.Execute(new NotifyingCommand(new AddTransitionCommand(track, transition, description), _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { left.Id, right.Id }, TransitionId = transition.Id };
    }

    public TimelineEditResult SetTransitionType(Guid transitionId, string typeId)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail(Messages.Edit_DissolveGone);
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
        if (!TransitionRules.Allows(track.Type, typeId)) return TimelineEditResult.Fail(Messages.Transition_WrongType);
        if (typeId == transition.TransitionTypeId) return TimelineEditResult.Unchanged();

        // D044 §6: the duration is kept when it lies in the new type's range, else clamped to the nearest allowed value
        // (the longest that fits for a type needing handles); one step with the type, never merged.
        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var noun = TransitionNoun(track);
        var left = track.Clips.First(c => c.Id == transition.LeftClipId);
        var right = track.Clips.First(c => c.Id == transition.RightClipId);
        var fits = MaxFrames(track, left, right, rate, except: transition, typeId);
        if (TransitionRules.ClampFrames(typeId, TransitionRules.Frames(transition.Duration, rate), fits, rate) is not { } frames)
            return TimelineEditResult.Fail(NothingFits(typeId, noun));

        var before = TransitionState.Capture(track, transition);
        var stored = frames == TransitionRules.Frames(transition.Duration, rate) ? transition.Duration : MediaTime.FromFrame(frames, rate);
        var after = before with { TypeId = typeId, Direction = TransitionRules.DefaultDirection(typeId), Duration = stored };
        plan.UpdateTransition(transition, after);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(CantChange(track, transition, error));

        _undoRedo.Execute(new NotifyingCommand(new UpdateTransitionCommand(transition, before, after, "Change Transition Type"),
            _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { left.Id, right.Id }, TransitionId = transition.Id };
    }

    public TimelineEditResult SetTransitionDirection(Guid transitionId, TransitionDirection direction)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail(Messages.Edit_DissolveGone);
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
        if (!TransitionRules.HasDirection(transition.TransitionTypeId) || !Enum.IsDefined(direction))
            return TimelineEditResult.Fail(Messages.Transition_NoDirection);
        if (transition.Direction == direction) return TimelineEditResult.Unchanged();

        var before = TransitionState.Capture(track, transition);
        _undoRedo.Execute(new NotifyingCommand(
            new UpdateTransitionCommand(transition, before, before with { Direction = direction }, "Change Push Direction"),
            _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { transition.LeftClipId, transition.RightClipId }, TransitionId = transition.Id };
    }

    public TimelineEditResult RemoveTransition(Guid transitionId)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail(Messages.Edit_DissolveGone);
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        _undoRedo.Execute(new NotifyingCommand(new RemoveTransitionCommand(track, transition, $"Remove {Title(TransitionNoun(track))}"),
            _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { transition.LeftClipId, transition.RightClipId } };
    }

    public TimelineEditResult SetTransitionDuration(Guid transitionId, MediaTime duration)
    {
        if (FindTransition(transitionId) is not { } found) return TimelineEditResult.Fail(Messages.Edit_DissolveGone);
        var (track, transition) = found;
        if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        var noun = TransitionNoun(track);
        var typeId = transition.TransitionTypeId;
        var frames = TransitionRules.Frames(duration, rate);
        var stored = MediaTime.FromFrame(frames, rate);
        if (stored == transition.Duration) return TimelineEditResult.Unchanged();
        if (RangeError(typeId, frames, rate, noun) is { } rangeError)
            return TimelineEditResult.Fail(rangeError);

        var left = track.Clips.First(c => c.Id == transition.LeftClipId);
        var right = track.Clips.First(c => c.Id == transition.RightClipId);
        if (TooLong(frames, MaxFrames(track, left, right, rate, except: transition, typeId), noun, typeId) is { } tooLong)
            return TimelineEditResult.Fail(tooLong);

        var before = TransitionState.Capture(track, transition);
        var after = before with { Duration = stored };
        plan.UpdateTransition(transition, after);
        if (Validate(plan) is { } error)
            return TimelineEditResult.Fail(CantChange(track, transition, error));

        _undoRedo.Execute(new NotifyingCommand(new UpdateTransitionCommand(transition, before, after, $"Change {Title(noun)} Duration"),
            _projectService.NotifyTimelineChanged));
        return new TimelineEditResult { Success = true, ClipIds = new[] { left.Id, right.Id }, TransitionId = transition.Id };
    }

    public long? MaxTransitionFrames(Guid leftClipId, Guid rightClipId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right, allowLocked: true) is not null)
            return null;
        var own = track.Transitions.FirstOrDefault(t => t.LeftClipId == left.Id);
        return MaxFrames(track, left, right, plan.Rate, except: own, own?.TransitionTypeId ?? TransitionRules.TypeFor(track.Type));
    }

    public long? MaxTransitionFrames(Guid leftClipId, Guid rightClipId, string typeId)
    {
        var plan = new EditPlan(Sequence, Settings);
        if (ResolveCut(plan, leftClipId, rightClipId, out var track, out var left, out var right, allowLocked: true) is not null)
            return null;
        if (!TransitionRules.Allows(track.Type, typeId)) return null;
        var own = track.Transitions.FirstOrDefault(t => t.LeftClipId == left.Id);
        return Math.Min(MaxFrames(track, left, right, plan.Rate, except: own, typeId), TransitionRules.MaxFramesOf(typeId, plan.Rate));
    }

    /// <summary>The reason a duration of <paramref name="frames"/> is outside what the type allows (D044 §5), or null; the
    /// dissolve and the crossfade keep their minimum messages.</summary>
    private static string? RangeError(string typeId, long frames, FrameRate rate, string noun)
    {
        var min = TransitionRules.MinFramesOf(typeId, rate);
        var max = TransitionRules.MaxFramesOf(typeId, rate);
        if (frames >= min && frames <= max) return null;
        if (typeId is TransitionRules.CrossDissolve or TransitionRules.Crossfade)
            return string.Format(Tr(noun, Messages.Transition_MinDissolve, Messages.Transition_MinCrossfade), min);
        return max == long.MaxValue
            ? string.Format(Messages.Transition_DurationMin, min)
            : string.Format(Messages.Transition_DurationRange, min, max);
    }

    /// <summary>Why not even the type's minimum fits the cut: the source handles for a type that needs them (naming the
    /// types that need none, D044 §6), else the clips' length.</summary>
    private static string NothingFits(string typeId, string noun) =>
        !TransitionRules.RequiresHandles(typeId) ? Messages.TransitionRule_ClipTooShort
        : typeId == TransitionRules.Crossfade ? NotEnoughMediaFor(noun)
        : Messages.Transition_NotEnoughMediaType;

    /// <summary>What a transition of <paramref name="track"/> is called: a dissolve on a video track, a crossfade on an audio
    /// track (D032 R9).</summary>
    private static string TransitionNoun(Track track) => track.Type == TrackType.Audio ? "crossfade" : "dissolve";

    private static string Title(string noun) => char.ToUpperInvariant(noun[0]) + noun[1..];

    /// <summary>Two touching clips of one (unlocked) track, A left of B: a dissolve on a video track, a crossfade on an audio
    /// track (D032 R9).</summary>
    private static string? ResolveCut(EditPlan plan, Guid leftClipId, Guid rightClipId, out Track track, out Clip left, out Clip right,
        bool allowLocked = false, string? typeId = null)
    {
        track = null!;
        left = right = null!;
        if (Resolve(plan, new[] { leftClipId, rightClipId }, out var clips) is { } resolveError) return resolveError;
        if (clips.Count != 2) return IsNamed(typeId) ? Messages.TransitionRule_TwoClips : Messages.Edit_DissolveTwoClips;
        (left, right) = (clips[0], clips[1]);
        track = plan.TrackOf(left);
        if (plan.TrackOf(right) != track) return IsNamed(typeId) ? Messages.Edit_TransitionSameTrack : Messages.Edit_DissolveSameTrack;
        if (!allowLocked && track.IsLocked) return string.Format(Messages.Edit_TrackLocked, track.Name);
        if (left.TimelineEnd != right.TimelineStart)
            return Tr(track, typeId, Messages.Transition_NeedsMeetDissolve, Messages.Transition_NeedsMeetCrossfade, Messages.Transition_NeedsMeetNamed);
        // D045: a template starts and ends with its own animation; it takes no transition.
        if (left is TemplateClip || right is TemplateClip) return Messages.Transition_NotOnTemplate;
        return null;
    }

    /// <summary>The longest transition of <paramref name="typeId"/> the cut A | B can take (D025 §3–§4, D044 §3): the frames
    /// of each clip not used by its transition on the other edge, and the source handles for a type that needs them.
    /// <paramref name="except"/> is the cut's own transition.</summary>
    private long MaxFrames(Track track, Clip left, Clip right, FrameRate rate, Transition? except, string typeId)
    {
        var handles = TransitionRules.RequiresHandles(typeId);
        var others = track.Transitions.Where(t => t != except).ToList();
        long PartAtStart(Clip clip) => others.Where(t => t.RightClipId == clip.Id)
            .Select(t => TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate)).AfterCut).DefaultIfEmpty(0).Max();
        long PartAtEnd(Clip clip) => others.Where(t => t.LeftClipId == clip.Id)
            .Select(t => TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate)).BeforeCut).DefaultIfEmpty(0).Max();

        var leftState = ClipState.Capture(left);
        var rightState = ClipState.Capture(right);
        return TransitionRules.MaxFrames(
            roomInLeft: DissolveHandles.Frames(leftState, rate) - PartAtStart(left),
            roomInRight: DissolveHandles.Frames(rightState, rate) - PartAtEnd(right),
            handleAfterLeft: handles ? DissolveHandles.After(left, leftState, AssetOf(left), rate) : DissolveHandles.Unlimited,
            handleBeforeRight: handles ? DissolveHandles.Before(right, rightState, AssetOf(right), rate) : DissolveHandles.Unlimited);
    }

    private static string? TooLong(long frames, long max, string noun, string typeId) =>
        frames <= max ? null
        : typeId is not (TransitionRules.CrossDissolve or TransitionRules.Crossfade)
            ? max < TransitionRules.MinFrames ? NothingFits(typeId, noun) : string.Format(Messages.Transition_TooLongType, frames, max)
        : max < TransitionRules.MinFrames ? NotEnoughMediaFor(noun)
        : string.Format(Tr(noun, Messages.Transition_TooLongDissolve, Messages.Transition_TooLongCrossfade), frames, max);

    /// <summary><see cref="NotEnoughMedia"/> for a dissolve, the same words for a crossfade.</summary>
    private static string NotEnoughMediaFor(string noun) => Tr(noun, Messages.Transition_NotEnoughMediaDissolve, Messages.Transition_NotEnoughMediaCrossfade);

    /// <summary>The message of a dissolve or of a crossfade (D039: no noun is glued into a sentence).</summary>
    private static string Tr(string noun, string dissolve, string crossfade) => noun == "crossfade" ? crossfade : dissolve;

    private static string Tr(Track track, string dissolve, string crossfade) => Tr(TransitionNoun(track), dissolve, crossfade);

    /// <summary>True when a message about a transition of <paramref name="typeId"/> on <paramref name="track"/> names its
    /// type (D044): a library type other than the dissolve; a dissolve and a crossfade keep their own words.</summary>
    private static bool IsNamed(Track track, string? typeId) => track.Type == TrackType.Video && IsNamed(typeId);

    private static bool IsNamed(string? typeId) => typeId is not (null or TransitionRules.CrossDissolve or TransitionRules.Crossfade);

    /// <summary>The message of a dissolve, of a crossfade, or <paramref name="named"/> with the type's name for the other
    /// types of the library (D044).</summary>
    private static string Tr(Track track, string? typeId, string dissolve, string crossfade, string named) =>
        IsNamed(track, typeId) ? string.Format(named, TypeName(typeId!)) : Tr(track, dissolve, crossfade);

    /// <summary>The name of a library type in a message (the Inspector's and the menu's name).</summary>
    private static string TypeName(string typeId) => TransitionTypeNames.Of(typeId);

    /// <summary>The type a message about <paramref name="clip"/>'s transitions names: of the transitions at its
    /// <paramref name="edge"/> (both edges when null) — with <paramref name="handlesOnly"/> only those that need source
    /// handles — a library type before a dissolve; null when it has none.</summary>
    private static string? EdgeTypeOf(Track track, Clip clip, ClipEdge? edge = null, bool handlesOnly = false) =>
        track.Transitions
            .Where(t => (edge != ClipEdge.Start && t.LeftClipId == clip.Id) || (edge != ClipEdge.End && t.RightClipId == clip.Id))
            .Where(t => !handlesOnly || TransitionRules.RequiresHandles(t.TransitionTypeId))
            .Select(t => t.TransitionTypeId)
            .OrderBy(id => !IsNamed(id))
            .FirstOrDefault();

    /// <summary>The refusal when the cut after <paramref name="left"/> already has a transition, naming that one; else null.</summary>
    private static string? CutHas(Track track, Clip left) =>
        track.Transitions.FirstOrDefault(t => t.LeftClipId == left.Id) is { } existing
            ? Tr(track, existing.TransitionTypeId, Messages.Transition_CutHasDissolve, Messages.Transition_CutHasCrossfade, Messages.Transition_CutHasNamed)
            : null;

    private static string CantChange(Track track, Transition transition, string error) =>
        IsNamed(track, transition.TransitionTypeId)
            ? string.Format(Messages.Transition_CantChangeNamed, TypeName(transition.TransitionTypeId), error)
            : string.Format(Tr(track, Messages.Transition_CantChangeDissolve, Messages.Transition_CantChangeCrossfade), error);

    private (Track Track, Transition Transition)? FindTransition(Guid id)
    {
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
            if (track.Transitions.FirstOrDefault(t => t.Id == id) is { } transition)
                return (track, transition);
        return null;
    }

    // --- Snapping --------------------------------------------------------------

    public SnapResult Snap(IReadOnlyList<MediaTime> candidates, MediaTime tolerance, IReadOnlyCollection<Guid> excludedClipIds,
        Func<SnapResult, bool>? accept = null)
    {
        var excluded = excludedClipIds as ISet<Guid> ?? excludedClipIds.ToHashSet();
        var targets = new List<MediaTime> { MediaTime.Zero, Sequence.PlayheadPosition };
        targets.AddRange(Sequence.Markers.Select(m => m.Position));   // D027 §6
        foreach (var track in Sequence.VideoTracks.Concat(Sequence.AudioTracks))
        {
            foreach (var clip in track.Clips)
            {
                if (excluded.Contains(clip.Id)) continue;
                targets.Add(clip.TimelineStart);
                targets.Add(clip.TimelineEnd);
            }
        }

        return accept is null
            ? SnapEngine.Find(candidates, targets, tolerance)
            : SnapEngine.FindAll(candidates, targets, tolerance).FirstOrDefault(accept);
    }

    // --- Helpers ---------------------------------------------------------------

    /// <summary>The asset's rate if ffprobe reported a usable one, else the 30 FPS
    /// fallback — with a flag so the caller never presents the fallback as the
    /// file's real rate.</summary>
    public static (FrameRate Rate, bool FromSource) ResolveSourceFrameRate(MediaAsset asset)
    {
        if (asset.Metadata?.FrameRate is { IsValid: true } rate &&
            rate.Numerator >= (long)MinSourceFps * rate.Denominator &&
            rate.Numerator <= (long)MaxSourceFps * rate.Denominator)
        {
            return (rate, true);
        }

        return (FrameRate.Default, false);
    }

    public static string FormatRate(FrameRate rate) => rate.Denominator == 1
        ? rate.Numerator.ToString(CultureInfo.InvariantCulture)
        : Math.Round(rate.ToDouble(), 3).ToString("0.###", CultureInfo.InvariantCulture);

    private MediaAsset? FindAsset(Guid id) => Project.MediaAssets.FirstOrDefault(a => a.Id == id);

    private Track? FindTrack(Guid id) =>
        Sequence.VideoTracks.Concat(Sequence.AudioTracks).FirstOrDefault(t => t.Id == id);

    /// <summary>Source length limit for video/audio clips; null for clips without one (images, text).</summary>
    private MediaTime? SourceDuration(Clip clip) =>
        clip is VideoClip or AudioClip ? FindAsset(((MediaBackedClip)clip).MediaAssetId)?.Metadata?.Duration : null;

    private static string? Resolve(EditPlan plan, IEnumerable<Guid> ids, out List<Clip> clips)
    {
        var byId = plan.AllTracks.SelectMany(t => t.Clips).ToDictionary(c => c.Id);
        clips = new List<Clip>();
        foreach (var id in ids.Distinct())
        {
            if (!byId.TryGetValue(id, out var clip))
                return Messages.Edit_SelectedClipGone;
            clips.Add(clip);
        }
        return null;
    }

    private static string? CheckEditable(EditPlan plan, IEnumerable<Clip> clips)
    {
        foreach (var clip in clips)
        {
            if (plan.TrackOf(clip).IsLocked)
                return string.Format(Messages.Edit_TrackLocked, plan.TrackOf(clip).Name);
        }
        return null;
    }

    /// <summary>Validates the plan: the dissolves are first brought in line with the planned clips (D025 §5: removed
    /// where their cut is gone, moved with their clips) and so are the links (D032 R5: a link left without its other clip
    /// is cleared); then every affected track's clips and dissolves are checked — zones against the clips, and the source
    /// handles of every dissolve the edit creates or keeps (§4) — and the links.</summary>
    private string? Validate(EditPlan plan)
    {
        if (PlanPartnerShifts(plan) is { } partnerError) return partnerError;
        plan.ReconcileTransitions();
        plan.ClampFades();
        plan.ReconcileLinks();
        foreach (var track in plan.AffectedTracks)
        {
            if (TimelineValidator.ValidateTrack(track, plan.EffectiveClips(track), plan.Rate, FindAsset) is { } error)
                return error;
        }
        foreach (var track in plan.TransitionTracks)
        {
            if (ValidateTransitions(plan, track) is { } error)
                return error;
        }
        // D044 §4: the fill peaks of every video track as the plan leaves them — rejected, never clamped.
        if (ValidateFillPeaks(plan) is { } peaksError) return peaksError;
        // A link the edit leaves without its other clip is cleared — never on a locked track (D032 L5, AQ12).
        foreach (var clip in plan.ClearedLinks)
        {
            if (plan.TrackOf(clip).IsLocked)
                return string.Format(Messages.Edit_TrackLocked, plan.TrackOf(clip).Name);
        }
        return LinkRules.Validate(plan.EffectiveLinks());
    }

    public static string NotEnoughMedia => Messages.Transition_NotEnoughMediaDissolve;

    private string? ValidateTransitions(EditPlan plan, Track track)
    {
        var transitions = plan.EffectiveTransitions(track).ToList();
        if (transitions.Count == 0) return null;

        var clips = plan.EffectiveClips(track).ToDictionary(c => c.Clip.Id);
        var specs = transitions.Select(t =>
            new TransitionSpec(t.Transition.Id, t.State.TypeId, t.State.Duration, t.State.LeftClipId, t.State.RightClipId, t.State.Direction));
        if (TransitionRules.Validate(track.Type, track.Name, specs,
                id => clips.TryGetValue(id, out var c) ? (c.State.Start, c.State.End) : null, plan.Rate) is { } error)
            return error;

        foreach (var (transition, state) in transitions)
        {
            if (!plan.IsTouched(transition) || !TransitionRules.RequiresHandles(state.TypeId)) continue;
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(state.Duration, plan.Rate));
            var left = clips[state.LeftClipId];
            var right = clips[state.RightClipId];
            if (afterCut > DissolveHandles.After(left.Clip, left.State, AssetOf(left.Clip), plan.Rate) ||
                beforeCut > DissolveHandles.Before(right.Clip, right.State, AssetOf(right.Clip), plan.Rate))
                return state.TypeId == TransitionRules.Push ? Messages.Transition_NotEnoughMediaType : NotEnoughMediaFor(TransitionNoun(track));
        }
        return null;
    }

    /// <summary>D044 §4 on the plan: every video track's transitions with their cut (the left clip's planned end).</summary>
    private static string? ValidateFillPeaks(EditPlan plan)
    {
        var peaks = new List<(string TypeId, MediaTime Cut)>();
        foreach (var track in plan.AllTracks.Where(t => t.Type == TrackType.Video))
        {
            var ends = plan.EffectiveClips(track).ToDictionary(c => c.Clip.Id, c => c.State.End);
            foreach (var (_, state) in plan.EffectiveTransitions(track))
                if (TransitionRules.IsFill(state.TypeId) && ends.TryGetValue(state.LeftClipId, out var cut))
                    peaks.Add((state.TypeId, cut));
        }
        return TransitionRules.ValidateFillPeaks(peaks);
    }

    private MediaAsset? AssetOf(Clip clip) => clip is MediaBackedClip m ? FindAsset(m.MediaAssetId) : null;

    /// <summary>The frames of <paramref name="clip"/> inside the dissolve zones at its start and its end (0 without one).</summary>
    private static (long AtStart, long AtEnd) DissolveParts(Track track, Clip clip, FrameRate rate)
    {
        long atStart = 0, atEnd = 0;
        foreach (var t in track.Transitions)
        {
            var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(t.Duration, rate));
            if (t.RightClipId == clip.Id) atStart = afterCut;
            if (t.LeftClipId == clip.Id) atEnd = beforeCut;
        }
        return (atStart, atEnd);
    }

    /// <summary>The status note for dissolves an edit removed because their cut is gone (D025 §5), or null.</summary>
    private static string? TransitionNote(EditPlan plan) => Join(
        plan.AutoRemovedVideoTypes switch
        {
            [] => null,
            [var one] => IsNamed(one) ? string.Format(Messages.Edit_TransitionRemovedNamed, TypeName(one)) : Messages.Edit_DissolveRemoved,
            var many => many.Any(IsNamed)
                ? string.Format(Messages.Edit_TransitionsRemoved, many.Count)
                : string.Format(Messages.Edit_DissolvesRemoved, many.Count)
        },
        plan.AutoRemovedCrossfades switch
        {
            0 => null,
            1 => Messages.Edit_CrossfadeRemoved,
            var n => string.Format(Messages.Edit_CrossfadesRemoved, n)
        });

    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : $"{a} {b}";

    private void Commit(EditPlan plan, string description)
    {
        var command = new NotifyingCommand(plan.BuildCommand(description), _projectService.NotifyTimelineChanged);
        _undoRedo.Execute(command);
    }

    private static Clip CreateClip(MediaAsset asset, ClipState state)
    {
        Clip clip = asset.Kind switch
        {
            MediaKind.Video => new VideoClip { MediaAssetId = asset.Id },
            MediaKind.Audio => new AudioClip { MediaAssetId = asset.Id },
            _ => new ImageClip { MediaAssetId = asset.Id }
        };
        state.ApplyTo(clip);
        return clip;
    }

    /// <summary>The clipboard's copy of <paramref name="clip"/>: <see cref="CloneClip"/> keeping the link, so a paste can
    /// tell which copied clips belong together (D032 R5). Never inserted itself.</summary>
    private static Clip CopyWithLink(Clip clip)
    {
        var copy = CloneClip(clip);
        if (copy is MediaBackedClip media && clip is MediaBackedClip source) media.LinkId = source.LinkId;
        return copy;
    }

    /// <summary>Copy of every clip property with a new Id (used for the right half of a split). The link is not copied:
    /// whoever makes the copy decides its link (D032 R5).</summary>
    private static Clip CloneClip(Clip source)
    {
        Clip copy = source switch
        {
            VideoClip v => new VideoClip
            {
                MediaAssetId = v.MediaAssetId, Speed = v.Speed, PositionX = v.PositionX, PositionY = v.PositionY,
                Scale = v.Scale, RotationDegrees = v.RotationDegrees, Opacity = v.Opacity, Volume = v.Volume, IsMuted = v.IsMuted,
                Crop = v.Crop, IsAudioDetached = v.IsAudioDetached,
                // D034 §6: the markup goes with the copy; it is immutable, so the two clips stay independent.
                HoldMarkup = v.HoldMarkup
            },
            AudioClip a => new AudioClip { MediaAssetId = a.MediaAssetId, Speed = a.Speed, Volume = a.Volume, IsMuted = a.IsMuted },
            ImageClip i => new ImageClip
            {
                MediaAssetId = i.MediaAssetId, Speed = i.Speed, PositionX = i.PositionX, PositionY = i.PositionY,
                Scale = i.Scale, RotationDegrees = i.RotationDegrees, Opacity = i.Opacity, Crop = i.Crop
            },
            TextClip t => new TextClip
            {
                Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, ColorHex = t.ColorHex, Alignment = t.Alignment,
                PositionX = t.PositionX, PositionY = t.PositionY, Scale = t.Scale, RotationDegrees = t.RotationDegrees, Opacity = t.Opacity,
                // D043: the style and both motions go with the copy (a split's parts keep them; their fades follow D025).
                Bold = t.Bold, Outline = t.Outline, Background = t.Background, MotionIn = t.MotionIn, MotionOut = t.MotionOut
            },
            TemplateClip p => new TemplateClip
            {
                Kind = p.Kind, CounterStart = p.CounterStart, Date = p.Date, Route = p.Route, Grade = p.Grade, Status = p.Status,
                AccentHex = p.AccentHex, Title = p.Title, Subtitle = p.Subtitle, StatusAt = p.StatusAt
            },
            _ => throw new NotSupportedException($"Unknown clip type {source.GetType().Name}.")
        };

        ClipState.Capture(source).ApplyTo(copy);
        (copy.FadeIn, copy.FadeOut) = (source.FadeIn, source.FadeOut);
        foreach (var effect in source.Effects)
        {
            copy.Effects.Add(new Effect
            {
                EffectTypeId = effect.EffectTypeId,
                DisplayName = effect.DisplayName,
                IsEnabled = effect.IsEnabled,
                Parameters = new Dictionary<string, object?>(effect.Parameters)
            });
        }
        return copy;
    }
}
