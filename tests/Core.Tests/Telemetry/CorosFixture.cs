using W = AiVideoEditor.Core.Tests.Telemetry.FitTestWriter;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>A synthetic COROS export shaped like the product owner's session of 2026-09-27: 1 s records with a timer
/// pause, climb / rest splits, the local-time offset +3 h.</summary>
internal static class CorosFixture
{
    public static readonly DateTimeOffset Start = new(2026, 9, 27, 7, 2, 39, TimeSpan.Zero);
    public static readonly long T0 = W.FitTime(Start);   // 1 159 426 959

    /// <summary>Records t0..t0+20 and t0+30..t0+60 (the timer paused 20 → 30), heart rate 100 + seconds.</summary>
    public static FitTestWriter Writer(long manufacturer = 294, long fileType = 4, bool session = true, bool heartRate = true,
        long? localOffsetSeconds = 10_800, Action<FitTestWriter>? extraRecords = null)
    {
        var w = new FitTestWriter()
            .Define(0, 0, (0, 1, W.Enum), (1, 2, W.UInt16), (2, 2, W.UInt16), (4, 4, W.UInt32), (8, 24, W.String))
            .Data(0, fileType, manufacturer, 814L, T0 + 2_000_000, "COROS APEX 4 42mm")
            .Define(1, 21, (253, 4, W.UInt32), (0, 1, W.Enum), (1, 1, W.Enum))
            .Data(1, T0, 0L, 0L)
            .Define(2, 20, (253, 4, W.UInt32), (3, 1, W.UInt8), (2, 2, W.UInt16))
            .Define(3, 20, (3, 1, W.UInt8), (2, 2, W.UInt16));

        for (long s = 0; s <= 60; s++)
        {
            if (s > 20 && s < 30) continue;
            object? hr = heartRate ? 100L + s : null;
            if (s == 0 || s == 30) w.Data(2, T0 + s, hr, 2600L);
            else w.Compressed(3, T0 + s, hr, 2600L);
            if (s == 20) w.Data(1, T0 + 20, 0L, 4L);   // stop_all
            if (s == 30) w.Data(1, T0 + 30, 0L, 0L);   // start
        }

        extraRecords?.Invoke(w);
        w.Data(1, T0 + 60, 0L, 4L);

        w.Define(4, 312, (254, 2, W.UInt16), (0, 1, W.Enum), (1, 4, W.UInt32), (9, 4, W.UInt32), (27, 4, W.UInt32), (15, 1, W.UInt8), (16, 1, W.UInt8))
            .Data(4, 0L, 9L, 12_340L, T0, T0 + 12, 105L, 112L)
            .Data(4, 1L, 10L, 28_000L, T0 + 12, T0 + 40, 115L, 129L)
            .Data(4, 2L, 9L, 15_500L, T0 + 40, null, 145L, 160L);   // no end time: start + elapsed

        if (session)
        {
            w.Define(5, 18, (253, 4, W.UInt32), (2, 4, W.UInt32), (7, 4, W.UInt32), (8, 4, W.UInt32), (5, 1, W.Enum), (6, 1, W.Enum), (16, 1, W.UInt8), (17, 1, W.UInt8))
                .Data(5, T0 + 60, T0, 60_000L, 50_250L, 31L, 69L, 130L, 160L);
        }

        w.Define(6, 34, (253, 4, W.UInt32), (5, 4, W.UInt32))
            .Data(6, T0 + 60, localOffsetSeconds is { } o ? (object)(T0 + 60 + o) : null);
        return w;
    }
}
