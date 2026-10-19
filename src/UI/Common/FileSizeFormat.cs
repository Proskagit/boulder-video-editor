using AiVideoEditor.UI.Localization;
namespace AiVideoEditor.UI.Common;

/// <summary>Formats a byte count for display, e.g. "842 MB". Binary (1024-based)
/// units labeled the way most file managers show them.</summary>
public static class FileSizeFormat
{
    private static string[] Units => new[] { Strings.Unit_B, Strings.Unit_KB, Strings.Unit_MB, Strings.Unit_GB, Strings.Unit_TB };

    public static string ToShortString(long bytes)
    {
        if (bytes <= 0) return "0 " + Strings.Unit_B;

        var order = 0;
        var size = (double)bytes;
        var units = Units;
        while (size >= 1024 && order < units.Length - 1)
        {
            size /= 1024;
            order++;
        }

        // Whole numbers for KB and up (matches the "842 MB" style in the spec);
        // bytes themselves are always shown as whole numbers too.
        return order == 0 ? $"{bytes} {units[order]}" : $"{size:0} {units[order]}";
    }
}
