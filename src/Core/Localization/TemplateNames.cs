using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Localization;

/// <summary>The templates' names in the interface's language (D045 §5): REC, VHS, Боулдер, Хайлайт, Пульс, Итоги (D073).</summary>
public static class TemplateNames
{
    public static string Of(TemplateKind kind) => kind switch
    {
        TemplateKind.Rec => Messages.Template_Rec,
        TemplateKind.Vhs => Messages.Template_Vhs,
        TemplateKind.Boulder => Messages.Template_Boulder,
        TemplateKind.HeartRate => Messages.Template_HeartRate,
        TemplateKind.Summary => Messages.Template_Summary,
        _ => Messages.Template_Highlight
    };
}
