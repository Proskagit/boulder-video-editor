using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 26 (D041): the canvas the first video offers (§1) — when the dialog is asked, what accepting, declining and
/// closing do, Undo / Redo, the session flag — and «Заполнить кадр» / «Вписать в кадр» (§2) from the timeline's context
/// menu and the Inspector.
/// </summary>
public sealed class VerticalVideoUiTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly SourceViewerService _source;
    private readonly ScriptedDialogs _dialogs;
    private readonly TimelineViewModel _timeline;

    public VerticalVideoUiTests() : this(new ScriptedDialogs(0)) { }

    private VerticalVideoUiTests(ScriptedDialogs dialogs)
    {
        _dialogs = dialogs;
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _source = new SourceViewerService(_projects, _edit);
        _timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock,
            dialogs: _dialogs, source: _source);
    }

    private static VerticalVideoUiTests With(params int?[] answers) => new(new ScriptedDialogs(answers));

    private (int, int) Canvas => (_projects.Current.Settings.FrameWidth, _projects.Current.Settings.FrameHeight);
    private IEnumerable<Clip> Clips => _projects.Current.Timeline.VideoTracks.Concat(_projects.Current.Timeline.AudioTracks).SelectMany(t => t.Clips);

    private MediaAsset Video(int displayWidth, int displayHeight, int? rotation = 0, string name = "vivo.mp4", bool sound = false)
    {
        var coded = rotation is 90 or 270 ? (displayHeight, displayWidth) : (displayWidth, displayHeight);
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-vertical", Guid.NewGuid().ToString("N"), name),
            Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = coded.Item1, Height = coded.Item2,
                DisplayRotation = rotation, DisplayWidth = displayWidth, DisplayHeight = displayHeight,
                AudioCodec = sound ? "aac" : null
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private async Task AddAsync(MediaAsset asset)
    {
        _timeline.AddMedia(asset);
        await _timeline.CanvasOffer;
    }

    // --- §1: when the offer is made ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_portrait_video_in_an_empty_project_offers_its_shape_and_accepting_changes_the_canvas()
    {
        await AddAsync(Video(1080, 1920, name: "боулдер.mp4"));

        var asked = Assert.Single(_dialogs.Asked);
        Assert.Equal(Strings.Canvas_OfferTitle, asked.Title);
        Assert.Equal(string.Format(Strings.Canvas_OfferMessage, "боулдер.mp4", 1080, 1920, 1920, 1080, 1080, 1920), asked.Message);
        Assert.Equal(new[] { Strings.Canvas_OfferAccept, Strings.Canvas_OfferKeep }, asked.Buttons);
        Assert.Equal((1080, 1920), Canvas);
        Assert.Equal(string.Format(Strings.Canvas_OfferApplied, 1080, 1920), _status.Message);
        Assert.Single(Clips);
    }

    [Fact]
    public async Task Undo_restores_the_canvas_first_then_removes_the_clip_and_Redo_replays_both()
    {
        await AddAsync(Video(2160, 3840));
        Assert.Equal((1080, 1920), Canvas);

        _undo.Undo();
        Assert.Equal((1920, 1080), Canvas);
        Assert.Single(Clips);
        _undo.Undo();
        Assert.Empty(Clips);

        _undo.Redo();
        Assert.Single(Clips);
        Assert.Equal((1920, 1080), Canvas);
        _undo.Redo();
        Assert.Equal((1080, 1920), Canvas);
        Assert.Single(_dialogs.Asked);                      // Undo / Redo never ask again
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public async Task A_rotated_phone_clip_is_judged_by_its_display_size(int rotation)
    {
        await AddAsync(Video(1080, 1920, rotation));
        Assert.Single(_dialogs.Asked);
        Assert.Equal((1080, 1920), Canvas);
    }

    [Fact]
    public async Task A_four_by_three_landscape_clip_offers_its_shape_too()
    {
        await AddAsync(Video(1440, 1080));
        Assert.Single(_dialogs.Asked);
        Assert.Equal((1440, 1080), Canvas);
    }

    [Fact]
    public async Task An_unknown_orientation_makes_no_offer()
    {
        await AddAsync(Video(1080, 1920, rotation: null));
        Assert.Empty(_dialogs.Asked);
        Assert.Equal((1920, 1080), Canvas);
    }

    [Fact]
    public async Task An_unknown_display_size_makes_no_offer()
    {
        var asset = Video(1080, 1920);
        asset.Metadata!.DisplayWidth = null;
        await AddAsync(asset);
        Assert.Empty(_dialogs.Asked);
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(1920, 1080)]
    public async Task A_landscape_16_by_9_clip_makes_no_offer(int w, int h)
    {
        await AddAsync(Video(w, h));
        Assert.Empty(_dialogs.Asked);
        Assert.Equal((1920, 1080), Canvas);
    }

    [Fact]
    public async Task A_timeline_with_a_clip_makes_no_offer()
    {
        await AddAsync(Video(1920, 1080));
        await AddAsync(Video(1080, 1920));
        Assert.Empty(_dialogs.Asked);
    }

    [Fact]
    public async Task A_canvas_changed_in_the_session_makes_no_offer_even_back_at_the_default()
    {
        Assert.True(_edit.SetCanvasSize(1280, 720).Success);
        Assert.True(_edit.SetCanvasSize(1920, 1080).Success);
        await AddAsync(Video(1080, 1920));
        Assert.Empty(_dialogs.Asked);
    }

    [Fact]
    public async Task A_canvas_other_than_the_default_makes_no_offer()
    {
        _projects.CreateNew("p", new ProjectSettings { FrameWidth = 1280, FrameHeight = 720 });
        await AddAsync(Video(1080, 1920));
        Assert.Empty(_dialogs.Asked);
    }

    // --- §1: declining, closing, the session --------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]       // «Оставить»
    [InlineData(null)]    // closed
    public async Task Declining_or_closing_changes_nothing_and_the_offer_is_not_made_again(int? answer)
    {
        var t = With(answer);
        await t.AddAsync(t.Video(1080, 1920));
        Assert.Single(t._dialogs.Asked);
        Assert.Equal((1920, 1080), t.Canvas);

        // The timeline emptied and a portrait clip added again: still the same session — no second offer.
        t._undo.Undo();
        Assert.Empty(t.Clips);
        await t.AddAsync(t.Video(1080, 1920));
        Assert.Single(t._dialogs.Asked);
    }

    [Fact]
    public async Task A_new_project_starts_a_new_session()
    {
        var t = With(1, 0);
        await t.AddAsync(t.Video(1080, 1920));
        t._projects.CreateNew("next");
        await t.AddAsync(t.Video(1080, 1920));

        Assert.Equal(2, t._dialogs.Asked.Count);
        Assert.Equal((1080, 1920), t.Canvas);
    }

    [Fact]
    public async Task An_export_started_while_the_dialog_is_open_keeps_the_canvas()
    {
        VerticalVideoUiTests? t = null;
        IDisposable? export = null;
        var dialogs = new ScriptedDialogs(0) { OnAsk = () => export = t!._lock.Acquire() };
        t = new VerticalVideoUiTests(dialogs);

        await t.AddAsync(t.Video(1080, 1920));

        Assert.Single(dialogs.Asked);
        Assert.NotNull(export);
        Assert.Equal((1920, 1080), t.Canvas);
        export!.Dispose();
    }

    // --- §1: Insert / Overwrite from the Source viewer ----------------------------------------------------------------

    [Fact]
    public async Task Insert_from_the_source_viewer_offers_the_canvas()
    {
        var asset = Video(1080, 1920);
        Assert.Null(_source.Open(asset));
        _timeline.InsertFromSourceCommand.Execute(null);
        await _timeline.CanvasOffer;

        Assert.Single(_dialogs.Asked);
        Assert.Equal((1080, 1920), Canvas);
    }

    [Fact]
    public async Task A_sound_only_insert_creates_no_picture_and_makes_no_offer()
    {
        var asset = Video(1080, 1920, sound: true);
        Assert.Null(_source.Open(asset));
        _source.SoundOnly = true;
        Assert.True(_source.PlacesSoundOnly);
        _timeline.InsertFromSourceCommand.Execute(null);
        await _timeline.CanvasOffer;

        Assert.Single(Clips);
        Assert.Empty(_dialogs.Asked);
        Assert.Equal((1920, 1080), Canvas);
    }

    // --- §2: the timeline's context menu ------------------------------------------------------------------------------

    internal TimelineClipViewModel ClipVm(Clip clip) => _timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);

    [Fact]
    public async Task The_context_menu_fills_and_fits_the_clicked_clip()
    {
        var t = With(1);                                      // keep the default canvas
        await t.AddAsync(t.Video(1080, 1920));
        var clip = (VideoClip)t.Clips.Single();
        var vm = t.ClipVm(clip);

        Assert.True(t._timeline.FillFrameCommand.CanExecute(vm));
        t._timeline.FillFrameCommand.Execute(vm);
        Assert.Equal(1920.0 * 1920 / (1080.0 * 1080), clip.Scale, 12);
        Assert.Equal(string.Format(Strings.Frame_Filled, (clip.Scale * 100).ToString("0.##")), t._status.Message);

        t._timeline.FitFrameCommand.Execute(vm);
        Assert.Equal(1.0, clip.Scale);
        Assert.Equal(string.Format(Strings.Frame_Fitted, "100"), t._status.Message);

        t._timeline.FitFrameCommand.Execute(vm);
        Assert.Equal(Messages.Frame_AlreadyFits, t._status.Message);

        t._undo.Undo();
        Assert.Equal(1920.0 * 1920 / (1080.0 * 1080), clip.Scale, 12);
    }

    [Fact]
    public async Task The_menu_commands_are_unavailable_for_text_and_audio_and_during_an_export()
    {
        var t = With(1);
        await t.AddAsync(t.Video(1080, 1920));
        Assert.True(t._edit.AddTextClip(MediaTime.FromSeconds(20)).Success);
        var video = t.ClipVm(t.Clips.OfType<VideoClip>().Single());
        var text = t.ClipVm(t.Clips.OfType<TextClip>().Single());

        Assert.False(t._timeline.FillFrameCommand.CanExecute(text));
        Assert.False(t._timeline.FitFrameCommand.CanExecute(text));
        Assert.False(t._timeline.FillFrameCommand.CanExecute(null));

        var canExecuteChanged = 0;
        t._timeline.FillFrameCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        using (t._lock.Acquire())
        {
            Assert.True(canExecuteChanged > 0);
            Assert.False(t._timeline.FillFrameCommand.CanExecute(video));
            t._timeline.FillFrameCommand.Execute(video);    // a stale menu: refused without an edit
            Assert.Equal(1.0, ((VideoClip)video.Clip).Scale);
        }
        Assert.True(t._timeline.FillFrameCommand.CanExecute(video));
    }

    // --- §2: the Inspector --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_Inspector_buttons_fill_and_fit_the_shown_clip()
    {
        var t = With(1);
        await t.AddAsync(t.Video(1080, 1920));
        var clip = (VideoClip)t.Clips.Single();
        var inspector = new InspectorViewModel(t._edit, t._status, editingLock: t._lock);
        inspector.ShowClip(new TimelineClipSelection(clip, "vivo.mp4", null, Rate));

        inspector.FillFrameCommand.Execute(null);
        Assert.Equal(1920.0 * 1920 / (1080.0 * 1080), clip.Scale, 12);
        Assert.StartsWith(Strings.Frame_Filled[..10], t._status.Message);

        inspector.FitFrameCommand.Execute(null);
        Assert.Equal(1.0, clip.Scale);

        using (t._lock.Acquire())
        {
            inspector.FillFrameCommand.Execute(null);
            Assert.Equal(1.0, clip.Scale);
        }
    }
}
