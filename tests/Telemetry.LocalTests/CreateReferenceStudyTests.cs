using AiVideoEditor.Media;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>A test that runs only when <c>AIVE_COROS_CREATE_STUDY</c> names the study file to create — an explicit request;
/// otherwise it is skipped and creates nothing.</summary>
public sealed class CreateStudyFactAttribute : FactAttribute
{
    public CreateStudyFactAttribute() => Skip = SkipReason(Environment.GetEnvironmentVariable(ReferenceStudyCreator.OutputVariable));

    public static string? SkipReason(string? output) => string.IsNullOrWhiteSpace(output)
        ? $"Creates the reference study of Step 35.3R2 only on request: set {ReferenceStudyCreator.OutputVariable} to the new *.syncstudy.json " +
          $"and {ReferenceStudyCreator.ProjectVariable} to the set's project folder."
        : null;
}

/// <summary>
/// The tool of Step 35.3R2: creates the reference study of the owner's set of 2026-09-27 — the FIT and its 16 videos,
/// hashed in full, with the reference marking rule, not frozen — at the path given in <c>AIVE_COROS_CREATE_STUDY</c>, for
/// the project in <c>AIVE_COROS_PROJECT</c>. Run explicitly:
/// <code>
/// $env:AIVE_COROS_CREATE_STUDY = "&lt;set folder&gt;\Болдеринг20260927100239.syncstudy.json"
/// $env:AIVE_COROS_PROJECT = "&lt;the set's project folder&gt;"
/// dotnet test tests/Telemetry.LocalTests/Telemetry.LocalTests.csproj --filter "FullyQualifiedName~CreateReferenceStudy"
/// </code>
/// An existing file is never overwritten; the study file is personal data and never committed.
/// </summary>
public class CreateReferenceStudyTests
{
    private readonly ITestOutputHelper _output;

    public CreateReferenceStudyTests(ITestOutputHelper output) => _output = output;

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AiVideoEditor.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root (AiVideoEditor.sln) not found.");
    }

    [CreateStudyFact]
    public async Task CreateReferenceStudy()
    {
        var output = Environment.GetEnvironmentVariable(ReferenceStudyCreator.OutputVariable)!;
        var project = Environment.GetEnvironmentVariable(ReferenceStudyCreator.ProjectVariable);
        Assert.False(string.IsNullOrWhiteSpace(project), $"Set {ReferenceStudyCreator.ProjectVariable} to the set's project folder (holding project.json).");

        // Refuse a bad or existing path before the set is probed or hashed.
        var repository = RepositoryRoot();
        if (ReferenceStudyCreator.PathProblem(output, repository, project!) is { } problem) throw new ReferenceStudyRefusedException(problem);

        var fitPath = RealSet.FitPath;
        var workout = await RealSet.WorkoutAsync(fitPath);
        var videos = await RealSet.ProbeAsync(RealSet.VideoPaths(fitPath));
        var projectAssets = await ReferenceStudyCreator.ProjectAssetsAsync(project!);

        var created = await ReferenceStudyCreator.CreateAsync(output, fitPath, workout, videos, project!, projectAssets, repository,
            new FileFingerprintService(), () => DateTimeOffset.UtcNow);

        _output.WriteLine($"Study: {created.Path}");
        _output.WriteLine($"Videos: {created.Study.Draft.Videos.Count}; climbs: {created.Study.Draft.Fit.Climbs.Count}");
        _output.WriteLine($"Hashing: {created.HashTime.TotalSeconds:F1} s");
        _output.WriteLine($"Content hash: {created.ContentHash}");
        foreach (var check in created.Checks) _output.WriteLine($"  ✓ {check}");
    }

    [Fact]
    public void Without_the_variable_the_tool_is_skipped()
    {
        Assert.NotNull(CreateStudyFactAttribute.SkipReason(null));
        Assert.NotNull(CreateStudyFactAttribute.SkipReason("  "));
        Assert.Null(CreateStudyFactAttribute.SkipReason(@"E:\x.syncstudy.json"));
    }
}
