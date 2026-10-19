using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.UI.Localization;

/// <summary>The media kind as the user reads it (D039 §2): never the enum's name.</summary>
public static class MediaKindText
{
    /// <summary>The short upper-case badge of a Media Browser row ("VIDEO" / "ВИДЕО").</summary>
    public static string Badge(MediaKind kind) => kind switch
    {
        MediaKind.Video => Strings.MediaKind_VideoBadge,
        MediaKind.Audio => Strings.MediaKind_AudioBadge,
        MediaKind.Image => Strings.MediaKind_ImageBadge,
        _ => "?"
    };

    /// <summary>The kind in a sentence or a field ("Video" / "Видео").</summary>
    public static string Name(MediaKind kind) => kind switch
    {
        MediaKind.Video => Strings.MediaKind_Video,
        MediaKind.Audio => Strings.MediaKind_Audio,
        MediaKind.Image => Strings.MediaKind_Image,
        _ => kind.ToString()
    };

    /// <summary>A file picker's filter name for the kind ("Video files" / "Видеофайлы").</summary>
    public static string FileFilter(MediaKind kind) => kind switch
    {
        MediaKind.Video => Strings.FileFilter_Video,
        MediaKind.Audio => Strings.FileFilter_Audio,
        MediaKind.Image => Strings.FileFilter_Image,
        _ => Strings.FileFilter_AllMedia
    };
}
