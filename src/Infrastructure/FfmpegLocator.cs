using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiVideoEditor.Infrastructure;

/// <summary>Resolves ffmpeg: <see cref="FfmpegOptions.FfmpegPath"/>, then PATH. Cached per app run.</summary>
public sealed class FfmpegLocator : IFfmpegLocator
{
    private readonly ExecutableLocator _locator;

    public FfmpegLocator(IOptions<FfmpegOptions> options, ILogger<FfmpegLocator> logger) =>
        _locator = new ExecutableLocator("ffmpeg", () => options.Value.FfmpegPath, logger);

    public Task<string?> GetFfmpegPathAsync(CancellationToken ct = default) => _locator.GetPathAsync(ct);
}
