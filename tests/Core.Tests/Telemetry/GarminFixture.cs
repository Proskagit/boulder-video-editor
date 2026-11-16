using W = AiVideoEditor.Core.Tests.Telemetry.FitTestWriter;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>
/// A synthetic Garmin export shaped like the Venu X1 training of 2026-11-14 (D062): no device name in the file, the
/// session and the activity stamped with the training's start, records only on a change (1–6 s apart, «smart
/// recording»), a start and a final stop_all, and climb / rest splits with the route fields 69–73 — climb 1 a send on a
/// French 6b (11), climb 2 an attempt on a 6c (13) with 2 falls, climb 3 completed with a fall on a 6b.
/// </summary>
internal static class GarminFixture
{
    public static readonly DateTimeOffset Start = new(2026, 11, 14, 12, 31, 9, TimeSpan.Zero);
    public static readonly long T0 = W.FitTime(Start);

    /// <summary>The session's elapsed time: 120.292 s.</summary>
    public const long ElapsedMs = 120_292;

    private static readonly int[] Steps = [1, 3, 1, 2, 6, 1, 1, 4];

    public static FitTestWriter Writer(bool sessionStampedWithStart = true)
    {
        var w = new FitTestWriter()
            .Define(0, 0, (0, 1, W.Enum), (1, 2, W.UInt16), (2, 2, W.UInt16), (4, 4, W.UInt32))
            .Data(0, 4L, 1L, 4603L, T0)
            .Define(1, 21, (253, 4, W.UInt32), (0, 1, W.Enum), (1, 1, W.Enum))
            .Data(1, T0, 0L, 0L)
            .Define(2, 20, (253, 4, W.UInt32), (3, 1, W.UInt8));

        long s = 0;
        for (var i = 0; s <= 120; i++)
        {
            w.Data(2, T0 + s, 100L + s % 50);
            s += Steps[i % Steps.Length];
        }

        w.Data(1, T0 + 120, 0L, 4L);   // stop_all

        w.Define(3, 312, (254, 2, W.UInt16), (0, 1, W.Enum), (1, 4, W.UInt32), (9, 4, W.UInt32), (27, 4, W.UInt32),
                (15, 1, W.UInt8), (16, 1, W.UInt8), (69, 1, W.Enum), (70, 4, W.UInt32), (71, 1, W.Enum), (72, 2, W.UInt16),
                (73, 1, W.Enum))
            .Data(3, 0L, 9L, 20_000L, T0, T0 + 20, 110L, 120L, 2L, 11L, 3L, 0L, 1L)
            .Data(3, 1L, 10L, 30_000L, T0 + 20, T0 + 50, 115L, 125L, null, null, null, null, null)
            .Data(3, 2L, 9L, 25_000L, T0 + 50, T0 + 75, 120L, 140L, 2L, 13L, 2L, 2L, null)
            .Data(3, 3L, 10L, 20_000L, T0 + 75, T0 + 95, 118L, 130L, null, null, null, null, null)
            .Data(3, 4L, 9L, 25_292L, T0 + 95, T0 + 120, 121L, 141L, 2L, 11L, 3L, 1L, null);

        // Garmin stamps the session and the activity with the start; the end is the start plus the elapsed time.
        w.Define(4, 18, (253, 4, W.UInt32), (2, 4, W.UInt32), (7, 4, W.UInt32), (8, 4, W.UInt32), (5, 1, W.Enum), (6, 1, W.Enum), (16, 1, W.UInt8), (17, 1, W.UInt8))
            .Data(4, sessionStampedWithStart ? T0 : T0 + 120, T0, ElapsedMs, ElapsedMs, 31L, 68L, 114L, 169L);
        w.Define(5, 34, (253, 4, W.UInt32), (5, 4, W.UInt32))
            .Data(5, T0, T0 + 10_800);
        return w;
    }
}
