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
/// D068 item 8 (as the inspectors of CapCut, DaVinci Resolve and Insta360 Studio): a single clip's sections are grouped —
/// Clip (frame, timing, clip), Text / Template, People, Holds, FIT, Sound — and the rail on the panel's edge shows one group
/// at a time instead of one long list. A video clip opens on «Зацепки»; a group the user chose stays chosen for the next clip
/// that has it (spatial stability), otherwise the clip's own default.
/// </summary>
public sealed partial class InspectorViewModel
{
    private InspectorCategory? _chosenCategory;
    private bool _categoriesHooked;

    private IReadOnlyList<InspectorCategoryItem>? _categories;

    /// <summary>The rail's buttons, in their order.</summary>
    public IReadOnlyList<InspectorCategoryItem> Categories => _categories ??= CreateCategories();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsClipCategory), nameof(ShowsTextCategory), nameof(ShowsPeopleCategory),
        nameof(ShowsHoldsCategory), nameof(ShowsFitCategory), nameof(ShowsAudioCategory))]
    private InspectorCategory _selectedCategory = InspectorCategory.Clip;

    public bool ShowsClipCategory => SelectedCategory == InspectorCategory.Clip;
    public bool ShowsTextCategory => SelectedCategory == InspectorCategory.Text;
    public bool ShowsPeopleCategory => SelectedCategory == InspectorCategory.People;
    public bool ShowsHoldsCategory => SelectedCategory == InspectorCategory.Holds;
    public bool ShowsFitCategory => SelectedCategory == InspectorCategory.Fit;
    public bool ShowsAudioCategory => SelectedCategory == InspectorCategory.Audio;

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

    /// <summary>A rail button: the user's choice, kept for the next clips that have the group.</summary>
    public void ChooseCategory(InspectorCategory category)
    {
        _chosenCategory = category;
        SelectedCategory = category;
        RefreshCategoryItems();
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

    private InspectorCategory DefaultCategory() =>
        IsCategoryAvailable(InspectorCategory.Holds) ? InspectorCategory.Holds
        : IsCategoryAvailable(InspectorCategory.Text) ? InspectorCategory.Text
        : _clip is AudioClip ? InspectorCategory.Audio
        : InspectorCategory.Clip;

    /// <summary>Called whenever what the clip has may change: keeps the chosen group if the clip has it, else its default.</summary>
    private void RefreshCategories()
    {
        HookCategoryInputs();
        var target = _chosenCategory is { } chosen && IsCategoryAvailable(chosen) ? chosen : DefaultCategory();
        if (!IsCategoryAvailable(SelectedCategory) || SelectedCategory != target) SelectedCategory = target;
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
}
