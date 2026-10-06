using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiVideoEditor.Infrastructure;

/// <summary>Resolves ffprobe: <see cref="FfmpegOptions.FfprobePath"/>, then PATH. Cached per app run.</summary>
public sealed class FfprobeLocator : IFfprobeLocator
{
    private readonly ExecutableLocator _locator;

    public FfprobeLocator(IOptions<FfmpegOptions> options, ILogger<FfprobeLocator> logger) =>
        _locator = new ExecutableLocator("ffprobe", () => options.Value.FfprobePath, logger);

    /// <summary>Test seam: the PATH probe with <paramref name="probeTimeout"/> instead of the app's limit.</summary>
    internal FfprobeLocator(IOptions<FfmpegOptions> options, ILogger<FfprobeLocator> logger, TimeSpan probeTimeout) =>
        _locator = new ExecutableLocator("ffprobe", () => options.Value.FfprobePath, logger, probeTimeout);

    /// <summary>The limit of this locator's PATH probe.</summary>
    internal TimeSpan ProbeTimeout => _locator.ProbeTimeout;

    public Task<string?> GetFfprobePathAsync(CancellationToken ct = default) => _locator.GetPathAsync(ct);
}
