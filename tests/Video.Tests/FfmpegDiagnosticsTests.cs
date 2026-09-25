using System.Diagnostics;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog.Extensions.Logging;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.3: FFmpeg / ffprobe diagnostics. The app's own logger configuration (<see cref="LoggingBootstrapper"/>,
/// written into a temporary folder) routes the Video subsystem and the ffmpeg / ffprobe locators to
/// <c>ffmpeg-*.log</c> and nothing else there, other sources to <c>app-*.log</c>; <c>FfmpegProcess</c> logs its
/// command line, a normal end quietly, an end by the app quietly, and a failed exit with its exit code and stderr;
/// ffprobe runs with <c>-v error</c> (clean JSON on stdout, errors on stderr) and tells a failure, its timeout and a
/// cancellation apart. In the media collection, like the other tests that count ping processes.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FfmpegDiagnosticsTests : IDisposable
{
    private readonly TestMedia _media;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-ffmpeg-log", Guid.NewGuid().ToString("N"));

    public FfmpegDiagnosticsTests(TestMedia media)
    {
        _media = media;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // --- helpers ---------------------------------------------------------------------------------------------

    private sealed record Entry(LogLevel Level, string Text);

    /// <summary>Records what a component logs (level and rendered message with its exception).</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<Entry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add(new Entry(logLevel, formatter(state, exception) + (exception is null ? "" : " | " + exception.Message)));
        }
        public List<Entry> AtLeast(LogLevel level) { lock (Entries) return Entries.Where(e => e.Level >= level).ToList(); }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public RecordingLogger Inner { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>The app's logger configuration writing into a temporary folder; the files are read after it is closed.</summary>
    private sealed class AppLogs : IDisposable
    {
        private readonly string _folder;
        private readonly SerilogLoggerFactory _factory;

        public AppLogs(string folder)
        {
            _folder = folder;
            _factory = new SerilogLoggerFactory(LoggingBootstrapper.CreateLogger(folder), dispose: true);
        }

        public ILogger For(string category) => _factory.CreateLogger(category);
        public ILogger<T> For<T>() => new Logger<T>(_factory);

        /// <summary>Closes the files and returns (app log, ffmpeg log, errors log) — "" when a file was not written.</summary>
        public (string App, string Ffmpeg, string Errors) Close()
        {
            _factory.Dispose();
            string Read(string prefix) => string.Concat(Directory.GetFiles(_folder, prefix + "*.log").Select(File.ReadAllText));
            return (Read("app-"), Read("ffmpeg-"), Read("errors-"));
        }

        public void Dispose() => _factory.Dispose();
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, $"{name}-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return path;
    }

    private sealed class Locator(string path) : IFfprobeLocator
    {
        public Task<string?> GetFfprobePathAsync(CancellationToken ct = default) => Task.FromResult<string?>(path);
    }

    private string SomeFile(string name, byte[]? content = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content ?? "x"u8.ToArray());
        return path;
    }

    private static async Task<string> RunToEnd(FfmpegProcess process)
    {
        await process.Stdout.CopyToAsync(Stream.Null);
        await process.WaitForExitAsync(CancellationToken.None);
        await process.DisposeAsync(); // waits for stderr, i.e. for the end entry
        return "";
    }

    // --- routing ----------------------------------------------------------------------------------------------

    [Fact]
    public void Video_and_locator_sources_go_to_the_ffmpeg_log_only_other_sources_to_the_app_log()
    {
        var logs = new AppLogs(Path.Combine(_dir, "logs"));
        logs.For("AiVideoEditor.Video.FfmpegProcess").LogWarning("decoder-warning-1");
        logs.For("AiVideoEditor.Video.FfprobeMediaAnalysisService").LogDebug("probe-debug-2");
        logs.For("AiVideoEditor.Infrastructure.FfmpegLocator").LogInformation("locator-info-3");
        logs.For("AiVideoEditor.UI.Services.MediaAnalysisCoordinator").LogWarning("app-warning-4");
        logs.For("AiVideoEditor.Timeline.Playback.SpanReader").LogError("app-error-5");
        using (logs.For("AiVideoEditor.UI.Somewhere").BeginScope(new Dictionary<string, object> { ["Area"] = LogArea.Ffmpeg }))
            logs.For("AiVideoEditor.UI.Somewhere").LogInformation("tagged-6"); // an explicit Area is kept

        var (app, ffmpeg, errors) = logs.Close();

        foreach (var ffmpegOnly in new[] { "decoder-warning-1", "probe-debug-2", "locator-info-3", "tagged-6" })
        {
            Assert.Contains(ffmpegOnly, ffmpeg);
            Assert.DoesNotContain(ffmpegOnly, app); // not duplicated into the application log
        }
        foreach (var appOnly in new[] { "app-warning-4", "app-error-5" })
        {
            Assert.Contains(appOnly, app);
            Assert.DoesNotContain(appOnly, ffmpeg);
        }
        Assert.Contains("(App)", app);          // the application log keeps its format
        Assert.Contains("app-error-5", errors); // the errors log still collects errors of any area
        Assert.DoesNotContain("decoder-warning-1", errors);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "logs"), "export-*.log")); // no export log written
    }

    [Theory]
    [InlineData(true, "Found ffprobe on PATH")]
    [InlineData(false, "ffprobe could not be located")]
    public async Task Executable_lookup_diagnostics_go_to_the_ffmpeg_log(bool found, string expected)
    {
        var logs = new AppLogs(Path.Combine(_dir, "logs"));
        var locator = new ExecutableLocator("ffprobe", () => null, logs.For<FfprobeLocator>(), (_, _) => Task.FromResult(found));

        await locator.GetPathAsync(CancellationToken.None);
        var (app, ffmpeg, _) = logs.Close();

        Assert.Contains(expected, ffmpeg);
        Assert.DoesNotContain(expected, app);
    }

    [FfmpegFact]
    public async Task A_failed_ffmpeg_reaches_the_ffmpeg_log_file_with_its_exit_code_and_stderr()
    {
        var logs = new AppLogs(Path.Combine(_dir, "logs"));
        var missing = Path.Combine(_dir, "no such clip.mp4");
        var process = FfmpegProcess.Start(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-nostdin", "-i", missing, "-f", "null", "-" },
            _ => false, _ => { }, logs.For("AiVideoEditor.Video.FfmpegVideoDecoder"));
        await RunToEnd(process);

        var (app, ffmpeg, _) = logs.Close();

        Assert.Contains("exited with code", ffmpeg);
        Assert.Contains($"\"{missing}\"", ffmpeg);            // the command line, quoted as it could be pasted
        Assert.Contains("No such file or directory", ffmpeg); // ffmpeg's own stderr
        Assert.DoesNotContain("exited with code", app);
    }

    // --- FfmpegProcess ------------------------------------------------------------------------------------------

    [FfmpegFact]
    public async Task A_normal_ffmpeg_run_logs_its_command_line_at_debug_and_nothing_above()
    {
        var log = new RecordingLogger();
        var process = FfmpegProcess.Start(FfmpegTools.Ffmpeg!,
            new[] { "-hide_banner", "-nostdin", "-f", "lavfi", "-i", "testsrc=size=64x36:rate=25:duration=0.2", "-f", "null", "-" },
            _ => false, _ => { }, log);
        await RunToEnd(process);

        Assert.Empty(log.AtLeast(LogLevel.Warning));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Text.Contains("testsrc=size=64x36"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Text.Contains("exited normally"));
    }

    [FfmpegFact]
    public async Task An_ffmpeg_that_fails_on_its_own_is_a_warning_with_command_line_exit_code_and_stderr()
    {
        var log = new RecordingLogger();
        var missing = Path.Combine(_dir, "missing.mp4");
        var process = FfmpegProcess.Start(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-nostdin", "-i", missing, "-f", "null", "-" },
            _ => false, _ => { }, log);
        await RunToEnd(process);

        var warning = Assert.Single(log.AtLeast(LogLevel.Warning));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("exited with code", warning.Text);
        Assert.Contains(missing, warning.Text);
        Assert.Contains("No such file or directory", warning.Text);
    }

    [FfmpegFact]
    public async Task An_ffmpeg_ended_by_the_app_is_not_reported_as_a_failure()
    {
        var log = new RecordingLogger();
        var process = FfmpegProcess.Start(FfmpegTools.Ffmpeg!,
            new[] { "-hide_banner", "-nostdin", "-f", "lavfi", "-i", "testsrc=size=64x36:rate=25:duration=30", "-f", "rawvideo", "pipe:1" },
            _ => false, _ => { }, log);
        await Task.Delay(300);

        await process.DisposeAsync(); // a seek, the end of playback, a cancelled export, closing the app

        Assert.Empty(log.AtLeast(LogLevel.Warning));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Text.Contains("ended by the app"));
    }

    // --- ffprobe ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_ffprobe_is_a_warning_with_command_line_exit_code_and_stderr()
    {
        var log = new RecordingLogger<FfprobeMediaAnalysisService>();
        var script = Script("ffprobe-fails", "echo probe-says-no-1>&2\r\nexit /b 3");
        var service = new FfprobeMediaAnalysisService(new Locator(script), log);

        var result = await service.AnalyzeAsync(SomeFile("a.mp4"));

        Assert.Equal(MediaAnalysisOutcome.ProbeProcessFailed, result.Outcome);
        var warning = Assert.Single(log.Inner.AtLeast(LogLevel.Warning));
        Assert.Contains("exited with code 3", warning.Text);
        Assert.Contains("probe-says-no", warning.Text);
        Assert.Contains("-v error -print_format json -show_format -show_streams", warning.Text);
        Assert.DoesNotContain("timed out", warning.Text);
    }

    [Fact]
    public async Task An_ffprobe_timeout_is_its_own_warning_not_a_process_failure()
    {
        var log = new RecordingLogger<FfprobeMediaAnalysisService>();
        var script = Script("ffprobe-hangs", "echo started-then-silent 1>&2\r\nping -n 31 127.0.0.1 >nul\r\nexit 0");
        var service = new FfprobeMediaAnalysisService(new Locator(script), log) { ProcessTimeout = TimeSpan.FromSeconds(1) };

        var result = await service.AnalyzeAsync(SomeFile("b.mp4"));

        Assert.Equal("FFprobe took too long to respond.", result.ErrorMessage);
        var warning = Assert.Single(log.Inner.AtLeast(LogLevel.Warning));
        Assert.Contains("timed out after 1 s", warning.Text);
        Assert.Contains("started-then-silent", warning.Text); // stderr so far
        Assert.DoesNotContain("exited with code", warning.Text);
    }

    [Fact]
    public async Task A_cancelled_ffprobe_is_only_a_debug_entry()
    {
        var log = new RecordingLogger<FfprobeMediaAnalysisService>();
        var script = Script("ffprobe-hangs", "ping -n 31 127.0.0.1 >nul\r\nexit 0");
        var service = new FfprobeMediaAnalysisService(new Locator(script), log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var result = await service.AnalyzeAsync(SomeFile("c.mp4"), cts.Token);

        Assert.Equal(MediaAnalysisOutcome.Cancelled, result.Outcome);
        Assert.Empty(log.Inner.AtLeast(LogLevel.Warning));
        Assert.Contains(log.Inner.Entries, e => e.Level == LogLevel.Debug && e.Text.Contains("cancelled"));
    }

    [FfmpegFact]
    public async Task The_real_ffprobe_reports_why_a_damaged_file_failed()
    {
        // With the former "-v quiet" ffprobe said nothing; with "-v error" its reason reaches the log.
        var log = new RecordingLogger<FfprobeMediaAnalysisService>();
        var service = new FfprobeMediaAnalysisService(new Locator(FfmpegTools.Ffprobe!), log);
        var damaged = SomeFile("damaged.mp4", Enumerable.Range(0, 4096).Select(i => (byte)(i * 7919 % 251)).ToArray());

        var result = await service.AnalyzeAsync(damaged);

        Assert.Equal(MediaAnalysisOutcome.ProbeProcessFailed, result.Outcome);
        var warning = Assert.Single(log.Inner.AtLeast(LogLevel.Warning));
        Assert.Contains("exited with code", warning.Text);
        Assert.DoesNotContain("(no stderr output)", warning.Text);
        Assert.Contains("damaged.mp4", warning.Text);
    }

    [FfmpegFact]
    public async Task With_v_error_the_real_ffprobe_still_gives_clean_json_and_the_same_metadata()
    {
        var log = new RecordingLogger<FfprobeMediaAnalysisService>();
        var service = new FfprobeMediaAnalysisService(new Locator(FfmpegTools.Ffprobe!), log);
        var file = _media.Get("avsync.mp4");

        var result = await service.AnalyzeAsync(file.Path);

        Assert.Equal(MediaAnalysisOutcome.Success, result.Outcome);
        var m = result.Metadata!;
        // What the file was generated as (TestMedia: 256×144 H.264 at 25 fps, 4 s, a mono 48 kHz AAC click).
        Assert.Equal((256, 144, Core.Common.FrameRate.Fps25, Core.Common.FrameRate.Fps25, "h264", 0),
            (m.Width!.Value, m.Height!.Value, m.FrameRate!.Value, m.AvgFrameRate!.Value, m.VideoCodec!, m.DisplayRotation!.Value));
        Assert.Equal(("aac", 48000, 1), (m.AudioCodec!, m.AudioSampleRate!.Value, m.AudioChannels!.Value));
        Assert.InRange(m.Duration!.TotalSeconds, 3.95, 4.1);
        Assert.Equal(file.Metadata.StartTime, m.StartTime);
        Assert.Empty(log.Inner.AtLeast(LogLevel.Warning));
        Assert.Contains(log.Inner.Entries, e => e.Level == LogLevel.Debug && e.Text.Contains("-v error"));
    }
}
