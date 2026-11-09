using AiVideoEditor.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiVideoEditor.UI.Views;

/// <summary>D057 §3.8: the settings window's content; it opens scrolled to the section asked for.</summary>
public partial class AppSettingsView : UserControl
{
    public AppSettingsView()
    {
        InitializeComponent();
    }

    /// <summary>The section's panel by <paramref name="section"/> (tests, the opening scroll).</summary>
    internal Control SectionPanel(AppSettingsSection section) => section switch
    {
        AppSettingsSection.Ffmpeg => FfmpegSection,
        AppSettingsSection.TimelineAndMarkup => TimelineSection,
        AppSettingsSection.CorosFit => CorosFitSection,
        _ => GeneralSection
    };

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is AppSettingsViewModel { Section: not AppSettingsSection.General } vm)
            SectionPanel(vm.Section).BringIntoView();
    }
}
