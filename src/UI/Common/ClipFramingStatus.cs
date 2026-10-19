using System.Globalization;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.Common;

/// <summary>The status line after "Fill frame" / "Fit in frame" (D041 §2), the same from the Inspector and the clip's context
/// menu: the service's own message (a refusal, the value already there), or the scale the clip now has.</summary>
internal static class ClipFramingStatus
{
    public static string Text(TimelineEditResult result, ClipFraming framing, Clip clip)
    {
        if (result.Message is { } message) return message;
        if (!result.Success || VisualProperties.Of(clip) is not { } visual) return Strings.Inspector_ClipNotChanged;
        var percent = (visual.Scale * 100).ToString("0.##", CultureInfo.CurrentCulture);
        return string.Format(framing == ClipFraming.Fill ? Strings.Frame_Filled : Strings.Frame_Fitted, percent);
    }
}
