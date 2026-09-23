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

    public Task<string?> GetFfprobePathAsync(CancellationToken ct = default) => _locator.GetPathAsync(ct);
}
