namespace AiVideoEditor.Export;

/// <summary>
/// Offline export orchestration (Phase 8, D023): renders an ExportJob's snapshot frame by frame
/// with the Core composition rules and hands frames and audio to an encoder backend
/// (<see cref="ExportService"/>). No FFmpeg or UI types here: the encoder and the rasterizer
/// come in through Core interfaces.
/// </summary>
internal static class ModuleInfo
{
    public const string Name = "Export";
}
