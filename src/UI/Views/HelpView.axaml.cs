using AiVideoEditor.UI.ViewModels;
using Avalonia.Controls;

namespace AiVideoEditor.UI.Views;

/// <summary>D068 §3: the help window's content; a new topic opens scrolled to its top.</summary>
public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is HelpViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(HelpViewModel.SelectedTopic)) TopicScroll.Offset = default;
                };
        };
    }
}
