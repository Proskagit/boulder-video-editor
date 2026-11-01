using System.Text.RegularExpressions;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>
/// D049 §1: the training, the recording-time tag and the alignment never read the editor's time zone or clock — W and C
/// are UTC instants, L is converted only with an explicit offset. Fails on the forbidden APIs in the guarded sources.
/// </summary>
public sealed partial class SystemTimeGuardTests
{
    private static readonly string[] Guarded =
    {
        "src/Core/Telemetry",                                  // every file of the folder
        "src/Media/TrainingImportService.cs",
        "src/Media/FileFingerprintService.cs",
        "src/Project/Research",                                // the COROS synchronisation study
        "src/Video/FfprobeMediaAnalysisService.cs",            // reads the creation_time tag
        "src/Video/FfprobeOutput.cs",
        "src/UI/Services/TrainingImportWorkflow.cs",
        "src/UI/Services/TrainingSessionService.cs",
        "src/UI/Localization/TrainingText.cs",
        "src/UI/ViewModels/Panels/InspectorViewModel.Training.cs",
    };

    [GeneratedRegex(@"TimeZoneInfo\.Local\b|DateTime\.Now\b|DateTimeOffset\.Now\b|DateTime\.Today\b|\.ToLocalTime\(|\.LocalDateTime\b")]
    private static partial Regex Forbidden();

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AiVideoEditor.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (AiVideoEditor.sln) not found.");
    }

    [Fact]
    public void The_guarded_sources_never_use_the_system_time_zone_or_clock()
    {
        var root = Root();
        var files = Guarded.SelectMany(g =>
        {
            var path = Path.Combine(root, g);
            return Directory.Exists(path) ? Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories) : new[] { path };
        }).ToList();

        Assert.All(files, f => Assert.True(File.Exists(f), $"guarded source missing: {f}"));
        var hits = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Forbidden().IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0, "System time used (D049 §1):\n" + string.Join("\n", hits));
    }
}
