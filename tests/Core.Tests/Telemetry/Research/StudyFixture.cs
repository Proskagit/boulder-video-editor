using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;

namespace AiVideoEditor.Core.Tests.Telemetry.Research;

/// <summary>Synthetic studies for Step 35.3b1: a FIT with three climbs, videos named by a hash letter, a ticking clock.</summary>
internal static class StudyFixture
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);
    public static readonly long Second = TimeSpan.TicksPerSecond;

    public static string Hash(char c) => new(c, 64);

    public static StudyClimb Climb(int index, int number, int startSeconds, long? startExtraTicks = null) =>
        new(index, number, T0.AddSeconds(startSeconds).AddTicks(startExtraTicks ?? 0), 20 * Second, T0.AddSeconds(startSeconds + 21));

    public static StudyFit Fit(Guid? key = null, char hash = 'f', params StudyClimb[] climbs) =>
        new(key ?? Guid.NewGuid(), Hash(hash), 54_003, new TrainingActivity(T0, T0.AddHours(1), 294, 814),
            climbs.Length > 0 ? climbs : new[] { Climb(0, 1, 0), Climb(2, 2, 258), Climb(4, 3, 542) });

    public static StudyVideo Video(char hash, long durationSeconds = 600, string name = "video_20260927_100628.mp4") =>
        new(Guid.NewGuid(), name, 1_000 + hash, Hash(hash), durationSeconds * Second, true, "2026-09-27T07:07:00.000000Z",
            RecordingTimeTagOrigin.Format, 0, null);

    /// <summary>A clock that advances one second per reading.</summary>
    public static Func<DateTimeOffset> Clock()
    {
        var t = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        return () => t = t.AddSeconds(1);
    }

    public static (SyncStudy Study, SyncStudyEditor Editor) Study(params char[] videoHashes)
    {
        var fit = Fit();
        var videos = (videoHashes.Length > 0 ? videoHashes : new[] { 'a' })
            .Select(h => Video(h))
            .Select(v => (v, new StudyLocation(v.VideoKey, $@"D:\set\{v.Sha256[0]}.mp4", 100L)))
            .ToList();
        var clock = Clock();
        var study = SyncStudyEditor.Create(Guid.NewGuid(), fit, videos, new StudyLocation(fit.FitKey, @"D:\set\a.fit", 100L), clock);
        return (study, new SyncStudyEditor(study, clock));
    }

    public static Guid FirstVideo(this SyncStudy study) => study.Draft.Videos[0].VideoKey;

    public static ClimbIdentity Identity(this SyncStudy study, int number) =>
        study.Draft.Fit.Climbs.Single(c => c.ClimbNumber == number).Identity;
}
