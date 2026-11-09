using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 35.5: the heart-rate overlay's value on every frame — the BPM of the picture right below it through the one mapping
/// of D049 §6 (<c>w = Z + s + ClockCorrection</c>, the clip's exact source-time rule) and the readout's rule (the latest
/// sample at most 2 s old, inside the recording, outside the pauses); nothing where there is no value; equal to the
/// Inspector's readout; and its place in the snapshot (presentation only, no other kind touched).
/// </summary>
public class HeartRateOverlayTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };
    private readonly Track _v3 = new() { Type = TrackType.Video, Name = "V3", Order = 2 };

    public HeartRateOverlayTests() => _project.Timeline.VideoTracks.AddRange(new[] { _v1, _v2, _v3 });

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    /// <summary>The value of the sample of second <paramref name="i"/>: distinct for every second of a minute.</summary>
    private static int Bpm(long i) => 100 + (int)(i % 60);

    /// <summary>
    /// A 600 s training from <see cref="Start"/>: a sample every second (BPM 100 + i mod 60), none from 300 s to 309 s (a gap in
    /// the data), the timer paused from 200 s to 228 s.
    /// </summary>
    private static ClimbingWorkout Workout() => new()
    {
        DeviceName = "COROS APEX 4 42mm", Manufacturer = 294, Product = null, StartUtc = Start, EndUtc = Start.AddSeconds(600),
        UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(600), TimerTime = MediaTime.FromSeconds(572),
        Sport = 31, SubSport = 69, AverageHeartRate = 130, MaxHeartRate = 159,
        HeartRate = Enumerable.Range(0, 600).Where(i => i is < 300 or > 309).Select(i => new HeartRateSample(Start.AddSeconds(i), Bpm(i))).ToList(),
        Pauses = new[] { new WorkoutInterval(Start.AddSeconds(200), Start.AddSeconds(228)) },
        Segments = Array.Empty<WorkoutSegment>(),
        Warnings = Array.Empty<TrainingFileWarning>()
    };

    private TrainingReference Train(MediaTime? clockCorrection = null, TrainingAvailability availability = TrainingAvailability.Available)
    {
        var workout = Workout();
        var training = new TrainingReference
        {
            FilePath = @"C:\fit\Болдеринг20260927100239.fit", FileSizeBytes = 1000, Sha256 = new string('a', 64),
            Activity = TrainingActivity.Of(workout), Workout = workout, Availability = availability,
            ClockCorrection = clockCorrection ?? MediaTime.Zero
        };
        _project.Training = training;
        return training;
    }

    private static RecordingTimeProbe Tag(DateTimeOffset end) =>
        RecordingTimeProbe.Found(end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format);

    /// <param name="zero">D055 §1: the time of the file's first frame, by its recording-time tag (read as the end of its
    /// 900 s); null — the file gives none. Never the recording zero <c>Z</c>, which the heart rate never reads.</param>
    private (VideoClip Clip, MediaAsset Asset) Video(Track track, long start, long frames, DateTimeOffset? zero, long sourceIn = 0,
        ClipSpeed? speed = null, double opacity = 1)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(900), FrameRate = Rate, Width = 1920, Height = 1080, DisplayRotation = 0,
                DisplayWidth = 1920, DisplayHeight = 1080,
                RecordingTimeProbe = zero is { } first ? Tag(first.AddSeconds(900)) : null
            }
        };
        _project.MediaAssets.Add(asset);
        var clipSpeed = speed ?? ClipSpeed.Normal;
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(frames), SourceIn = F(sourceIn),
            SourceOut = F(sourceIn) + SpeedTiming.SourceLength(frames, clipSpeed, Rate), Speed = clipSpeed, Opacity = opacity
        };
        track.Clips.Add(clip);
        return (clip, asset);
    }

    private TemplateClip Overlay(Track track, long start, long frames, TemplateKind kind = TemplateKind.HeartRate)
    {
        var fields = TemplateRules.Defaults(kind);
        var clip = new TemplateClip { Kind = kind, TimelineStart = F(start), Duration = F(frames), AccentHex = fields.AccentHex, Title = fields.Title };
        track.Clips.Add(clip);
        return clip;
    }

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_project, 1);

    private static int? ValueAt(PlaybackSnapshot snapshot, long frame) =>
        snapshot.LayersAt(F(frame)).OfType<TemplateLayer>().Single(l => l.Kind == TemplateKind.HeartRate).HeartRate;

    // --- the mapping ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_frame_shows_the_sample_of_its_watch_second()
    {
        Train();
        Video(_v1, 0, 25 * 120, Start.AddSeconds(10));          // the video starts 10 s into the training
        Overlay(_v2, 0, 25 * 120);
        var snapshot = Snapshot();

        foreach (var frame in new long[] { 0, 1, 24, 25, 26, 25 * 37 + 12, 25 * 119 + 24 })
            Assert.Equal(Bpm(10 + frame / 25), ValueAt(snapshot, frame));
    }

    [Fact]
    public void The_clock_correction_moves_the_watch_time()
    {
        Train(clockCorrection: MediaTime.FromSeconds(-7));      // w = c − 7 s
        Video(_v1, 0, 25 * 60, Start.AddSeconds(20));
        Overlay(_v2, 0, 25 * 60);
        var snapshot = Snapshot();

        Assert.Equal(Bpm(13), ValueAt(snapshot, 0));
        Assert.Equal(Bpm(13 + 30), ValueAt(snapshot, 25 * 30));
    }

    [Fact]
    public void A_trimmed_moved_clip_shows_its_source_time()
    {
        Train();
        Video(_v1, 25 * 100, 25 * 60, Start.AddSeconds(5), sourceIn: 25 * 40);   // source 40 s at timeline 100 s
        Overlay(_v2, 25 * 100, 25 * 60);
        var snapshot = Snapshot();

        Assert.Equal(Bpm(45), ValueAt(snapshot, 25 * 100));
        Assert.Equal(Bpm(45 + 12), ValueAt(snapshot, 25 * 112 + 3));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public void At_another_speed_the_value_follows_the_source_picture(double speed)
    {
        Train();
        var clipSpeed = ClipSpeed.FromSteps((int)Math.Round(speed * ClipSpeed.StepsPerUnit));
        Video(_v1, 0, 25 * 60, Start.AddSeconds(30), speed: clipSpeed);
        Overlay(_v2, 0, 25 * 60);
        var snapshot = Snapshot();

        foreach (var frame in new long[] { 0, 25 * 3, 25 * 20 + 7 })
        {
            var source = HoldAnimationRule.SourceTimeAt(MediaTime.Zero, MediaTime.Zero, clipSpeed, frame, Rate);
            Assert.Equal(Bpm(30 + (long)Math.Floor(source.TotalSeconds)), ValueAt(snapshot, frame));
        }
    }

    [Fact]
    public void It_equals_the_inspector_readout_on_every_frame()
    {
        var training = Train(clockCorrection: MediaTime.FromSeconds(3.4));
        var (clip, asset) = Video(_v1, 25 * 2, 25 * 400, Start.AddSeconds(-30), sourceIn: 13, speed: ClipSpeed.FromSteps(25));
        Overlay(_v2, 0, 25 * 410);
        var snapshot = Snapshot();

        for (var frame = 25 * 2L; frame < 25 * 402; frame += 7)
            Assert.Equal(TrainingAlignment.ReadoutAt(training, clip, asset, frame, Rate).Bpm, ValueAt(snapshot, frame));
    }

    // --- no value -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Pauses_gaps_and_the_edges_of_the_training_show_nothing()
    {
        Train();
        Video(_v1, 0, 25 * 700, Start.AddSeconds(-20));         // the video starts 20 s before the training
        Overlay(_v2, 0, 25 * 700);
        var snapshot = Snapshot();

        Assert.Null(ValueAt(snapshot, 25 * 10));                  // before the recording
        Assert.Equal(Bpm(0), ValueAt(snapshot, 25 * 20));         // its first sample
        Assert.Equal(Bpm(199), ValueAt(snapshot, 25 * 219 + 24)); // just before the pause
        Assert.Null(ValueAt(snapshot, 25 * 220));                 // in the pause
        Assert.Null(ValueAt(snapshot, 25 * 247));
        Assert.Equal(Bpm(228), ValueAt(snapshot, 25 * 248));      // after it
        Assert.Equal(Bpm(299), ValueAt(snapshot, 25 * 321));      // the last sample before the gap, 2 s old: still shown
        Assert.Null(ValueAt(snapshot, 25 * 322 + 1));             // older than 2 s: nothing, never a held or interpolated value
        Assert.Equal(Bpm(310), ValueAt(snapshot, 25 * 330));      // after the gap
        Assert.Null(ValueAt(snapshot, 25 * 620));                 // after the recording
    }

    [Fact]
    public void Without_a_training_a_usable_one_or_a_recording_zero_it_shows_nothing()
    {
        Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        var (_, unknownZero) = Video(_v1, 25 * 60, 25 * 60, zero: null);
        Overlay(_v2, 0, 25 * 120);

        Assert.Null(ValueAt(Snapshot(), 25 * 5));                 // no training

        Train(availability: TrainingAvailability.Missing);
        Assert.Null(ValueAt(Snapshot(), 25 * 5));                 // the FIT not available

        Train();
        var snapshot = Snapshot();
        Assert.Equal(Bpm(15), ValueAt(snapshot, 25 * 5));
        Assert.Null(ValueAt(snapshot, 25 * 65));                  // a video with no recording zero
        Assert.Null(snapshot.VideoLayers.SelectMany(l => l.Spans).Single(s => s.AssetId == unknownZero.Id).HeartRate);
    }

    // --- the picture below ----------------------------------------------------------------------------------------------

    [Fact]
    public void It_reads_the_nearest_shown_picture_below_it()
    {
        Train();
        Video(_v1, 0, 25 * 60, Start.AddSeconds(100));
        Video(_v2, 25 * 10, 25 * 10, Start.AddSeconds(400));                 // V2 covers V1 from 10 s to 20 s
        Video(_v2, 25 * 30, 25 * 10, Start.AddSeconds(500), opacity: 0);      // invisible: V1 is what shows there
        Overlay(_v3, 0, 25 * 70);
        var snapshot = Snapshot();

        Assert.Equal(Bpm(105), ValueAt(snapshot, 25 * 5));
        Assert.Equal(Bpm(405), ValueAt(snapshot, 25 * 15));
        Assert.Equal(Bpm(135), ValueAt(snapshot, 25 * 35));
        Assert.Null(ValueAt(snapshot, 25 * 65));                  // nothing below: a gap
    }

    [Fact]
    public void A_picture_below_that_is_not_a_video_gives_nothing()
    {
        Train();
        Video(_v1, 0, 25 * 60, Start.AddSeconds(100));
        var image = new MediaAsset { FilePath = @"C:\media\photo.jpg", Kind = MediaKind.Image, Metadata = new MediaMetadata { Width = 100, Height = 100 } };
        _project.MediaAssets.Add(image);
        _v2.Clips.Add(new ImageClip { MediaAssetId = image.Id, TimelineStart = F(0), Duration = F(25 * 10), SourceOut = F(25 * 10) });
        Overlay(_v3, 0, 25 * 20);
        var snapshot = Snapshot();

        Assert.Null(ValueAt(snapshot, 25 * 5));                   // the photo is the picture below
        Assert.Equal(Bpm(115), ValueAt(snapshot, 25 * 15));
    }

    [Fact]
    public void Two_overlays_over_two_trainings_free_videos_read_their_own_pictures()
    {
        Train();
        Video(_v1, 0, 25 * 20, Start.AddSeconds(50));
        Video(_v1, 25 * 20, 25 * 20, Start.AddSeconds(450));
        Overlay(_v2, 0, 25 * 20);
        Overlay(_v2, 25 * 20, 25 * 20);
        var snapshot = Snapshot();

        Assert.Equal(Bpm(51), ValueAt(snapshot, 25));
        Assert.Equal(Bpm(451), ValueAt(snapshot, 25 * 21));
    }

    // --- the snapshot ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Other_kinds_never_carry_a_value()
    {
        Train();
        Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        foreach (var (kind, i) in new[] { TemplateKind.Rec, TemplateKind.Vhs, TemplateKind.Boulder, TemplateKind.Highlight }.Select((k, i) => (k, i)))
            Overlay(_v2, 25 * 10 * i, 25 * 10, kind);
        var snapshot = Snapshot();

        for (var frame = 0L; frame < 25 * 40; frame += 25)
            Assert.All(snapshot.LayersAt(F(frame)).OfType<TemplateLayer>(), l => Assert.Null(l.HeartRate));
    }

    [Fact]
    public void A_new_recording_zero_never_moves_the_value_and_a_new_training_changes_only_the_presentation()
    {
        Train();
        var (_, asset) = Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        Overlay(_v2, 0, 25 * 60);
        var before = Snapshot();

        asset.RecordingZeroOverrideUtc = Start.AddSeconds(40);    // D055 §2: Z refines the route bounds only
        var after = Snapshot();
        Assert.True(before.DiffersOnlyInPresentation(after));     // no decoder restarts
        Assert.Equal(Bpm(10), ValueAt(after, 0));
        asset.Metadata!.RecordingTimeProbe = Tag(Start.AddSeconds(940));   // another file time moves it
        Assert.Equal(Bpm(40), ValueAt(Snapshot(), 0));

        _project.Training = null;
        Assert.True(after.DiffersOnlyInPresentation(Snapshot()));
        Assert.Null(ValueAt(Snapshot(), 0));
    }

    [Fact]
    public void The_draw_operation_carries_the_value()
    {
        Train();
        Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        Overlay(_v2, 0, 25 * 60);
        var layer = Snapshot().LayersAt(F(25)).OfType<TemplateLayer>().Single();
        var canvas = new FrameSize(1920, 1080);

        var plan = CompositionDrawPlan.Build(canvas, Affine2D.Identity, new[] { new ResolvedLayer(layer, null) });

        Assert.Equal(Bpm(11), plan.Operations.OfType<TemplateDraw>().Single().HeartRate);
    }

    // --- rules ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_defaults_and_the_validation_of_the_overlay()
    {
        var defaults = TemplateRules.Defaults(TemplateKind.HeartRate);
        Assert.Equal(TemplateRules.HeartRateAccentHex, defaults.AccentHex);
        Assert.Equal(HeartRateLook.Plate, defaults.Look);
        Assert.Equal(OverlayCorner.BottomRight, defaults.Corner);
        Assert.Null(TemplateRules.Validate(defaults));
        Assert.NotNull(TemplateRules.Validate(defaults with { Look = (HeartRateLook)7 }));
        Assert.NotNull(TemplateRules.Validate(defaults with { Corner = (OverlayCorner)7 }));
        Assert.Equal((0.3, 0.3), TemplateRules.Animation(TemplateKind.HeartRate));
        Assert.Equal(MediaTime.FromSeconds(10), TemplateRules.DefaultDuration(TemplateKind.HeartRate));
    }

    // --- D055 §4: the heart rate of a green route area ---------------------------------------------------------------------

    private static int? AreaValueAt(PlaybackSnapshot snapshot, long frame) =>
        snapshot.LayersAt(F(frame)).OfType<TemplateLayer>().Single(l => l.Kind == TemplateKind.Boulder).HeartRate;

    /// <summary>A green route area — a «Боулдер» anchored to <paramref name="clip"/> — on V2 over [start, start + frames).</summary>
    private TemplateClip Area(VideoClip clip, long start, long frames, bool fromFit = false)
    {
        var area = new TemplateClip
        {
            Kind = TemplateKind.Boulder, TimelineStart = F(start), Duration = F(frames), AccentHex = TemplateRules.DefaultAccentHex,
            AnchorClipId = clip.Id, AnchorRouteId = Guid.NewGuid(),
            FitClimb = fromFit ? new ClimbIdentity(0, 1, Start) : null
        };
        _v2.Clips.Add(area);
        _v2.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return area;
    }

    [Fact]
    public void A_green_area_shows_its_clips_heart_rate_on_its_span_by_the_files_time_made_by_hand_or_from_the_fit()
    {
        Train();
        var (clip, _) = Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        Area(clip, 25 * 5, 25 * 10);                                  // by hand
        Area(clip, 25 * 30, 25 * 10, fromFit: true);                  // from the FIT
        var snapshot = Snapshot();

        Assert.Equal(Bpm(10 + 6), AreaValueAt(snapshot, 25 * 6));       // the clip's picture at 6 s: w = file + 6 s
        Assert.Equal(Bpm(10 + 35), AreaValueAt(snapshot, 25 * 35));
        Assert.Empty(snapshot.LayersAt(F(25 * 20)).OfType<TemplateLayer>());   // outside both areas: nothing
    }

    [Fact]
    public void Its_bounds_limit_where_the_heart_rate_is_shown_and_never_move_the_samples()
    {
        Train();
        var (clip, _) = Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        var area = Area(clip, 25 * 10, 25 * 10);
        var at15 = AreaValueAt(Snapshot(), 25 * 15);

        area.TimelineStart = F(25 * 2);                               // stretched to the left and to the right
        area.Duration = F(25 * 30);
        Assert.Equal(at15, AreaValueAt(Snapshot(), 25 * 15));             // the same picture, the same sample
        Assert.Equal(Bpm(10 + 3), AreaValueAt(Snapshot(), 25 * 3));       // shown where the area now is

        area.TimelineStart = F(25 * 14);                              // narrowed
        area.Duration = F(25 * 2);
        Assert.Equal(at15, AreaValueAt(Snapshot(), 25 * 15));
        Assert.Empty(Snapshot().LayersAt(F(25 * 3)).OfType<TemplateLayer>());
    }

    [Fact]
    public void An_area_never_reads_the_recording_zero_and_shows_nothing_where_the_fit_has_no_value()
    {
        Train();
        var (clip, asset) = Video(_v1, 0, 25 * 400, Start.AddSeconds(10));
        Area(clip, 0, 25 * 400);
        Assert.Equal(Bpm(10 + 5), AreaValueAt(Snapshot(), 25 * 5));

        asset.RecordingZeroOverrideUtc = Start.AddSeconds(100);       // D055 §2: Z refines the route bounds only
        Assert.Equal(Bpm(10 + 5), AreaValueAt(Snapshot(), 25 * 5));

        Assert.Null(AreaValueAt(Snapshot(), 25 * 200));                   // w = 210 s: the timer's pause — the area stays
        Assert.Single(Snapshot().LayersAt(F(25 * 200)).OfType<TemplateLayer>());
        Assert.Null(AreaValueAt(Snapshot(), 25 * 295));                   // w = 305 s: a gap in the data

        asset.Metadata!.RecordingTimeProbe = null;                    // no file time: nothing, even with Z
        Assert.Null(AreaValueAt(Snapshot(), 25 * 5));
    }

    [Fact]
    public void An_area_hidden_switched_off_unanchored_or_without_a_training_shows_no_heart_rate()
    {
        var (clip, _) = Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        var area = Area(clip, 0, 25 * 60);
        Assert.Null(Snapshot().VideoLayers.SelectMany(l => l.Templates).Single().HeartRate);   // no training

        Train();
        Assert.Equal(Bpm(15), AreaValueAt(Snapshot(), 25 * 5));
        area.ShowHeartRate = false;
        Assert.Null(AreaValueAt(Snapshot(), 25 * 5));
        area.ShowHeartRate = true;
        area.AnchorClipId = null;                                     // a free «Боулдер»
        Assert.Null(AreaValueAt(Snapshot(), 25 * 5));
    }

    [Fact]
    public void An_areas_draw_operation_carries_the_value_and_its_own_heart_colour()
    {
        Train();
        var (clip, _) = Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        var area = Area(clip, 0, 25 * 60);
        area.HeartHex = "#00E676";
        var layers = Snapshot().LayersAt(F(25 * 5));
        var template = layers.OfType<TemplateLayer>().Single();
        Assert.Equal(Bpm(15), template.HeartRate);
        Assert.Equal("#00E676", template.Span.Fields.HeartColorFor(TemplateKind.Boulder));
        Assert.Equal(TemplateRules.DefaultAccentHex, template.Span.Fields.AccentHex);   // the route's colour untouched
    }

    // --- D057 §3.8: the heart rate's display offset ------------------------------------------------------------------------

    private PlaybackSnapshot Snapshot(MediaTime offset) => PlaybackSnapshotBuilder.Build(_project, 1, null, offset);

    [Fact]
    public void A_display_offset_of_minus_sixty_seconds_shows_the_value_of_a_minute_later_on_the_watch()
    {
        Train();
        Video(_v1, 0, 25 * 120, Start.AddSeconds(10));
        Overlay(_v2, 0, 25 * 120);
        var shifted = Snapshot(MediaTime.FromSeconds(-60));
        foreach (var frame in new long[] { 0, 25, 25 * 37 + 12 })
            Assert.Equal(Bpm(10 + frame / 25 + 60), ValueAt(shifted, frame));
        Assert.Equal(Bpm(10), ValueAt(Snapshot(MediaTime.Zero), 0));           // zero: as before
        Assert.Equal(Bpm(10 - 5 + 30), ValueAt(Snapshot(MediaTime.FromSeconds(5)), 25 * 30));   // a positive one: later
    }

    [Fact]
    public void The_offset_shifts_only_the_values_shown_in_a_green_area_never_the_area_or_the_synchronisation()
    {
        var training = Train(clockCorrection: MediaTime.FromSeconds(2));
        var (clip, asset) = Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        var area = Area(clip, 25 * 5, 25 * 10);
        var (start, duration, zero) = (area.TimelineStart, area.Duration, FitRouteBinding.FileTimeOf(asset, training.Workout!)!.Value.Zero);

        var shifted = Snapshot(MediaTime.FromSeconds(-60));
        Assert.Equal(Bpm(10 + 2 + 5 + 60), AreaValueAt(shifted, 25 * 5));
        Assert.Null(shifted.LayersAt(F(25 * 4)).OfType<TemplateLayer>().SingleOrDefault(l => l.Kind == TemplateKind.Boulder));   // still only in the area

        Assert.Equal((start, duration), (area.TimelineStart, area.Duration));                 // the area unchanged
        Assert.Equal(MediaTime.FromSeconds(2), training.ClockCorrection);                     // CC unchanged
        Assert.Equal(zero, FitRouteBinding.FileTimeOf(asset, training.Workout!)!.Value.Zero);  // T_file unchanged
        var readout = TrainingAlignment.ReadoutAt(training, clip, asset, 25 * 5, Rate);
        Assert.Equal(Bpm(10 + 2 + 5), readout.Bpm);                                          // the readout: the synchronised value
    }

    [Fact]
    public void The_export_takes_the_offset_it_is_given()
    {
        Train();
        Video(_v1, 0, 25 * 60, Start.AddSeconds(10));
        Overlay(_v2, 0, 25 * 60);
        foreach (var asset in _project.MediaAssets) asset.AnalysisStatus = MediaAnalysisStatus.Completed;
        var environment = new AiVideoEditor.Core.Export.ExportPreflightEnvironment(true, _ => true, _ => true, path => string.Equals(path, @"C:\out", StringComparison.OrdinalIgnoreCase))
        {
            HeartRateDisplayOffset = MediaTime.FromSeconds(-60)
        };
        var check = AiVideoEditor.Core.Export.ExportPreflight.Check(_project, @"C:\out\clip.mp4", environment);
        var job = check.Job ?? throw new Xunit.Sdk.XunitException(string.Join("; ", check.Issues.Select(i => i.Kind + ": " + i.Message)));
        Assert.Equal(Bpm(10 + 60), ValueAt(job.Snapshot, 0));
    }
}
