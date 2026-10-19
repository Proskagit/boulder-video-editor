using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiVideoEditor.App.Composition;

/// <summary>The application's host builder. D040 §1 (FU-24.2): the content root — the folder <c>appsettings.json</c> is
/// read from — is the application's folder, never the process's current directory, so the app started from another folder
/// (a shortcut's «Start in», a console elsewhere) still reads <c>App:Language</c> and <c>Ffmpeg:*</c>.</summary>
public static class AppHostBuilder
{
    /// <param name="args">The command-line arguments, also a configuration source.</param>
    /// <param name="applicationFolder">The folder of <c>appsettings.json</c>; <see cref="AppContext.BaseDirectory"/>
    /// (the exe's folder) unless a test gives another.</param>
    public static HostApplicationBuilder Create(string[] args, string? applicationFolder = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = applicationFolder ?? AppContext.BaseDirectory,
        });

        // Bound from the "Ffmpeg" section of appsettings.json — see FfprobeLocator.
        builder.Services.Configure<FfmpegOptions>(builder.Configuration.GetSection("Ffmpeg"));

        return builder;
    }
}
