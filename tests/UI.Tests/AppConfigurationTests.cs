using AiVideoEditor.App.Composition;
using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>The tests change the process's current directory, so they run alone, after every parallel collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "Current directory";
}

/// <summary>D040 §1 (FU-24.2): <c>appsettings.json</c> is read from the application's folder, never from the process's
/// current directory — started from another folder the app still reads <c>App:Language</c> and <c>Ffmpeg:*</c>.</summary>
[Collection(CurrentDirectoryCollection.Name)]
public sealed class AppConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aive-config-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDirectory = Environment.CurrentDirectory;

    public AppConfigurationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousDirectory;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Folder(string name, string language, string? ffmpeg, string? ffprobe)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        static string Json(string? value) => value is null ? "null" : "\"" + value.Replace("\\", "\\\\") + "\"";
        File.WriteAllText(Path.Combine(folder, "appsettings.json"),
            $$"""{ "App": { "Language": "{{language}}" }, "Ffmpeg": { "FfmpegPath": {{Json(ffmpeg)}}, "FfprobePath": {{Json(ffprobe)}} } }""");
        return folder;
    }

    [Fact]
    public void The_application_folder_is_read_although_the_current_directory_has_another_file()
    {
        var app = Folder("app", "en", @"C:\tools\ffmpeg.exe", @"C:\tools\ffprobe.exe");
        Environment.CurrentDirectory = Folder("elsewhere", "xx", @"D:\other\ffmpeg.exe", null);

        var builder = AppHostBuilder.Create([], app);
        using var host = builder.Build();

        Assert.Equal("en", builder.Configuration["App:Language"]);
        var ffmpeg = host.Services.GetRequiredService<IOptions<FfmpegOptions>>().Value;
        Assert.Equal(@"C:\tools\ffmpeg.exe", ffmpeg.FfmpegPath);
        Assert.Equal(@"C:\tools\ffprobe.exe", ffmpeg.FfprobePath);
    }

    [Fact]
    public void Without_a_folder_the_content_root_is_the_application_base_directory_not_the_current_directory()
    {
        Environment.CurrentDirectory = Folder("elsewhere", "xx", null, null);

        var builder = AppHostBuilder.Create([]);

        Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath));
        // The app's own appsettings.json is copied next to this test assembly: its language, not the current directory's.
        Assert.Equal("ru", builder.Configuration["App:Language"]);
    }

    [Fact]
    public void Command_line_arguments_reach_the_configuration()
    {
        var app = Folder("app", "ru", null, null);

        var builder = AppHostBuilder.Create(["--App:Language=en"], app);

        Assert.Equal("en", builder.Configuration["App:Language"]);
    }
}
