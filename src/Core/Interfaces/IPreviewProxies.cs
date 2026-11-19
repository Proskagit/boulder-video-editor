using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>D071 §3: what a proxy is made from and into.</summary>
public sealed record ProxyBuildRequest
{
    public required string SourcePath { get; init; }

    /// <summary>The proxy file to write (the builder writes it whole or not at all).</summary>
    public required string OutputPath { get; init; }

    /// <summary>The proxy's picture (<see cref="PreviewDecoding.ProxySize"/>).</summary>
    public required FrameSize Size { get; init; }

    /// <summary>The source's duration, for the progress only.</summary>
    public MediaTime Duration { get; init; }

    /// <summary>The D033 conversion of an HDR source: the proxy is SDR (the Preview's picture), decoded without it.</summary>
    public HdrConversion? Hdr { get; init; }
}

/// <summary>
/// D071 §3: makes a proxy — a small H.264 file of the source's picture with a key frame every half second and, frame for
/// frame, the source's own timestamps, checked after the encoding (a proxy whose frames differ from the source's is
/// never kept: the Preview would show other frames than the export).
/// </summary>
public interface IProxyBuilder
{
    /// <summary>Makes the proxy. <paramref name="progress"/> gets 0…1. Throws <see cref="ProxyBuildException"/> when it
    /// could not be made or checked (nothing is left at <see cref="ProxyBuildRequest.OutputPath"/>), or
    /// <see cref="OperationCanceledException"/>.</summary>
    Task BuildAsync(ProxyBuildRequest request, IProgress<double>? progress = null, CancellationToken ct = default);
}

public sealed class ProxyBuildException : Exception
{
    public ProxyBuildException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// D071 §3: the proxies of every project in one folder of the application's cache
/// (<c>%LOCALAPPDATA%\BoulderVideoEditor\cache\proxies</c>), one per file (1080p, D071 amendment 2), keyed by the source's content — the same
/// file in two projects has one proxy; an edited or replaced file has another key.
/// </summary>
public interface IProxyStore
{
    string Folder { get; }

    /// <summary>The ready proxy of <paramref name="asset"/>, or null.</summary>
    string? TryGet(MediaAsset asset);

    /// <summary>Makes the proxy unless it is there; returns its path, or null when the source can't be read.</summary>
    Task<string?> GetOrCreateAsync(MediaAsset asset, IProgress<double>? progress, CancellationToken ct);

    /// <summary>The bytes the proxies take now.</summary>
    long SizeBytes();

    /// <summary>Removes every proxy that is not being read now; returns the bytes freed. Never throws.</summary>
    long Clear();
}
