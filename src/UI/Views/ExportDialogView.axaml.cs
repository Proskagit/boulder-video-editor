using AiVideoEditor.UI.ViewModels;
using Avalonia.Controls;

namespace AiVideoEditor.UI.Views;

/// <summary>The export dialog: «Копировать» puts the YouTube timestamps on the system clipboard.</summary>
public partial class ExportDialogView : UserControl
{
    private ExportDialogViewModel? _viewModel;

    public ExportDialogView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.CopyRequested -= OnCopy;
        _viewModel = DataContext as ExportDialogViewModel;
        if (_viewModel is not null) _viewModel.CopyRequested += OnCopy;
    }

    private async void OnCopy(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
