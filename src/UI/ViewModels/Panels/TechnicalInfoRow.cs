namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>One label/value line in the Inspector's Technical Information section.
/// Rows are only ever added for fields that actually have a value — there is no
/// "N/A" row; inapplicable fields are simply absent.</summary>
public sealed class TechnicalInfoRow
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}
