using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Localization;

/// <summary>The templates' names in the interface's language (D045 §5): REC, VHS, Боулдер, Хайлайт.</summary>
public static class TemplateNames
{
    public static string Of(TemplateKind kind) => kind switch
    {
        TemplateKind.Rec => Messages.Template_Rec,
        TemplateKind.Vhs => Messages.Template_Vhs,
        TemplateKind.Boulder => Messages.Template_Boulder,
        _ => Messages.Template_Highlight
    };
}
