using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia.Controls;

namespace AiVideoEditor.UI.Views.Panels;

public partial class ToolbarView : UserControl
{
    private RecentProjectsViewModel? _recent;

    public ToolbarView()
    {
        InitializeComponent();
        // Recent ▾: the list is read again each time the drop-down opens; choosing a project closes it.
        var flyout = RecentButton.Flyout!;
        flyout.Opened += async (_, _) =>
        {
            if (_recent is { } recent) await recent.OnOpenedAsync();
        };
        flyout.Closed += (_, _) => _recent?.OnClosed();
        DataContextChanged += (_, _) => Attach((DataContext as ToolbarViewModel)?.Recent);
    }

    private void Attach(RecentProjectsViewModel? recent)
    {
        if (ReferenceEquals(recent, _recent)) return;
        if (_recent is not null) _recent.CloseRequested -= OnCloseRequested;
        _recent = recent;
        if (_recent is not null) _recent.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => RecentButton.Flyout?.Hide();
}
