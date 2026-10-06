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

    /// <summary>Test seam: the PATH probe with <paramref name="probeTimeout"/> instead of the app's limit.</summary>
    internal FfmpegLocator(IOptions<FfmpegOptions> options, ILogger<FfmpegLocator> logger, TimeSpan probeTimeout) =>
        _locator = new ExecutableLocator("ffmpeg", () => options.Value.FfmpegPath, logger, probeTimeout);

    /// <summary>The limit of this locator's PATH probe.</summary>
    internal TimeSpan ProbeTimeout => _locator.ProbeTimeout;

    public Task<string?> GetFfmpegPathAsync(CancellationToken ct = default) => _locator.GetPathAsync(ct);
}
