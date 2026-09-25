namespace AiVideoEditor.Core.Export;

/// <summary>
/// Encodes the finished pictures and sound of an export into the fixed D023 format (<see cref="ExportFormat"/>):
/// MP4, H.264 (CRF 18, preset medium, 8-bit 4:2:0, BT.709 limited range), AAC-LC 48 kHz stereo 192 kbps, the
/// size and exact frame rate of <see cref="ExportOutput"/>. It knows nothing about composition or audio placement:
/// it receives opaque BGRA canvases (<see cref="Composition.ICompositionRasterizer"/>) and the mixed 48 kHz stereo
/// float PCM (the export's audio source). The backend (ffmpeg) stays behind this interface.
/// </summary>
public interface IExportEncoder
{
    /// <summary>
    /// Starts encoding an export of <paramref name="output"/> for <paramref name="destinationPath"/>. Nothing is written
    /// to that path until <see cref="IExportEncoding.CompleteAsync"/> succeeds: the encoding goes to temporary files
    /// next to it, which are moved into place (replacing an existing file) only then. ffmpeg missing →
    /// <see cref="ExportException"/> (<see cref="ExportFailure.EncoderUnavailable"/>).
    /// </summary>
    Task<IExportEncoding> StartAsync(ExportOutput output, string destinationPath, CancellationToken ct = default);
}

/// <summary>
/// One running encoding. Order: the whole audio first — exactly <see cref="ExportOutput.AudioSampleCount"/> stereo
/// frames through <see cref="WriteAudioAsync"/> (silence included: the track always exists) — then exactly
/// <see cref="ExportOutput.FrameCount"/> canvases through <see cref="WriteFrameAsync"/>, then
/// <see cref="CompleteAsync"/>. Calls come from one thread at a time. A backend failure is an
/// <see cref="ExportException"/> (<see cref="ExportFailure.EncodeFailed"/>; moving the result into place:
/// <see cref="ExportFailure.OutputFailed"/>), cancellation an <see cref="OperationCanceledException"/>; a violated
/// order or count is an <see cref="InvalidOperationException"/>. Disposing without a successful
/// <see cref="CompleteAsync"/> aborts: the backend processes end, the temporary files are deleted and the destination
/// is untouched.
/// </summary>
public interface IExportEncoding : IAsyncDisposable
{
    /// <summary>The next stereo frames of the export's audio (48 kHz, interleaved float, already mixed and clamped —
    /// the encoder changes no gain).</summary>
    ValueTask WriteAudioAsync(ReadOnlyMemory<float> interleaved, CancellationToken ct = default);

    /// <summary>The next canvas: <c>Width × Height</c> BGRA pixels (byte order B, G, R, A; opaque), rows
    /// <paramref name="stride"/> bytes apart, top to bottom. The first frame ends the audio.</summary>
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, int stride, CancellationToken ct = default);

    /// <summary>Ends the input, waits for the encoder, checks that it succeeded and moves the result to the destination.</summary>
    Task CompleteAsync(CancellationToken ct = default);
}
