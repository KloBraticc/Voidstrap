using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Voidstrap.Integrations.Plugins;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class HomePage
{
    private static readonly string[] RobloxSectionKeys = ["hero", "news", "catalog", "continue"];

    private static readonly string[] StudioSectionKeys =
        ["studioFavorites", "studioRecent", "studioOwned", "studioFiles", "studioTemplates"];

    private Border? _dropSection;

    private Border? _dragSection;

    private Point _dragStart;

    private static string[] AllSectionKeys => [.. RobloxSectionKeys, .. StudioSectionKeys];

    private static List<string> HiddenSections => App.Settings.Prop.HomeHiddenSections ??= new List<string>();

    private static bool IsSectionKey(object? tag) => tag is string key && AllSectionKeys.Contains(key);

    private static Border? FindSection(DependencyObject start)
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is Border { Tag: string key } section && AllSectionKeys.Contains(key))
                return section;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private Border? SectionByKey(string? key) =>
        key is null
            ? null
            : LayoutSections().FirstOrDefault(section => section.Tag as string == key);

    private IEnumerable<Border> LayoutSections() =>
        RobloxSections.Children.OfType<Border>().Concat(StudioSections.Children.OfType<Border>())
            .Where(section => IsSectionKey(section.Tag));

    private static string SectionDisplayName(string key) =>
        key switch
        {
            "hero" => Strings.Ui_Home_SectionHero,
            "news" => Strings.Ui_Home_SectionNews,
            "catalog" => Strings.Ui_Home_SectionCatalog,
            "continue" => Strings.Ui_Home_SectionContinue,
            "studioFavorites" => Strings.Ui_Home_SectionStudioFavorites,
            "studioRecent" => Strings.Ui_Home_SectionStudioRecent,
            "studioOwned" => Strings.Ui_Home_SectionStudioOwned,
            "studioFiles" => Strings.Ui_Home_SectionStudioFiles,
            "studioTemplates" => Strings.Ui_Home_SectionStudioTemplates,
            _ => key,
        };

    private void WireLayoutSections()
    {
        foreach (Border section in LayoutSections())
        {
            section.DragOver += Section_DragOver;
            section.DragLeave += Section_DragLeave;
            section.Drop += Section_Drop;
        }
    }

    private void UnwireLayoutSections()
    {
        foreach (Border section in LayoutSections())
        {
            section.DragOver -= Section_DragOver;
            section.DragLeave -= Section_DragLeave;
            section.Drop -= Section_Drop;
        }
    }

    private void ApplyHomeLayout()
    {
        ApplySectionOrder(RobloxSections, RobloxSectionKeys, App.Settings.Prop.HomeSectionOrder);
        ApplySectionOrder(StudioSections, StudioSectionKeys, App.Settings.Prop.HomeStudioSectionOrder);
        RefreshHiddenTrays();
    }

    private static void ApplySectionOrder(Panel root, string[] defaults, List<string>? saved)
    {
        List<Border> sections = root.Children.OfType<Border>()
            .Where(section => section.Tag is string key && defaults.Contains(key))
            .ToList();
        if (sections.Count == 0)
            return;
        Dictionary<string, int> ranks = (saved ?? new List<string>())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Select((key, index) => (key, index))
            .ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
        List<Border> ordered = defaults
            .Select((key, index) => (key, index))
            .OrderBy(pair => ranks.TryGetValue(pair.key, out int rank) ? rank : int.MaxValue)
            .ThenBy(pair => pair.index)
            .Select(pair => sections.FirstOrDefault(section => (string?)section.Tag == pair.key))
            .OfType<Border>()
            .ToList();
        List<string> hidden = HiddenSections;
        int insertAt = root.Children.IndexOf(sections[0]);
        foreach (Border section in sections)
            root.Children.Remove(section);
        foreach (Border section in ordered)
        {
            string key = (string)section.Tag;
            section.Visibility = hidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
            root.Children.Insert(Math.Min(insertAt, root.Children.Count), section);
            insertAt++;
        }
        DetachModHosts(root);
    }

    private static void DetachModHosts(Panel root)
    {
        List<UIElement> hosts = root.Children.OfType<UIElement>()
            .Where(element => element is FrameworkElement { Tag: string tag } && tag.StartsWith("mod:", StringComparison.Ordinal))
            .ToList();
        foreach (UIElement host in hosts)
            root.Children.Remove(host);
    }

    private void SaveLayout()
    {
        App.Settings.Prop.HomeSectionOrder = RobloxSections.Children.OfType<Border>()
            .Where(section => section.Tag is string key && RobloxSectionKeys.Contains(key))
            .Select(section => (string)section.Tag)
            .ToList();
        App.Settings.Prop.HomeStudioSectionOrder = StudioSections.Children.OfType<Border>()
            .Where(section => section.Tag is string key && StudioSectionKeys.Contains(key))
            .Select(section => (string)section.Tag)
            .ToList();
        App.Settings.SaveDeferred();
    }

    private void RefreshHiddenTrays()
    {
        List<string> hidden = HiddenSections;
        PopulateHiddenTray(RobloxHiddenTray, RobloxHiddenChips, RobloxSectionKeys, hidden);
        PopulateHiddenTray(StudioHiddenTray, StudioHiddenChips, StudioSectionKeys, hidden);
    }

    private void PopulateHiddenTray(Border tray, WrapPanel chips, string[] keys, List<string> hidden)
    {
        chips.Children.Clear();
        foreach (string key in keys)
        {
            if (!hidden.Contains(key))
                continue;
            Wpf.Ui.Controls.Button chip = new()
            {
                Content = "+ " + SectionDisplayName(key),
                Appearance = ControlAppearance.Secondary,
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = Strings.Ui_Home_ShowSectionTooltip,
                Tag = key,
            };
            chip.Click += TrayChip_Click;
            chips.Children.Add(chip);
        }
        tray.Visibility = chips.Children.Count > 0 && _viewModel.IsLayoutEditMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditLayout_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsLayoutEditMode = !_viewModel.IsLayoutEditMode;
        RefreshHiddenTrays();
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Prop.HomeSectionOrder = new List<string>();
        App.Settings.Prop.HomeStudioSectionOrder = new List<string>();
        App.Settings.Prop.HomeHiddenSections = new List<string>();
        App.Settings.SaveDeferred();
        ApplyHomeLayout();
        PluginAnchors.ApplyAll();
    }

    private void SectionHide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;
        Border? section = FindSection(element);
        if (section?.Tag is not string key)
            return;
        List<string> hidden = HiddenSections;
        if (!hidden.Contains(key))
            hidden.Add(key);
        App.Settings.SaveDeferred();
        ApplyHomeLayout();
        PluginAnchors.ApplyAll();
    }

    private void TrayChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key })
            return;
        HiddenSections.Remove(key);
        App.Settings.SaveDeferred();
        ApplyHomeLayout();
        PluginAnchors.ApplyAll();
    }

    private void SectionGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement grip)
            return;
        _dragSection = FindSection(grip);
        _dragStart = e.GetPosition(this);
    }

    private void SectionGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSection == null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragSection = null;
            return;
        }
        Point current = e.GetPosition(this);
        if (
            Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance
        )
            return;
        const string LOG_IDENT = "HomePage::SectionDrag";
        Border section = _dragSection;
        _dragSection = null;
        try
        {
            section.Opacity = 0.55;
            DragDrop.DoDragDrop(section, (string)section.Tag, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The section could not be dragged: " + ex.GetType().Name);
        }
        finally
        {
            section.Opacity = 1.0;
            SetDropHighlight(null);
        }
    }

    private bool IsSectionDrag(DragEventArgs e, Border target)
    {
        if (!e.Data.GetDataPresent(DataFormats.StringFormat))
            return false;
        string? key = e.Data.GetData(DataFormats.StringFormat) as string;
        return key != null && AllSectionKeys.Contains(key) && !ReferenceEquals(SectionByKey(key), target);
    }

    private void Section_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Border target || !IsSectionDrag(e, target))
            return;
        e.Effects = DragDropEffects.Move;
        SetDropHighlight(target);
        AutoScrollSectionDrag(e, target);
        e.Handled = true;
    }

    private void Section_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border target && ReferenceEquals(_dropSection, target))
            SetDropHighlight(null);
    }

    private void Section_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(null);
        if (sender is not Border target || !IsSectionDrag(e, target))
            return;
        string? key = e.Data.GetData(DataFormats.StringFormat) as string;
        Border? source = SectionByKey(key);
        if (source == null || source.Parent is not Panel root || !ReferenceEquals(target.Parent, root))
            return;
        Point point = e.GetPosition(target);
        int from = root.Children.IndexOf(source);
        int to = root.Children.IndexOf(target);
        if (point.Y >= target.ActualHeight / 2)
            to++;
        if (from < to)
            to--;
        root.Children.Remove(source);
        root.Children.Insert(Math.Clamp(to, 0, root.Children.Count), source);
        SaveLayout();
        DetachModHosts(root);
        PluginAnchors.ApplyAll();
        e.Handled = true;
    }

    private void SetDropHighlight(Border? target)
    {
        if (ReferenceEquals(_dropSection, target))
            return;
        if (_dropSection != null)
            _dropSection.Opacity = 1.0;
        _dropSection = target;
        if (_dropSection != null)
            _dropSection.Opacity = 0.7;
    }

    private void AutoScrollSectionDrag(DragEventArgs e, Border target)
    {
        ScrollViewer? scrollViewer = FindAncestor<ScrollViewer>(target);
        if (scrollViewer == null)
            return;
        Point position = e.GetPosition(scrollViewer);
        if (position.Y < 48)
            scrollViewer.LineUp();
        else if (position.Y > scrollViewer.ViewportHeight - 48)
            scrollViewer.LineDown();
    }

    private static T? FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            current = VisualTreeHelper.GetParent(current);
            if (current is T match)
                return match;
        }
        return null;
    }
}
