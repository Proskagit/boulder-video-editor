using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Calibration;
using AiVideoEditor.Media;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>
/// Step 37.7 on the product owner's real sessions, read only: the FIT and the 16 videos of 27.09 (probed in place) with each
/// video's recording zero set the way the owner sets it — «Совместить кадр с концом подъёма» at the owner's frozen mark of a
/// confirmed climb (35.3R2) —, the proposals of D051 through the real edit and training services (A1, A13); and the
/// measurement of A10 on 27.09 and 08.08 (the owner's research table of 08.08), the K3 estimate by the app's own code.
/// </summary>
public sealed class Phase37RealSessionTests(ITestOutputHelper output)
{
    private static readonly string Set0809 = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(RealSet.FitPath)!)!, "Боулдер 08.08.2026");

    private sealed record Mark(string Video, int Climb, long StartTicks, long EndTicks);

    /// <summary>The owner's confirmed marks of 27.09 (the frozen study of 35.3R2).</summary>
    private static IReadOnlyList<Mark> ConfirmedMarks()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(RealSet.FitPath, ".syncstudy.json")));
        var draft = json.RootElement.GetProperty("draft");
        var videos = draft.GetProperty("videos").EnumerateArray()
            .ToDictionary(v => v.GetProperty("videoKey").GetString()!, v => v.GetProperty("originalFileName").GetString()!);
        var decisions = draft.GetProperty("decisions").EnumerateArray().ToDictionary(d => d.GetProperty("markId").GetString()!);
        return draft.GetProperty("marks").EnumerateArray()
            .Where(m => decisions.TryGetValue(m.GetProperty("markId").GetString()!, out var d) && d.GetProperty("state").GetString() == "confirmed")
            .Select(m => new Mark(videos[m.GetProperty("videoKey").GetString()!],
                decisions[m.GetProperty("markId").GetString()!].GetProperty("climb").GetProperty("climbNumber").GetInt32(),
                m.GetProperty("startSourceTicks").GetInt64(), m.GetProperty("endSourceTicks").GetInt64()))
            .ToList();
    }

    // --- A1, A13 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A1_A13_one_mark_for_every_climb_whose_start_is_filmed_12_s_before_it_none_without_Z_none_before_the_recording()
    {
        var workout = await RealSet.WorkoutAsync(RealSet.FitPath);
        var assets = await RealSet.ProbeAsync(RealSet.VideoPaths(RealSet.FitPath));
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        projects.Current.Settings.FrameRate = FrameRate.Fps30;
        projects.Current.Settings.IsFrameRateLocked = true;
        projects.AddMediaAssets(assets);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var import = new TrainingImportService(NullLogger<TrainingImportService>.Instance);
        var session = new TrainingSessionService(projects, import);
        session.Attach(await import.LoadAsync(RealSet.FitPath), RealSet.FitPath);

        // The owner's Z: the first confirmed climb of each video matched by its end at the owner's mark.
        var zeros = new Dictionary<Guid, DateTimeOffset>();
        foreach (var group in ConfirmedMarks().GroupBy(m => m.Video))
        {
            var mark = group.OrderBy(m => m.StartTicks).First();
            var asset = assets.Single(a => Path.GetFileName(a.FilePath) == group.Key);
            var climb = workout.Climbs.Single(c => c.ClimbNumber == mark.Climb);
            var zero = TrainingAlignment.ZeroForClimb(climb, ClimbAnchor.End, MediaTime.Zero, new MediaTime(mark.EndTicks));
            Assert.True(edit.SetRecordingZeroOverride(asset.Id, zero).Success);
            zeros[asset.Id] = zero;
        }
        var marks = projects.Current.Training!.Marks;

        // The expectation by hand: a climb gets a mark when its start or W_auto lies in a recording with Z (D051 §5.5.1).
        var initial = TimeSpan.FromTicks(StartCalibration.InitialTicks);
        bool Inside(DateTimeOffset w, MediaAsset a) => zeros.TryGetValue(a.Id, out var z) && w >= z && w < z + a.Metadata!.Duration.ToTimeSpan();
        var expected = workout.Climbs.Where(c => assets.Any(a => Inside(c.StartUtc, a) || Inside(c.StartUtc - initial, a)))
            .Select(c => c.ClimbNumber!.Value).OrderBy(n => n).ToList();
        Assert.Equal(expected, marks.Select(m => m.Climb.ClimbNumber).OrderBy(n => n).ToList());
        Assert.Equal(marks.Count, marks.Select(m => m.Climb).Distinct().Count());               // one per climb
        Assert.All(marks, m => Assert.Contains(m.MediaAssetId, zeros.Keys));                     // none on a video without Z
        foreach (var m in marks)
        {
            var climb = workout.Climbs.Single(c => c.ClimbNumber == m.Climb.ClimbNumber);
            Assert.Equal(climb.StartUtc - initial, m.AutoStartUtc);                               // 12 s before the start
            Assert.Equal(StartCalibration.InitialTicks, m.StartCorrectionTicks);
            Assert.NotEqual(FitMarkPositionKind.Moved, m.Position.Kind);
        }
        Assert.DoesNotContain(marks, m => m.Climb.ClimbNumber == 1);                            // A13: C1 begun before every recording
        output.WriteLine($"videos with Z: {zeros.Count} of {assets.Count}; marks: {marks.Count} ({string.Join(", ", marks.OrderBy(m => m.Climb.ClimbNumber).Select(m => $"C{m.Climb.ClimbNumber} {m.Position.Kind}"))})");
    }

    // --- A10 -----------------------------------------------------------------------------------------------------------

    /// <summary>The observation of a climb as a moved mark gives it: <c>c = W_s − W_visible start</c> (seconds).</summary>
    private sealed record Observation(string Training, int Climb, double Lag);

    private static async Task<IReadOnlyList<Observation>> Observations0927()
    {
        var workout = await RealSet.WorkoutAsync(RealSet.FitPath);
        var result = new List<Observation>();
        foreach (var group in ConfirmedMarks().GroupBy(m => m.Video))
        {
            var first = group.OrderBy(m => m.StartTicks).First();
            var zero = TrainingAlignment.ZeroForClimb(workout.Climbs.Single(c => c.ClimbNumber == first.Climb), ClimbAnchor.End, MediaTime.Zero,
                new MediaTime(first.EndTicks));
            foreach (var mark in group)
            {
                var start = workout.Climbs.Single(c => c.ClimbNumber == mark.Climb).StartUtc;
                result.Add(new Observation("27.09", mark.Climb, (start - TrainingAlignment.WatchTime(zero, new MediaTime(mark.StartTicks), MediaTime.Zero)).TotalSeconds));
            }
        }
        return result;
    }

    /// <summary>08.08 from the owner's accepted research table: per climb its video and <c>k_start</c>, <c>k_end</c> (W − the
    /// file-name time + position). A video's Z matched by the end of its first climb: the lag of climb i is
    /// <c>k_start(i) − k_end(first) + 4.6 s</c>.</summary>
    private static IReadOnlyList<Observation> Observations0808()
    {
        var report = Directory.GetFiles(Set0809, "Итоги исследования*.md").Single();
        var rows = File.ReadLines(report)
            .Select(l => Regex.Match(l, @"^\| C(\d+) \| \d+ \| (\d{6}) \| ([+−-]?[\d,]+) \| ([+−-]?[\d,]+) \|"))
            .Where(m => m.Success)
            .Select(m => (Climb: int.Parse(m.Groups[1].Value), Video: m.Groups[2].Value, KStart: Number(m.Groups[3].Value), KEnd: Number(m.Groups[4].Value)))
            .ToList();
        Assert.Equal(18, rows.Count);
        return rows.GroupBy(r => r.Video).SelectMany(g =>
        {
            var first = g.OrderBy(r => r.Climb).First();
            return g.Select(r => new Observation("08.08", r.Climb, r.KStart - first.KEnd + TrainingAlignment.ClimbEndLag.TotalSeconds));
        }).ToList();

        static double Number(string s) => double.Parse(s.Replace('−', '-').Replace(',', '.'), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A10_the_share_of_routes_usable_without_a_correction_before_and_after_the_calibration_is_measured()
    {
        var observations = (await Observations0927()).Concat(Observations0808()).ToList();
        Assert.Equal(28, observations.Count);

        // The calibration from both trainings, by the app's own K3 (every mark moved to the visible start once, in order).
        var t0 = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        var records = observations.Select((o, i) => new CalibrationRecord(
            new CalibrationKey(new TrainingActivity(o.Training == "27.09" ? t0 : t0.AddDays(1), t0.AddDays(2), 294, 814),
                new ClimbIdentity(o.Climb, o.Climb, t0.AddMinutes(o.Climb))),
            (long)Math.Round(o.Lag * TimeSpan.TicksPerSecond), CalibrationRecordStatus.Used, null, t0.AddSeconds(i), t0.AddSeconds(i), t0.AddSeconds(i)))
            .ToList();
        var estimate = StartCalibration.Estimate(records) / (double)TimeSpan.TicksPerSecond;
        var initial = StartCalibration.InitialTicks / (double)TimeSpan.TicksPerSecond;

        output.WriteLine($"Observations: {observations.Count} (27.09: {observations.Count(o => o.Training == "27.09")}, 08.08: {observations.Count(o => o.Training == "08.08")}); " +
                         $"the lag W_s − visible start: median {Median(observations.Select(o => o.Lag)):F1} s");
        output.WriteLine($"StartCorrection: before {initial:F1} s, after the calibration {estimate:F2} s");
        foreach (var training in new[] { "27.09", "08.08", "both" })
        {
            var set = observations.Where(o => training == "both" || o.Training == training).ToList();
            foreach (var (label, value) in new[] { ("before", initial), ("after", estimate) })
            {
                var errors = set.Select(o => Math.Abs(o.Lag - value)).ToList();
                output.WriteLine($"{training,-6} {label,-6}: |error| median {Median(errors):F1} s; within 1 s {errors.Count(e => e <= 1)}/{set.Count}, " +
                                 $"2 s {errors.Count(e => e <= 2)}/{set.Count}, 3 s {errors.Count(e => e <= 3)}/{set.Count}; " +
                                 $"corrections (> 2 s) {errors.Count(e => e > 2)}");
            }
        }
        Assert.InRange(estimate, -30, 90);                                   // the measurement exists; no accuracy threshold (A10)
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
