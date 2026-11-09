using AiVideoEditor.UI.ViewModels;
using Avalonia.Controls;

namespace AiVideoEditor.UI.Views;

/// <summary>D057 §3.9: «Копировать» puts the timestamps on the system clipboard.</summary>
public partial class YouTubeTimestampsView : UserControl
{
    private YouTubeTimestampsViewModel? _viewModel;

    public YouTubeTimestampsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) _viewModel.CopyRequested -= OnCopy;
        _viewModel = DataContext as YouTubeTimestampsViewModel;
        if (_viewModel is not null) _viewModel.CopyRequested += OnCopy;
    }

    private async void OnCopy(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}
