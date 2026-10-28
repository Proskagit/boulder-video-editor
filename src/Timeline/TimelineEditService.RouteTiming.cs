using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Commands;

namespace AiVideoEditor.Timeline;

// --- Route timing (D048 stage 2) --------------------------------------------------------------------------------------
// The standard layout (§3.4) and the overlay rules that follow a video clip's edits (§3.5, §3.6, §3.8): a «Боулдер»
// anchored to the clip (TemplateClip.AnchorClipId) and the clip's own Duration-mode hold animation. Everything is planned
// in the same EditPlan as the edit itself, so one Undo restores the clip with its overlays. In timeline frames: the clip
// [Vs, Ve), the green [Ps, S), the orange [As, Ae) (Ae = T), the lilac [S, Pe).
public sealed partial class TimelineEditService
{
    private static readonly MediaTime LayoutIntro = MediaTime.FromSeconds(7);
    private static readonly MediaTime LayoutFinal = MediaTime.FromSeconds(5);

    /// <summary>The shortest lilac that shows Top / Attempt (§1.25): the status draws nothing on its moment's frame (D045
    /// §17), so a manual status needs this many timeline frames from <c>S</c> to the template's end.</summary>
    private const long MinStatusFrames = 2;

    /// <summary>
    /// "Lay out intro and final" (D048 §3.4): from <paramref name="t"/> (<c>T ∈ [Vs, Ve]</c>) on a video clip with holds —
    /// the hold animation in the Duration mode on <c>[T − Fa, T)</c> and an anchored «Боулдер» from <c>T − I</c> to the clip's
    /// end with its status moment <c>S = max(Ve − 5 s, T, Ps + 1 frame)</c> (no status when <c>Ve − S</c> is under two
    /// frames, §1.25), where
    /// <c>I = min(7 s, A)</c>, <c>Fa = ⌊(10·I + 7) / 14⌋</c>. One Undo step; an existing Duration animation and anchored
    /// «Боулдер» are updated, never duplicated. With no time before <c>T</c> no animation is made and the result says so.
    /// </summary>
    public TimelineEditResult LayOutIntroAndFinal(Guid videoClipId, MediaTime t)
    {
        if (MarkupClip(videoClipId, out var video) is { } clipError) return TimelineEditResult.Fail(clipError);
        if (video.HoldMarkup is not { Holds.Length: > 0 } markup) return TimelineEditResult.Fail(Messages.Layout_NoHolds);

        var plan = new EditPlan(Sequence, Settings);
        var rate = plan.Rate;
        long vs = video.TimelineStart.ToNearestFrame(rate), ve = video.TimelineEnd.ToNearestFrame(rate);
        var tf = t.ToNearestFrame(rate);
        if (tf < vs || tf > ve) return TimelineEditResult.Fail(Messages.Layout_OutsideClip);

        var available = tf - vs;
        var intro = Math.Min(LayoutIntro.ToNearestFrame(rate), available);
        var animationFrames = (10 * intro + 7) / 14;
        var ps = tf - intro;
        var s = Math.Max(Math.Max(ve - LayoutFinal.ToNearestFrame(rate), tf), ps + 1);

        // The animation: [T − Fa, T) in the Duration mode (its order kept when it already was one).
        if (animationFrames >= 1)
        {
            var state = ClipState.Capture(video);
            var order = markup.Animation is { Mode: HoldAnimationMode.Duration } current ? current.Order : HoldAnimationOrder.BottomToTop;
            var animation = new HoldAnimation
            {
                Mode = HoldAnimationMode.Duration, Order = order, Duration = MediaTime.FromFrame(animationFrames, rate),
                StartSourceTime = HoldAnimationRule.SourceTimeAt(state.Start, state.SourceIn, state.Speed, tf - animationFrames, rate)
            };
            if (HoldMarkupRules.ValidateAnimation(animation) is { } invalid) return TimelineEditResult.Fail(invalid);
            plan.SetMarkup(video, markup with { Animation = animation });
        }

        // The anchored «Боулдер»: updated when there is one, otherwise new on a track above the clip.
        IUndoableCommand? addTrack = null;
        var template = AnchoredTemplates(plan, video).FirstOrDefault(p => p.Kind == TemplateKind.Boulder);
        var timing = ClipState.FromFrames(ps, ve, MediaTime.Zero, rate);
        if (template is not null)
        {
            var track = plan.TrackOf(template);
            if (track.IsLocked) return TimelineEditResult.Fail(string.Format(Messages.Edit_TrackLocked, track.Name));
            plan.Update(template, track, timing);
            plan.Carried.Add(template);
            var fields = plan.TemplateFieldsOf(template);
            plan.SetTemplateFields(template, fields with
            {
                StatusAt = StatusAtFor(template, s - ps, rate),
                Status = ve - s < MinStatusFrames ? BoulderStatus.None : fields.Status
            });
        }
        else
        {
            var defaults = TemplateRules.Defaults(TemplateKind.Boulder);
            template = new TemplateClip
            {
                Kind = TemplateKind.Boulder, Date = defaults.Date, Route = defaults.Route, Grade = defaults.Grade,
                Status = ve - s < MinStatusFrames ? BoulderStatus.None : defaults.Status, AccentHex = defaults.AccentHex, Title = defaults.Title,
                Subtitle = defaults.Subtitle, StatusAt = MediaTime.FromFrame(s - ps, rate), AnchorClipId = video.Id
            };
            timing.ApplyTo(template);
            var (fadeIn, fadeOut) = TemplateRules.Animation(TemplateKind.Boulder);
            var frames = ve - ps;
            template.FadeIn = MediaTime.FromFrame(Math.Clamp(MediaTime.FromSeconds(fadeIn).ToNearestFrame(rate), 0, frames), rate);
            template.FadeOut = MediaTime.FromFrame(Math.Clamp(MediaTime.FromSeconds(fadeOut).ToNearestFrame(rate), 0, frames), rate);

            var videoTrack = plan.TrackOf(video);
            var above = Sequence.VideoTracks.Where(tr => tr.Order > videoTrack.Order && !tr.IsLocked)
                .OrderBy(tr => tr.Order)
                .FirstOrDefault(tr => tr.Clips.All(c => c.TimelineEnd.ToNearestFrame(rate) <= ps || c.TimelineStart.ToNearestFrame(rate) >= ve));
            if (above is null)
            {
                above = NewTrack(TrackType.Video);
                addTrack = new AddTrackCommand(Sequence, above);
            }
            plan.Insert(above, template);
        }

        if (Validate(plan) is { } error) return TimelineEditResult.Fail(string.Format(Messages.Edit_CantAddTemplate, plan.TrackOf(video).Name, error));

        IUndoableCommand command = plan.BuildCommand("Lay Out Intro and Final");
        if (addTrack is not null) command = new CompositeCommand("Lay Out Intro and Final", new[] { addTrack, command });
        _undoRedo.Execute(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { video.Id, template.Id }, animationFrames >= 1 ? null : Messages.Layout_NoAnimationTime);
    }

    // --- The overlay rules of an edit ----------------------------------------------------------------------------------

    /// <summary>The templates anchored to <paramref name="video"/> (D048 §3.5 — each one handled separately).</summary>
    private static List<TemplateClip> AnchoredTemplates(EditPlan plan, VideoClip video) =>
        plan.AllTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Where(p => plan.AnchorOf(p) == video.Id).ToList();

    /// <summary>The frames <c>[As, Ae)</c> of a Duration-mode animation of a clip at <paramref name="state"/>, or null.</summary>
    private static (long Start, long End)? OrangeOf(HoldMarkup? markup, ClipState state, FrameRate rate)
    {
        if (markup?.Animation is not { Mode: HoldAnimationMode.Duration } animation) return null;
        var start = HoldAnimationRule.StartFrame(state.Start, state.SourceIn, state.Speed, animation.StartSourceTime, rate);
        return (start, start + HoldAnimationRule.DurationFrames(animation, rate));
    }

    /// <summary>
    /// Plans what an edit does to the overlays of every video clip it changes (D048): a moved clip carries its anchored
    /// templates (§3.5); a removed one takes them along; a speed change scales them and the Duration animation (§3.8); a
    /// trim that cuts into a segment applies the protective rules (§3.8) — the segments it doesn't touch stay. A split is
    /// planned by its own rule (<see cref="PlanSplitOverlays"/>); a slip touches nothing. Idempotent (computed from the
    /// model's state), so it may run more than once for one plan. Then checks the templates the user edits directly
    /// (§4.2): inside their clip, no overlap of the lilac.
    /// </summary>
    private string? ReconcileOverlays(EditPlan plan)
    {
        if (plan.NewFrameRate is not null) return null;
        var rate = plan.Rate;
        foreach (var video in plan.AllTracks.SelectMany(t => t.Clips).OfType<VideoClip>().ToList())
        {
            if (plan.IsSplitLeft(video)) continue;
            var templates = AnchoredTemplates(plan, video);
            if (templates.Count == 0 && OrangeOf(video.HoldMarkup, ClipState.Capture(video), rate) is null) continue;

            if (plan.Removes(video))
            {
                foreach (var template in templates.Where(p => !plan.Removes(p))) plan.Remove(template);
                continue;
            }
            if (plan.PlannedState(video) is not { } after) continue;

            var before = ClipState.Capture(video);
            if (after.Speed != before.Speed) PlanScaledOverlays(plan, video, templates, before, after);
            else PlanBoundsOverlays(plan, video, templates, before, after);
        }
        return CheckAnchoredEdits(plan);
    }

    /// <summary>§3.8, a boundary change at the same speed: the picture's shift moves the overlays (a move, a ripple), then
    /// the trim's protective rules for what the new bounds cut. A slip (same bounds, other source) touches nothing.</summary>
    private void PlanBoundsOverlays(EditPlan plan, VideoClip video, List<TemplateClip> templates, ClipState before, ClipState after)
    {
        var rate = plan.Rate;
        long vs = before.Start.ToNearestFrame(rate), ve = before.End.ToNearestFrame(rate);
        long vs2 = after.Start.ToNearestFrame(rate), ve2 = after.End.ToNearestFrame(rate);
        if (vs == vs2 && ve == ve2) return;   // unchanged bounds (a slip): nothing

        // Where the picture's first source moment lands after the edit: the shift of everything over the picture.
        var shift = HoldAnimationRule.StartFrame(after.Start, after.SourceIn, after.Speed, before.SourceIn, rate) - vs;
        if (ve2 - vs2 == ve - vs && after.SourceIn == before.SourceIn) shift = vs2 - vs;   // a move

        // The Duration animation: source-anchored, so the shift is its own; only the cuts change it.
        var markup = video.HoldMarkup;
        var orange = OrangeOf(markup, after, rate);
        if (orange is { } o && markup is not null)
        {
            if (ve2 < o.End || vs2 >= o.End) { plan.SetMarkup(video, null); orange = null; }   // §3.9: the whole markup
            else if (vs2 > o.Start)
            {
                // Head trim into the orange: the rest [Vs', Ae), its 80 / 20 re-timed on F' (proportional), T kept.
                var animation = markup.Animation! with
                {
                    StartSourceTime = HoldAnimationRule.SourceTimeAt(after.Start, after.SourceIn, after.Speed, vs2, rate),
                    Duration = MediaTime.FromFrame(o.End - vs2, rate)
                };
                plan.SetMarkup(video, markup with { Animation = animation });
                orange = (vs2, o.End);
            }
        }

        foreach (var template in templates)
        {
            if (plan.Changes(template) && !plan.Carried.Contains(template)) continue;   // edited by the user itself
            var state = ClipState.Capture(template);
            var fields = TemplateProperties.Of(template)!.Value;
            long ps = state.Start.ToNearestFrame(rate) + shift, pe = state.End.ToNearestFrame(rate) + shift;
            var s = ps + StatusFrames(template, fields, rate);
            var lilac = fields.Status != BoulderStatus.None && s < pe;
            var status = fields.Status;

            // No frame of the template left in the clip → deleted for this clip (§3.8); the markup by its own rule.
            if (vs2 >= pe || ve2 <= ps) { plan.Remove(template); continue; }

            // Head: the cut part of the green goes (one frame may remain); reaching the lilac deletes only the lilac.
            if (vs2 > ps)
            {
                if (lilac && vs2 >= s) { status = BoulderStatus.None; lilac = false; }
                ps = vs2;
            }

            // Tail: the lilac keeps its length and shifts left with the end; a conflict deletes only the lilac.
            if (ve2 < pe)
            {
                if (lilac && ve2 >= s)
                {
                    var shifted = s - (pe - ve2);
                    var conflict = shifted < ps + 1 || (orange is { } kept && shifted < kept.End);
                    if (conflict) status = BoulderStatus.None;
                    else s = shifted;
                }
                else if (lilac) status = BoulderStatus.None;   // the new end before the lilac's start
                pe = ve2;
            }

            if (ps != state.Start.ToNearestFrame(rate) || pe != state.End.ToNearestFrame(rate))
            {
                var timing = ClipState.FromFrames(ps, pe, MediaTime.Zero, rate);
                plan.Update(template, plan.TrackOf(template), timing);
                plan.Carried.Add(template);
            }
            var statusAt = fields.Status != BoulderStatus.None ? StatusAtFor(template, Math.Max(0, s - ps), rate) : fields.StatusAt;
            if (status != fields.Status || statusAt != fields.StatusAt)
                plan.SetTemplateFields(template, fields with { Status = status, StatusAt = status == BoulderStatus.None ? fields.StatusAt : statusAt });
        }
    }

    /// <summary>§3.8, a speed change: every linked boundary <c>x' = Vs + ⌊(2·(x − Vs)·D' + D) / (2·D)⌋</c>; a green below one
    /// frame kept at one frame, a too short orange deletes the markup, a lilac under two frames is deleted (§1.25).</summary>
    private void PlanScaledOverlays(EditPlan plan, VideoClip video, List<TemplateClip> templates, ClipState before, ClipState after)
    {
        var rate = plan.Rate;
        long vs = before.Start.ToNearestFrame(rate), d = before.End.ToNearestFrame(rate) - vs;
        long d2 = after.End.ToNearestFrame(rate) - after.Start.ToNearestFrame(rate);
        if (d <= 0) return;
        long Scale(long x) => vs + (long)(((Int128)2 * (x - vs) * d2 + d) / (2 * d));

        var markup = video.HoldMarkup;
        if (OrangeOf(markup, before, rate) is { } o && markup is not null)
        {
            var end = Scale(o.End);
            var start = HoldAnimationRule.StartFrame(after.Start, after.SourceIn, after.Speed, markup.Animation!.StartSourceTime, rate);
            plan.SetMarkup(video, end - start < 1 ? null : markup with { Animation = markup.Animation with { Duration = MediaTime.FromFrame(end - start, rate) } });
        }

        foreach (var template in templates)
        {
            if (plan.Changes(template) && !plan.Carried.Contains(template)) continue;
            var state = ClipState.Capture(template);
            var fields = TemplateProperties.Of(template)!.Value;
            long ps = state.Start.ToNearestFrame(rate), pe = state.End.ToNearestFrame(rate);
            var s = ps + StatusFrames(template, fields, rate);
            var lilac = fields.Status != BoulderStatus.None && s < pe;
            long ps2 = Scale(ps), pe2 = Scale(pe), s2 = Scale(s);
            var status = fields.Status;
            if (lilac)
            {
                if (s2 - ps2 < 1)
                {
                    ps2 = s2 - 1;
                    if (ps2 < vs) { ps2 = vs; s2 = vs + 1; }
                }
                // §1.25: a lilac under two frames would show no status — deleted.
                if (pe2 - s2 < MinStatusFrames) { status = BoulderStatus.None; pe2 = Math.Max(s2, ps2 + 1); }
            }
            else if (pe2 - ps2 < 1) pe2 = ps2 + 1;

            plan.Update(template, plan.TrackOf(template), ClipState.FromFrames(ps2, pe2, MediaTime.Zero, rate));
            plan.Carried.Add(template);
            var statusAt = lilac && status != BoulderStatus.None ? StatusAtFor(template, s2 - ps2, rate) : fields.StatusAt;
            if (status != fields.Status || statusAt != fields.StatusAt)
                plan.SetTemplateFields(template, fields with { Status = status, StatusAt = statusAt });
        }
    }

    /// <summary>D048 §3.6 for one split clip: a Duration animation's markup goes only to the part that contains its start —
    /// a cut inside it deletes the whole markup. Interval-mode markups keep D034 §6's copy.</summary>
    private static void PlanSplitOverlays(EditPlan plan, Clip clip, ClipState state, Clip rightClip, long atFrame)
    {
        plan.MarkSplitLeft(clip);
        if (clip is not VideoClip video || rightClip is not VideoClip right) return;
        if (OrangeOf(video.HoldMarkup, state, plan.Rate) is not { } orange) return;
        if (atFrame <= orange.Start) plan.SetMarkup(video, null);                                   // all of it on the right
        else if (atFrame < orange.End) { plan.SetMarkup(video, null); right.HoldMarkup = null; }    // inside: deleted
        else right.HoldMarkup = null;                                                               // all of it on the left
    }

    /// <summary>
    /// D048 §3.5 / §3.6 / §3.8 for Insert / Overwrite (D031 SQ6 / SQ9): a video clip they cut at <paramref name="cut"/> is a
    /// split (§3.6) whose right part is then moved by <paramref name="shift"/> frames (Insert — the move rule: its overlays
    /// go along) or head-trimmed to <paramref name="trimTo"/> (Overwrite — the head-trim rules: the cut part of the green
    /// goes, the orange shortened or the markup deleted, reaching the lilac deletes it, no frame left deletes the
    /// template). Templates spanning the cut are split with it (the left part anchored to the left clip, the right part to
    /// the right one), templates wholly after it re-anchored to the right part; a lilac left under two frames without a
    /// continuation loses its status (§1.25). Run after the placement's own plan, before <see cref="Validate"/>.
    /// </summary>
    private string? PlanPlacedOverlays(EditPlan plan, IReadOnlyList<(Clip Left, Clip Right)> splits, long cut, long shift, long? trimTo)
    {
        var rate = plan.Rate;
        var templateSplits = splits.Where(s => s.Left is TemplateClip).ToList();
        foreach (var (left, right) in splits.ToList())
        {
            if (left is not VideoClip video || right is not VideoClip rightVideo) continue;
            if (trimTo is not null) TrimPlacedMarkup(rightVideo, rate);

            var anchored = plan.AllTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Where(p => plan.AnchorOf(p) == video.Id).ToList();
            foreach (var template in anchored)
            {
                // A template on the placement's own track was split / moved / trimmed by D031's rules: only its anchor.
                if (plan.Changes(template) || templateSplits.Any(s => s.Left == template))
                {
                    if (templateSplits.FirstOrDefault(s => s.Left == template).Right is TemplateClip part) part.AnchorClipId = rightVideo.Id;
                    else if (!plan.Removes(template) && plan.PlannedState(template) is { } moved && moved.Start.ToNearestFrame(rate) >= cut)
                        plan.SetAnchor(template, rightVideo.Id);
                    continue;
                }

                var track = plan.TrackOf(template);
                var fields = TemplateProperties.Of(template)!.Value;
                long ps = template.TimelineStart.ToNearestFrame(rate), pe = template.TimelineEnd.ToNearestFrame(rate);
                if (pe <= cut) continue;   // wholly on the left part: stays
                if (track.IsLocked) return string.Format(Messages.Edit_TrackLocked, track.Name);
                var s = ps + StatusFrames(template, fields, rate);
                var status = fields.Status != BoulderStatus.None;

                // The right portion where the placement leaves it.
                long rs = Math.Max(ps, cut), re = pe;
                if (trimTo is { } to) rs = Math.Max(rs, to);
                else { rs += shift; re += shift; }
                var hasRight = rs < re;
                var sRight = trimTo is null ? s + shift : s;
                var rightStatus = status && hasRight && (trimTo is null || sRight > rs) && re - sRight >= MinStatusFrames;

                if (ps < cut)
                {
                    if (hasRight)
                    {
                        if (!PlanSplitClip(plan, track, template, ClipState.Capture(template), cut, out var part))
                            return Tr(track, ZoneAt(track, template, ClipState.Capture(template), cut, rate)?.TransitionTypeId,
                                Messages.Split_InsideDissolve, Messages.Split_InsideCrossfade, Messages.Split_InsideNamed);
                        var rightTemplate = (TemplateClip)part;
                        ClipState.FromFrames(rs, re, MediaTime.Zero, rate).ApplyTo(rightTemplate);
                        if (trimTo is not null) rightTemplate.CounterStart += MediaTime.FromFrame(rs - cut, rate);   // the clock goes on
                        rightTemplate.AnchorClipId = rightVideo.Id;
                        if (status && !rightStatus) rightTemplate.Status = BoulderStatus.None;
                        // The left part keeps its lilac only when it continues on the right or is two frames long itself.
                        if (status && !rightStatus && cut - s < MinStatusFrames)
                            plan.SetTemplateFields(template, fields with { Status = BoulderStatus.None });
                    }
                    else
                    {
                        // Nothing of it after the overwritten range: its tail at the cut (a tail trim).
                        plan.Update(template, track, ClipState.FromFrames(ps, cut, MediaTime.Zero, rate));
                        plan.Carried.Add(template);
                        if (status && cut - s < MinStatusFrames) plan.SetTemplateFields(template, fields with { Status = BoulderStatus.None });
                    }
                    continue;
                }

                if (!hasRight) { plan.Remove(template); continue; }   // inside the overwritten range: no frame left
                plan.SetAnchor(template, rightVideo.Id);
                if (rs != ps || re != pe)
                {
                    plan.Update(template, track, ClipState.FromFrames(rs, re, MediaTime.Zero, rate));
                    plan.Carried.Add(template);
                }
                if (trimTo is not null && status)
                    plan.SetTemplateFields(template, rightStatus
                        ? fields with { StatusAt = StatusAtFor(template, s - rs, rate) }   // the moment stays on the picture
                        : fields with { Status = BoulderStatus.None });
            }
        }
        return null;
    }

    /// <summary>§3.8's head-trim rule for the markup of an Overwrite's right part (a planned clip, already at its place): the
    /// orange wholly cut deletes the markup; cut into, the rest is kept from the new start (its 80 / 20 re-timed), T kept.</summary>
    private static void TrimPlacedMarkup(VideoClip right, FrameRate rate)
    {
        var state = ClipState.Capture(right);
        if (OrangeOf(right.HoldMarkup, state, rate) is not { } orange) return;
        var start = state.Start.ToNearestFrame(rate);
        if (start >= orange.End) { right.HoldMarkup = null; return; }
        if (start <= orange.Start) return;
        right.HoldMarkup = right.HoldMarkup! with
        {
            Animation = right.HoldMarkup.Animation! with
            {
                StartSourceTime = HoldAnimationRule.SourceTimeAt(state.Start, state.SourceIn, state.Speed, start, rate),
                Duration = MediaTime.FromFrame(orange.End - start, rate)
            }
        };
    }

    /// <summary>§3.6: the templates anchored to the split clips — spanning the cut (split with them) or wholly on its right
    /// (anchored to the right part). The templates to split as well are returned.</summary>
    private static IEnumerable<Clip> AnchoredSpanning(EditPlan plan, IEnumerable<Clip> splitClips, long atFrame)
    {
        var rate = plan.Rate;
        var split = splitClips.ToHashSet();
        foreach (var video in split.OfType<VideoClip>().ToList())
            foreach (var template in AnchoredTemplates(plan, video))
                if (!split.Contains(template) && !plan.TrackOf(template).IsLocked &&
                    template.TimelineStart.ToNearestFrame(rate) < atFrame && atFrame < template.TimelineEnd.ToNearestFrame(rate))
                    yield return template;
    }

    /// <summary>§3.6 after the split: each template's parts anchored to the video part they lie over.</summary>
    private static void AnchorSplitParts(EditPlan plan, IReadOnlyList<(Clip Left, Clip Right)> splits, long atFrame)
    {
        var rate = plan.Rate;
        var rightOf = splits.Where(s => s.Left is VideoClip).ToDictionary(s => s.Left.Id, s => s.Right.Id);
        foreach (var (left, right) in splits)
            if (left is TemplateClip { AnchorClipId: { } anchor } && rightOf.TryGetValue(anchor, out var rightVideo))
                ((TemplateClip)right).AnchorClipId = rightVideo;
        foreach (var template in plan.AllTracks.SelectMany(t => t.Clips).OfType<TemplateClip>())
            if (template.AnchorClipId is { } anchor && rightOf.TryGetValue(anchor, out var rightVideo) &&
                !splits.Any(s => s.Left == template) && template.TimelineStart.ToNearestFrame(rate) >= atFrame)
                plan.SetAnchor(template, rightVideo);
    }

    /// <summary>§4.2 / §1.24, the invariant of the model for the templates the user edits directly (moved, trimmed, their
    /// fields): an anchored template stays inside its clip, its lilac overlaps neither the green nor the orange, and a move
    /// or trim never shortens a lilac with Top / Attempt below two frames nor cuts it away (§1.25).</summary>
    private string? CheckAnchoredEdits(EditPlan plan)
    {
        var rate = plan.Rate;
        foreach (var (clip, _, after) in plan.Updates.ToList())
        {
            if (clip is not TemplateClip template || plan.Carried.Contains(template)) continue;
            if (plan.AnchorOf(template) is not { } anchor || FindVideo(plan, anchor) is not { } video || plan.Removes(video)) continue;
            var fields = plan.TemplateFieldsOf(template);
            // A split's left part is not a shortening: its lilac continues in the right part (§3.6).
            if (template.Kind == TemplateKind.Boulder && fields.Status != BoulderStatus.None && !plan.IsSplitLeft(template))
            {
                var length = LilacFrames(template, after, fields, rate);
                if (length < MinStatusFrames && length < LilacFrames(template, ClipState.Capture(template), fields, rate))
                    return Messages.Overlay_StatusTooShort;
            }
            if (OverlayConflict(template, after, fields, plan.PlannedState(video) ?? ClipState.Capture(video),
                    plan.MarkupOf(video), rate) is { } error)
                return error;
        }
        return null;
    }

    /// <summary>The lilac's length <c>Pe − S</c> in frames of a template at <paramref name="state"/> (≤ 0: none).</summary>
    private static long LilacFrames(TemplateClip template, ClipState state, TemplateProperties fields, FrameRate rate) =>
        state.End.ToNearestFrame(rate) - (state.Start.ToNearestFrame(rate) + StatusFrames(template, fields, rate));

    /// <summary>The status moment's offset from the template's start in frames — the composition's rule (D045 §17:
    /// <c>StatusAt − CounterStart</c> on the counter's clock, whole frames).</summary>
    private static long StatusFrames(TemplateClip template, TemplateProperties fields, FrameRate rate) =>
        fields.StatusAt.ToNearestFrame(rate) - template.CounterStart.ToNearestFrame(rate);

    /// <summary>The <see cref="TemplateClip.StatusAt"/> that puts the status <paramref name="offset"/> frames after the
    /// template's start.</summary>
    private static MediaTime StatusAtFor(TemplateClip template, long offset, FrameRate rate) =>
        MediaTime.FromFrame(template.CounterStart.ToNearestFrame(rate) + offset, rate);

    private static VideoClip? FindVideo(EditPlan plan, Guid id) =>
        plan.AllTracks.SelectMany(t => t.Clips).OfType<VideoClip>().FirstOrDefault(v => v.Id == id);

    /// <summary>Why an anchored template at <paramref name="state"/> with <paramref name="fields"/> breaks the invariant
    /// over its clip, or null: outside the clip; a lilac before one green frame or over the orange.</summary>
    private static string? OverlayConflict(TemplateClip template, ClipState state, TemplateProperties fields, ClipState video,
        HoldMarkup? markup, FrameRate rate)
    {
        long ps = state.Start.ToNearestFrame(rate), pe = state.End.ToNearestFrame(rate);
        if (ps < video.Start.ToNearestFrame(rate) || pe > video.End.ToNearestFrame(rate)) return Messages.Overlay_OutsideClip;
        if (template.Kind != TemplateKind.Boulder || fields.Status == BoulderStatus.None) return null;
        var s = ps + StatusFrames(template, fields, rate);
        if (s >= pe) return null;   // no lilac on the timeline
        if (s < ps + 1) return Messages.Overlay_LilacOverlap;
        if (OrangeOf(markup, video, rate) is { } orange && s < orange.End && orange.Start < pe) return Messages.Overlay_LilacOverlap;
        return null;
    }

    /// <summary>The invariant for a template's fields changed directly (the Inspector's status or its moment). Choosing Top /
    /// Attempt or moving its moment is refused when the lilac <c>[S, Pe)</c> would be shorter than
    /// <see cref="MinStatusFrames"/> (§1.25: e.g. <c>T = Ve</c> or <c>T = Ve − 1</c>); then the no-overlap limits of
    /// <see cref="OverlayConflict"/>.</summary>
    private string? AnchoredFieldsConflict(TemplateClip template, TemplateProperties fields, ClipPropertyFields changed)
    {
        if (template.AnchorClipId is not { } anchor) return null;
        var plan = new EditPlan(Sequence, Settings);
        if (FindVideo(plan, anchor) is not { } video) return null;
        var rate = plan.Rate;
        var state = ClipState.Capture(template);
        if (template.Kind == TemplateKind.Boulder && fields.Status != BoulderStatus.None &&
            (changed & (ClipPropertyFields.TemplateStatus | ClipPropertyFields.TemplateStatusAt)) != 0)
        {
            long ps = state.Start.ToNearestFrame(rate), pe = state.End.ToNearestFrame(rate);
            if (pe - (ps + StatusFrames(template, fields, rate)) < MinStatusFrames) return Messages.Overlay_StatusTooShort;
        }
        return OverlayConflict(template, state, fields, ClipState.Capture(video), video.HoldMarkup, rate);
    }

    /// <summary>A Duration animation changed directly (the Inspector's start / duration): its orange <c>[As, Ae)</c> must
    /// stay inside the clip — <c>Vs ≤ As</c>, <c>Ae ≤ Ve</c> on the resulting frame values (§3.4, §4.2). Null when it does
    /// or the animation is in the Interval mode (D036 may run past the clip's end).</summary>
    private string? OrangeOutsideClip(VideoClip video, HoldMarkup markup)
    {
        var rate = new EditPlan(Sequence, Settings).Rate;
        var state = ClipState.Capture(video);
        if (OrangeOf(markup, state, rate) is not { } orange) return null;
        return orange.Start < state.Start.ToNearestFrame(rate) || orange.End > state.End.ToNearestFrame(rate)
            ? Messages.Overlay_OrangeOutsideClip
            : null;
    }

    /// <summary>The invariant for a clip's animation changed directly (the Inspector's fields): no anchored lilac
    /// over the new orange.</summary>
    private string? AnchoredAnimationConflict(VideoClip video, HoldMarkup markup)
    {
        var plan = new EditPlan(Sequence, Settings);
        foreach (var template in AnchoredTemplates(plan, video))
            if (OverlayConflict(template, ClipState.Capture(template), TemplateProperties.Of(template)!.Value, ClipState.Capture(video),
                    markup, plan.Rate) is { } error)
                return error;
        return null;
    }
}
