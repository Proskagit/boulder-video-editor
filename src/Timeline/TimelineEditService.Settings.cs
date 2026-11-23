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

/// <summary>Clip properties, the frame rate, the canvas, the project and export settings, the training clock and the recording zero (split out, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineEditService
{
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

        // D064 §1: a video clip's sound-only fades are whole frames up to the clip's length, as D025's.
        var audio = change.Audio;
        if (audio is { } sound && clip is VideoClip soundClip &&
            (sound.SoundFadeIn != soundClip.SoundFadeIn || sound.SoundFadeOut != soundClip.SoundFadeOut))
        {
            if (NormalizeFades(clip, new FadeProperties(sound.SoundFadeIn, sound.SoundFadeOut), plan.Rate, out var soundFades,
                    new FadeProperties(soundClip.SoundFadeIn, soundClip.SoundFadeOut)) is { } soundFadeError)
                return TimelineEditResult.Fail(soundFadeError);
            audio = sound with { SoundFadeIn = soundFades.FadeIn, SoundFadeOut = soundFades.FadeOut };
        }

        // Timing is untouched, so no timeline validation is needed: the clip's placement,
        // grid alignment and source range stay exactly as they are.
        var before = ClipPropertyValues.Capture(clip);
        var after = new ClipPropertyValues(
            change.Visual ?? before.Visual,
            audio ?? before.Audio,
            change.Text ?? before.Text,
            fade ?? before.Fade,
            change.Template ?? before.Template,
            change.Keyframes ?? before.Keyframes);

        // D064 amendment 1: a video clip's own keyframes and its volume points are one set of keyframes.
        var followAfter = (clip as VideoClip)?.Follow;
        if (clip is VideoClip synced && HasOwnSound(synced)) (after, followAfter) = KeyframeVolumeSync.Sync(before, after, synced.Follow, synced.Follow);

        // D053: Top / Attempt chosen where the status moment gives no lilac is put where the layout puts it.
        if (clip is TemplateClip placed && after.Template is { } wanted && before.Template is { } had)
            after = after with { Template = PlaceStatusMoment(placed, had, wanted) };
        var fields = ClipPropertyValues.Diff(before, after);
        if (fields == ClipPropertyFields.None) return TimelineEditResult.Unchanged();
        // D048 §3.4 / §4.2: an anchored template's status never overlaps its clip's plate or hold animation, and is never
        // set where no frame is left to show it.
        if (clip is TemplateClip template && after.Template is { } templateFields && AnchoredFieldsConflict(template, templateFields, fields) is { } overlap)
            return TimelineEditResult.Fail(overlap);

        var command = new SetClipPropertiesCommand(clip, before, after, fields, change.Gesture);
        // Amendment 1: a point inside a followed part is that part's keyframe — the sound and the Follow in one step.
        IUndoableCommand step = clip is VideoClip followed && !Equals(followed.Follow, followAfter)
            ? new SetSoundAndFollowCommand(command, new Commands.SetClipFollowCommand(followed, followed.Follow, followAfter, change.Gesture))
            : command;
        ExecuteTracked(new NotifyingCommand(step, _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok(new[] { clip.Id });
    }

    // --- Frame rate (D028, Step 13.5) ---------------------------------------------------------------------------------

    public TimelineEditResult SetFrameRate(FrameRate rate)
    {
        if (RateError(rate) is { } rateError) return TimelineEditResult.Fail(rateError);
        if (!ChangesRate(rate)) return TimelineEditResult.Unchanged();
        if (PlanFrameRate(rate, out var plan, out var command) is { } error) return TimelineEditResult.Fail(error);

        ExecuteTracked(new NotifyingCommand(command, NotifyRateChanged));
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

        ExecuteTracked(new NotifyingCommand(new CompositeCommand("Set Frame Size", commands), _projectService.NotifyTimelineChanged));
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
                // D063: the keyframes' positions scale with the static ones (their scale is relative to the canvas already).
                var keyframes = KeyframeValues.Of(clip);
                var scaledKeys = keyframes?.Keys is { } keys
                    ? new KeyframeValues(new TransformKeyframes(keys.Keys.Select(k => k with { X = k.X * scale, Y = k.Y * scale }).ToImmutableArray()))
                    : keyframes;
                var change = new ClipPropertyChange
                {
                    Visual = visual with { PositionX = visual.PositionX * scale, PositionY = visual.PositionY * scale },
                    // D075 amendment 1: the block's width is canvas pixels too — scaled with the font, so the lines wrap the same.
                    Text = text is { } t ? t with { FontSize = t.FontSize * scale, BoxWidth = t.BoxWidth * scale } : null,
                    Keyframes = scaledKeys
                };
                if (ClipPropertyValidator.Validate(clip, change) is { } error)
                    return string.Format(Messages.Edit_CantChangeSize, width, height, Describe(clip), track.Name, error);

                var before = new ClipPropertyValues(visual, null, text, Keyframes: keyframes);
                var after = new ClipPropertyValues(change.Visual, null, change.Text, Keyframes: scaledKeys);
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

    public TimelineEditResult SetTrainingClockCorrection(MediaTime correction)
    {
        if (_projectService.Current.Training is not { } training) return TimelineEditResult.Fail(Messages.Training_None);
        if (training.ClockCorrection == correction) return TimelineEditResult.Unchanged();
        ExecuteTracked(new NotifyingCommand(
            new SetTrainingClockCorrectionCommand(training, training.ClockCorrection, correction), _projectService.NotifyTimelineChanged));
        return TimelineEditResult.Ok();
    }

    public TimelineEditResult SetRecordingZeroOverride(Guid mediaAssetId, DateTimeOffset? zero)
    {
        var asset = _projectService.Current.MediaAssets.FirstOrDefault(a => a.Id == mediaAssetId);
        if (asset is not { Kind: MediaKind.Video }) return TimelineEditResult.Fail(Messages.Training_NotAVideo);
        var utc = zero?.ToUniversalTime();
        if (asset.RecordingZeroOverrideUtc == utc) return TimelineEditResult.Unchanged();
        IUndoableCommand command = new SetRecordingZeroOverrideCommand(asset, asset.RecordingZeroOverrideUtc, utc);
        // D051 §5.5 (B3): the proposals of the new Z are part of the same step.
        var proposals = ProposalsForZero(asset, utc);
        // D056 §2: the FIT's areas of this video the user has not changed are laid out again by the new time, same step.
        var relayout = RelayoutFitAreas(asset, utc, out var moved, out var kept);
        var parts = new List<IUndoableCommand> { command };
        if (proposals is not null) parts.Add(proposals);
        if (relayout is not null) parts.Add(relayout);
        if (parts.Count > 1) command = new CompositeCommand(command.Description, parts);
        ExecuteTracked(new NotifyingCommand(command, _projectService.NotifyTimelineChanged));
        var notes = new List<string>();
        if (moved > 0) notes.Add(string.Format(Messages.FitRoutes_LaidOutAgain, moved));
        if (kept > 0) notes.Add(string.Format(Messages.FitRoutes_KeptByHand, kept));
        // D054 (D053 §7, mode B): the clips of this video are bound to the FIT by the new Z — a step of its own; a climb that
        // has an area already (laid out again above, or changed by hand) never gets a second one.
        if (utc is not null && Project.Training is { IsUsable: true } && BindFitClimbs(null, asset.Id) is { Success: true, NoChange: false } created)
            return TimelineEditResult.Ok(created.ClipIds, string.Join(" ", notes.Append(created.Message ?? "").Where(n => n.Length > 0)));
        return notes.Count > 0 ? TimelineEditResult.Ok(Array.Empty<Guid>(), string.Join(" ", notes)) : TimelineEditResult.Ok();
    }

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

        ExecuteTracked(new NotifyingCommand(new CompositeCommand("Change Project Settings", commands),
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
        ExecuteTracked(new SetExportEncodingCommand(Settings, Settings.Export, export));
        return TimelineEditResult.Ok(null, ExportMessage(export));
    }

    /// <summary>D076: «Параметры экспорта: пресет «Максимальный».» for a built-in preset; otherwise the settings in short —
    /// «HEVC · NVENC · MP4 · Максимальное · slow · AAC 320».</summary>
    private string ExportMessage(ExportEncoding e) => Core.Export.ExportPresets.Match(e, new Core.Composition.FrameSize(Settings.FrameWidth, Settings.FrameHeight)) is { } kind
        ? string.Format(Messages.Settings_ExportProfileSet, kind switch
        {
            Core.Export.ExportPresetKind.Fast => Messages.ExportPreset_Fast,
            Core.Export.ExportPresetKind.Medium => Messages.ExportPreset_Medium,
            Core.Export.ExportPresetKind.High => Messages.ExportPreset_High,
            _ => Messages.ExportPreset_Maximum
        })
        : string.Format(Messages.Settings_ExportSet, string.Join(" · ", new[]
        {
            e.Codec switch { ExportVideoCodec.Hevc => "HEVC", ExportVideoCodec.Av1 => "AV1", _ => "H.264" },
            e.Encoder == ExportVideoEncoder.Nvenc ? "NVENC" : "CPU",
            e.Container.ToString().ToUpperInvariant(),
            e.RateControl == ExportRateControl.Bitrate ? $"{e.VideoBitrateMbps} Mbit/s" : e.Quality switch
            {
                ExportQuality.Maximum => Messages.ExportQuality_Maximum,
                ExportQuality.VeryHigh => Messages.ExportQuality_VeryHigh,
                ExportQuality.High => Messages.ExportQuality_High,
                ExportQuality.Standard => Messages.ExportQuality_Standard,
                ExportQuality.Compact => Messages.ExportQuality_Compact,
                _ => e.Quality.ToString()
            },
            e.PresetName, $"AAC {e.AudioBitrateKbps}"
        }));

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
    private static string? NormalizeFades(Clip clip, FadeProperties requested, FrameRate rate, out FadeProperties normalized,
        FadeProperties? current = null)
    {
        var stored = current ?? FadeProperties.Of(clip);   // D064: a video clip's sound-only fades follow the same rule
        normalized = stored;
        if (requested.FadeIn < MediaTime.Zero || requested.FadeOut < MediaTime.Zero) return Messages.Edit_FadeNegative;

        var frames = TransitionRules.ClipFrames(clip, rate);
        var fadeIn = requested.FadeIn == stored.FadeIn ? stored.FadeIn : Normalize(requested.FadeIn);
        var fadeOut = requested.FadeOut == stored.FadeOut ? stored.FadeOut : Normalize(requested.FadeOut);
        if (fadeIn != stored.FadeIn && TransitionRules.Frames(fadeIn, rate) > frames) return Messages.Edit_FadeInTooLong;
        if (fadeOut != stored.FadeOut && TransitionRules.Frames(fadeOut, rate) > frames) return Messages.Edit_FadeOutTooLong;

        normalized = new FadeProperties(fadeIn, fadeOut);
        return null;

        MediaTime Normalize(MediaTime value) => MediaTime.FromFrame(TransitionRules.Frames(value, rate), rate);
    }
}
