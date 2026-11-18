using AiVideoEditor.Core.Interfaces;
using System.ComponentModel;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>D068 item 8: the groups of a clip's Inspector sections, chosen on the panel's rail.</summary>
public enum InspectorCategory
{
    Clip,
    Text,
    People,
    Holds,
    Fit,
    Audio
}

/// <summary>One button of the Inspector's rail: its icon, name and whether the shown clip has it.</summary>
public sealed partial class InspectorCategoryItem : ObservableObject
{
    public InspectorCategoryItem(InspectorCategory category, string title, string glyph, IRelayCommand<InspectorCategory> select)
    {
        Category = category;
        Title = title;
        Glyph = glyph;
        Select = select;
    }

    public InspectorCategory Category { get; }
    public string Title { get; }
    public string Glyph { get; }
    public IRelayCommand<InspectorCategory> Select { get; }

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// D068 item 8, changed by amendment 1 (the owner): a single clip's sections are grouped — Clip (frame, timing, clip), Text /
/// Template, People, Holds, FIT, Sound — and stay one list; the rail on the panel's left edge is navigation only: a button
/// scrolls the list so that its group's first section is at the top, and the button of the group at the top is lit while
/// the user scrolls.
/// </summary>
public sealed partial class InspectorViewModel
{
    private bool _categoriesHooked;
    private IReadOnlyList<InspectorCategoryItem>? _categories;

    /// <summary>The rail's buttons, in their order (the order of the groups in the list).</summary>
    public IReadOnlyList<InspectorCategoryItem> Categories => _categories ??= CreateCategories();

    /// <summary>The group at the top of the list (lit on the rail).</summary>
    [ObservableProperty]
    private InspectorCategory _selectedCategory = InspectorCategory.Clip;

    /// <summary>Asks the view to scroll the list to a group's first section.</summary>
    public event EventHandler<InspectorCategory>? ScrollToCategoryRequested;

    private IReadOnlyList<InspectorCategoryItem> CreateCategories()
    {
        var select = new RelayCommand<InspectorCategory>(ChooseCategory);
        return new[]
        {
            new InspectorCategoryItem(InspectorCategory.Clip, Strings.InspectorRail_Clip, Icons.VideoTrack, select),
            new InspectorCategoryItem(InspectorCategory.Text, Strings.InspectorRail_Text, Icons.Text, select),
            new InspectorCategoryItem(InspectorCategory.People, Strings.InspectorRail_People, Icons.Person, select),
            new InspectorCategoryItem(InspectorCategory.Holds, Strings.InspectorRail_Holds, Icons.Hold, select),
            new InspectorCategoryItem(InspectorCategory.Fit, Strings.InspectorRail_Fit, Icons.Pulse, select),
            new InspectorCategoryItem(InspectorCategory.Audio, Strings.InspectorRail_Audio, Icons.AudioTrack, select)
        };
    }

    /// <summary>A rail button: the list scrolls to the group (nothing is hidden).</summary>
    public void ChooseCategory(InspectorCategory category)
    {
        if (!IsCategoryAvailable(category)) return;
        SelectedCategory = category;
        ScrollToCategoryRequested?.Invoke(this, category);
    }

    /// <summary>The view: <paramref name="category"/> is the group at the top of the list now.</summary>
    public void NoteCategoryAtTop(InspectorCategory category)
    {
        if (IsCategoryAvailable(category)) SelectedCategory = category;
    }

    /// <summary>Whether the shown clip has the group's sections (the same conditions as the sections' own visibility).</summary>
    public bool IsCategoryAvailable(InspectorCategory category) => category switch
    {
        InspectorCategory.Clip => true,
        InspectorCategory.Text => HasTextProperties || HasTemplate,
        InspectorCategory.People => HasPeopleSection,
        InspectorCategory.Holds => _clip is VideoClip && HoldMarkup is { HasClip: true },
        InspectorCategory.Fit => ShowsTraining || ShowsFitMarks || ShowsFitLink,
        InspectorCategory.Audio => HasAudioProperties,
        _ => false
    };

    /// <summary>Called whenever what the clip has may change: which buttons the rail shows.</summary>
    private void RefreshCategories()
    {
        HookCategoryInputs();
        if (!IsCategoryAvailable(SelectedCategory)) SelectedCategory = InspectorCategory.Clip;
        RefreshCategoryItems();
    }

    private void RefreshCategoryItems()
    {
        foreach (var item in Categories)
        {
            item.IsAvailable = IsCategoryAvailable(item.Category);
            item.IsSelected = item.Category == SelectedCategory;
        }
    }

    private static readonly HashSet<string> CategoryInputs = new()
    {
        nameof(HasTextProperties), nameof(HasTemplate), nameof(HasPeopleSection), nameof(ShowsTraining), nameof(ShowsFitMarks),
        nameof(ShowsFitLink), nameof(HasAudioProperties), nameof(IsSingleClipSelected), nameof(HoldMarkup), nameof(TemplateKind)
    };

    private void HookCategoryInputs()
    {
        if (_categoriesHooked) return;
        _categoriesHooked = true;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && CategoryInputs.Contains(name)) RefreshCategories();
        };
    }

    partial void OnSelectedCategoryChanged(InspectorCategory value) => RefreshCategoryItems();

    partial void OnHoldMarkupChanged(HoldMarkupViewModel? oldValue, HoldMarkupViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnHoldMarkupPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnHoldMarkupPropertyChanged;
    }

    private void OnHoldMarkupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HoldMarkupViewModel.HasClip)) RefreshCategories();
    }

    /// <summary>D068 item 18: «Трансформация и кадрирование» back to the defaults — position, scale, rotation, opacity, crop, and
    /// the transform keyframes removed; one undo step.</summary>
    [RelayCommand]
    private void ResetFrame()
    {
        if (_clip is null || RejectWhileLocked()) return;
        var result = _edit.SetClipProperties(_clip.Id, new ClipPropertyChange
        {
            Visual = VisualProperties.Default,
            Keyframes = _clip is VideoClip ? new KeyframeValues(null) : null
        });
        if (!result.Success) _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
    }

    /// <summary>D068 item 18: «Звук» back to the defaults — volume 100 %, not muted; one undo step.</summary>
    [RelayCommand]
    private void ResetAudio()
    {
        if (_clip is null || RejectWhileLocked()) return;
        var result = _edit.SetClipProperties(_clip.Id, new ClipPropertyChange { Audio = new AudioProperties(1, false) });
        if (!result.Success) _status.ReportWarning(result.Message ?? Strings.Inspector_ClipNotChanged);
    }
}
