using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// ffmpeg and ping under names no other process has — hard links (a copy on another volume) named for this test run — for
/// the tests that check that no process they started is left running: they look for these names only, so an ffmpeg that
/// another test assembly or program runs at the same time is never taken for theirs (Phase 30 Step 30.4). The tests run
/// their scripts' ffmpeg and ping through these paths; a process they leave behind is still found by its name.
/// </summary>
internal static class ProcessAliases
{
    private static readonly Lazy<(string? Ffmpeg, string Ping)> Paths = new(Create);

    /// <summary>The real ffmpeg under this run's name (null without ffmpeg).</summary>
    public static string? Ffmpeg => Paths.Value.Ffmpeg;

    /// <summary>ping under this run's name.</summary>
    public static string Ping => Paths.Value.Ping;

    /// <summary>The processes of <see cref="Ffmpeg"/> and <see cref="Ping"/> started since <paramref name="since"/> (one
    /// second of slack) and still alive, as "name#id".</summary>
    public static List<string> AliveSince(DateTime since) =>
        new[] { Ffmpeg, Ping }.OfType<string>()
            .SelectMany(path => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            .Where(p => { try { return !p.HasExited && p.StartTime >= since.AddSeconds(-1); } catch { return false; } })
            .Select(p => $"{p.ProcessName}#{p.Id}").ToList();

    private static (string? Ffmpeg, string Ping) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aive-process-aliases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        var id = Path.GetFileName(dir)[..12];
        var ffmpeg = FfmpegTools.Ffmpeg is { } path ? Alias(path, Path.Combine(dir, $"aive-ffmpeg-{id}.exe")) : null;
        return (ffmpeg, Alias(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(dir, $"aive-ping-{id}.exe")));
    }

    private static string Alias(string source, string alias)
    {
        // A bare name ("ffmpeg" found on PATH) is looked up on PATH; a linked executable (winget's ffmpeg) would run
        // under its target's name: link the target itself.
        if (!Path.IsPathRooted(source))
        {
            var file = Path.HasExtension(source) ? source : source + ".exe";
            source = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => Path.Combine(folder, file)).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException($"{file} not found on PATH");
        }
        source = new FileInfo(source).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? source;
        if (!CreateHardLink(alias, source, IntPtr.Zero)) File.Copy(source, alias);
        return alias;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
