using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using Xunit;
using static AiVideoEditor.Video.Tests.EncoderHarness;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Phase 8 Step 5 (D023): failures, cancellation and cleanup of the ffmpeg export encoder. Failing encoders are
/// scripts that stand in for ffmpeg: they run the real ffmpeg for one pass and misbehave in the other (exit with an
/// error after reading everything, exit at once without reading, never read, finish slowly). After every test the
/// process counter is back at its baseline, no ffmpeg/ping started by the test is alive, no temporary file is left and
/// an existing destination is either untouched (failure, cancellation) or replaced (success).
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FfmpegExportEncoderFailureTests : IDisposable
{
    private static readonly ExportOutput Short = Output(64, 36, FrameRate.Fps25, 25);   // 1 s
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-encoder-failures", Guid.NewGuid().ToString("N"));
    private readonly string _scripts;
    private readonly DateTime _started = DateTime.Now;
    private readonly int _baseline = FfmpegProcess.LiveProcesses;

    public FfmpegExportEncoderFailureTests()
    {
        Directory.CreateDirectory(_dir);
        _scripts = Path.Combine(Path.GetTempPath(), "aive-encoder-scripts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scripts);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _dir, _scripts })
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Destination => Path.Combine(_dir, "export.mp4");

    private sealed class Locator(string? path) : IFfmpegLocator
    {
        public Task<string?> GetFfmpegPathAsync(CancellationToken ct = default) => Task.FromResult(path);
    }

    /// <summary>A stand-in for ffmpeg: the real ffmpeg for the pass not named, <paramref name="behaviour"/> for the
    /// <paramref name="pass"/> ("audio": the f32le input, "video": the rawvideo input).</summary>
    private Locator Script(string pass, string behaviour)
    {
        var ffmpeg = FfmpegTools.Ffmpeg!;
        var marker = pass == "video" ? "rawvideo" : "f32le";
        var consume = $"\"{ffmpeg}\" -v error -f data -i pipe:0 -map 0 -c copy -f null -";
        var body = behaviour switch
        {
            "read-then-fail" => $"{consume}\r\nexit 5",
            "fail-at-once" => "exit 7",
            "never-read" => "ping -n 11 127.0.0.1 >nul\r\nexit 0",
            "read-then-hang" => $"{consume}\r\nping -n 11 127.0.0.1 >nul\r\nexit 0",
            _ => throw new ArgumentException(behaviour)
        };
        var path = Path.Combine(_scripts, $"ffmpeg-{pass}-{behaviour}.cmd");
        File.WriteAllText(path,
            "@echo off\r\n" +
            $"echo %* | findstr /C:\"{marker}\" >nul\r\n" +
            $"if not errorlevel 1 goto special\r\n\"{ffmpeg}\" %*\r\nexit %errorlevel%\r\n" +
            $":special\r\n{body}\r\n");
        return new Locator(path);
    }

    private void AssertCleanedUp(string? destinationContent)
    {
        Assert.Equal(_baseline, FfmpegProcess.LiveProcesses);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var alive = Process.GetProcessesByName("ffmpeg").Concat(Process.GetProcessesByName("PING"))
                .Where(p => { try { return !p.HasExited && p.StartTime >= _started.AddSeconds(-1); } catch { return false; } })
                .Select(p => $"{p.ProcessName}#{p.Id}").ToList();
            if (alive.Count == 0) break;
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail($"still running: {string.Join(", ", alive)}");
            Thread.Sleep(50);
        }
        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).ToList();
        if (destinationContent is null) Assert.Empty(files);
        else
        {
            Assert.Equal(new[] { "export.mp4" }, files);
            Assert.Equal(destinationContent, File.ReadAllText(Destination));
        }
    }

    private static Task EncodeShort(string destination, IFfmpegLocator? locator = null) =>
        Encode(Short, destination, _ => 0.1f, (n, px, stride) => NumberFrame(n, px, stride, 64, 36), locator: locator);

    // --- ffmpeg missing / folder missing ------------------------------------------------------------------------

    [Fact]
    public async Task Without_ffmpeg_the_export_cannot_start()
    {
        var error = await Assert.ThrowsAsync<ExportException>(() => Encoder(new Locator(null)).StartAsync(Short, Destination));
        Assert.Equal(ExportFailure.EncoderUnavailable, error.Failure);
        AssertCleanedUp(null);
    }

    [FfmpegFact]
    public async Task A_missing_destination_folder_is_an_output_failure()
    {
        var error = await Assert.ThrowsAsync<ExportException>(() => Encoder().StartAsync(Short, Path.Combine(_dir, "nowhere", "x.mp4")));
        Assert.Equal(ExportFailure.OutputFailed, error.Failure);
        AssertCleanedUp(null);
    }

    // --- encoder failures -----------------------------------------------------------------------------------------

    [FfmpegTheory]
    [InlineData("audio", "read-then-fail", "code 5")]
    [InlineData("video", "read-then-fail", "code 5")]
    [InlineData("audio", "fail-at-once", "code 7")]
    [InlineData("video", "fail-at-once", "code 7")]
    public async Task A_failing_encoder_fails_the_export_and_leaves_the_existing_destination_untouched(string pass, string behaviour, string reason)
    {
        File.WriteAllText(Destination, "previous export");

        var error = await Assert.ThrowsAsync<ExportException>(() => EncodeShort(Destination, Script(pass, behaviour)));

        Assert.Equal(ExportFailure.EncodeFailed, error.Failure);
        Assert.Contains(reason, error.Message);
        Assert.Contains(pass, error.Message);
        AssertCleanedUp("previous export");
    }

    [FfmpegFact]
    public async Task A_successful_export_replaces_the_existing_destination_and_leaves_no_temporary_file()
    {
        File.WriteAllText(Destination, "previous export");

        await EncodeShort(Destination);

        Assert.Equal("h264", Stream(Destination, "video").Str("codec_name"));
        Assert.Equal(new[] { "export.mp4" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.Equal(_baseline, FfmpegProcess.LiveProcesses);
    }

    // --- cancellation -----------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Cancelling_a_write_is_cancellation_and_disposing_cleans_up()
    {
        File.WriteAllText(Destination, "previous export");
        var encoding = await Encoder().StartAsync(Short, Destination);
        await encoding.WriteAudioAsync(new float[2 * 1_000]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await encoding.WriteAudioAsync(new float[2 * 1_000], cts.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await encoding.WriteAudioAsync(new float[2]));   // unusable now
        await encoding.DisposeAsync();

        AssertCleanedUp("previous export");
    }

    [FfmpegFact]
    public async Task Cancelling_while_the_encoder_does_not_read_ends_the_blocked_write()
    {
        // The video pass never reads stdin: frames fill the pipe and the write blocks until cancellation kills it.
        var encoding = await Encoder(Script("video", "never-read")).StartAsync(Output(640, 360, FrameRate.Fps25, 250), Destination);
        var audio = new float[2 * 4_800];
        for (var i = 0; i < 100; i++) await encoding.WriteAudioAsync(audio);                 // 10 s
        using var cts = new CancellationTokenSource(500);
        var frame = new byte[640 * 360 * 4];
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            for (var n = 0; n < 250; n++) await encoding.WriteFrameAsync(frame, 640 * 4, cts.Token);
        });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        await encoding.DisposeAsync();

        AssertCleanedUp(null);
    }

    [FfmpegFact]
    public async Task Cancelling_while_waiting_for_the_encoder_to_finish_is_cancellation()
    {
        var encoding = await Encoder(Script("video", "read-then-hang")).StartAsync(Short, Destination);
        await encoding.WriteAudioAsync(new float[2 * Short.AudioSampleCount]);
        for (var n = 0; n < Short.FrameCount; n++) await encoding.WriteFrameAsync(new byte[64 * 36 * 4], 64 * 4);
        using var cts = new CancellationTokenSource(300);
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encoding.CompleteAsync(cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        await encoding.DisposeAsync();

        AssertCleanedUp(null);
    }

    [FfmpegFact]
    public async Task Disposing_an_unfinished_encoding_aborts_it()
    {
        var encoding = await Encoder().StartAsync(Short, Destination);
        await encoding.WriteAudioAsync(new float[2 * Short.AudioSampleCount]);
        await encoding.WriteFrameAsync(new byte[64 * 36 * 4], 64 * 4);                      // the video pass is running

        await encoding.DisposeAsync();

        AssertCleanedUp(null);                                                                // no partial MP4 at the destination
    }

    // --- contract ----------------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task Frames_before_the_whole_audio_too_much_audio_or_missing_frames_are_rejected()
    {
        await using (var encoding = await Encoder().StartAsync(Short, Destination))
        {
            await encoding.WriteAudioAsync(new float[2 * 100]);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await encoding.WriteFrameAsync(new byte[64 * 36 * 4], 64 * 4));
        }
        await using (var encoding = await Encoder().StartAsync(Short, Destination))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await encoding.WriteAudioAsync(new float[2 * (Short.AudioSampleCount + 1)]));
            await Assert.ThrowsAsync<ArgumentException>(async () => await encoding.WriteAudioAsync(new float[3]));
        }
        await using (var encoding = await Encoder().StartAsync(Short, Destination))
        {
            await encoding.WriteAudioAsync(new float[2 * Short.AudioSampleCount]);
            await Assert.ThrowsAsync<ArgumentException>(async () => await encoding.WriteFrameAsync(new byte[10], 64 * 4));
            await encoding.WriteFrameAsync(new byte[64 * 36 * 4], 64 * 4);
            await Assert.ThrowsAsync<InvalidOperationException>(() => encoding.CompleteAsync());       // 1 of 25 frames
        }
        AssertCleanedUp(null);
    }
}
