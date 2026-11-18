using System.Collections.ObjectModel;
using AiVideoEditor.UI.Help;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>D068 §3: the help window — the topics on the left (filtered by the search text), the chosen one on the right.</summary>
public sealed partial class HelpViewModel : ViewModelBase
{
    private readonly IReadOnlyList<HelpTopic> _all;

    public HelpViewModel() : this(HelpDocument.LoadForCurrentLanguage()) { }

    public HelpViewModel(IReadOnlyList<HelpTopic> topics)
    {
        _all = topics;
        foreach (var topic in topics) Topics.Add(topic);
        _selectedTopic = Topics.FirstOrDefault();
    }

    /// <summary>The topics shown in the list: all, or those that contain <see cref="SearchText"/>.</summary>
    public ObservableCollection<HelpTopic> Topics { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private HelpTopic? _selectedTopic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingFound))]
    private string _searchText = "";

    public bool HasSelection => SelectedTopic is not null;

    public bool NothingFound => Topics.Count == 0;

    /// <summary>Opens the topic whose title is <paramref name="title"/>, if there is one (clears the search).</summary>
    public void Show(string title)
    {
        var topic = _all.FirstOrDefault(t => t.Title == title);
        if (topic is null) return;
        SearchText = "";
        SelectedTopic = topic;
    }

    partial void OnSearchTextChanged(string value)
    {
        var query = value.Trim();
        var shown = query.Length == 0 ? _all : _all.Where(t => t.Matches(query)).ToList();
        var keep = SelectedTopic is not null && shown.Contains(SelectedTopic) ? SelectedTopic : shown.FirstOrDefault();
        Topics.Clear();
        foreach (var topic in shown) Topics.Add(topic);
        SelectedTopic = keep;
        OnPropertyChanged(nameof(NothingFound));
    }
}
