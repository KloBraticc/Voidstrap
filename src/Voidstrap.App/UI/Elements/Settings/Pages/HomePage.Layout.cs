using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Voidstrap.Integrations.Plugins;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class HomePage
{
    private static readonly string[] RobloxSectionKeys = ["featured", "discover", "continue"];

    private static readonly string[] StudioSectionKeys =
        ["studioFavorites", "studioRecent", "studioOwned", "studioFiles", "studioTemplates"];

    private const int MaxLayoutUndo = 30;

    private readonly Stack<(List<string> Order, List<string> StudioOrder, List<string> Hidden)> _layoutUndo = new();

    private Border? _dropSection;

    private bool _dropAfter;

    private bool _dragging;

    private bool _endingDrag;

    private DropLineAdorner? _dropAdorner;

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
            "featured" => Strings.Ui_Home_SectionFeatured,
            "discover" => Strings.Ui_Home_SectionDiscover,
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

    private void ApplyHomeLayout()
    {
        ApplySectionOrder(RobloxSections, RobloxSectionKeys, App.Settings.Prop.HomeSectionOrder);
        ApplySectionOrder(StudioSections, StudioSectionKeys, App.Settings.Prop.HomeStudioSectionOrder);
        RefreshHiddenTrays();
        foreach (Border section in LayoutSections())
        {
            if (FindSectionTitle(section) is TextBlock title)
                title.Text = SectionDisplayName((string)section.Tag);
        }
    }

    private void SectionTitle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock title && FindSection(title)?.Tag is string key)
            title.Text = SectionDisplayName(key);
    }

    private static TextBlock? FindSectionTitle(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is TextBlock { Tag: "sectionTitle" } title)
                return title;
            if (child is DependencyObject node && FindSectionTitle(node) is TextBlock found)
                return found;
        }
        return null;
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
            .OrderBy(pair => ranks.TryGetValue(pair.key, out int rank) ? rank : pair.index == 0 ? -1 : int.MaxValue)
            .ThenBy(pair => pair.index)
            .Select(pair => sections.FirstOrDefault(section => (string?)section.Tag == pair.key))
            .OfType<Border>()
            .ToList();
        List<string> hidden = HiddenSections;
        foreach (Border section in sections)
        {
            Visibility wanted = hidden.Contains((string)section.Tag) ? Visibility.Collapsed : Visibility.Visible;
            if (section.Visibility != wanted)
                section.Visibility = wanted;
        }
        if (sections.SequenceEqual(ordered))
        {
            DetachModHosts(root);
            return;
        }
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
        if (chips.Children.Count > 1)
        {
            Wpf.Ui.Controls.Button all = new()
            {
                Content = Strings.Ui_Home_ShowAllSections,
                Appearance = ControlAppearance.Primary,
                FontSize = 12,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 6, 0),
                Tag = keys,
            };
            all.Click += TrayShowAll_Click;
            chips.Children.Add(all);
        }
        tray.Visibility = chips.Children.Count > 0 && _viewModel.IsLayoutEditMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditLayout_Click(object sender, RoutedEventArgs e) => SetLayoutEditMode(!_viewModel.IsLayoutEditMode);

    private void LearnMore_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            main.ShowLearnMore();
    }

    private void SetLayoutEditMode(bool enabled)
    {
        _viewModel.IsLayoutEditMode = enabled;
        if (enabled)
            Focus();
        else
            _layoutUndo.Clear();
        UpdateUndoButton();
        RefreshHiddenTrays();
    }

    private void HomePage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsLayoutEditMode)
            return;
        if (e.Key == Key.Escape)
        {
            SetLayoutEditMode(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            UndoLayout();
            e.Handled = true;
        }
    }

    private void PushLayoutUndo()
    {
        if (_layoutUndo.Count >= MaxLayoutUndo)
        {
            List<(List<string>, List<string>, List<string>)> kept = _layoutUndo.Take(MaxLayoutUndo - 1).Reverse().ToList();
            _layoutUndo.Clear();
            foreach ((List<string>, List<string>, List<string>) entry in kept)
                _layoutUndo.Push(entry);
        }
        _layoutUndo.Push((CurrentOrder(RobloxSections, RobloxSectionKeys), CurrentOrder(StudioSections, StudioSectionKeys), [.. HiddenSections]));
        UpdateUndoButton();
    }

    private static List<string> CurrentOrder(Panel root, string[] keys) =>
        root.Children.OfType<Border>()
            .Where(section => section.Tag is string key && keys.Contains(key))
            .Select(section => (string)section.Tag)
            .ToList();

    private void UpdateUndoButton() => UndoLayoutButton.IsEnabled = _layoutUndo.Count > 0;

    private void UndoLayout_Click(object sender, RoutedEventArgs e) => UndoLayout();

    private void UndoLayout()
    {
        if (_layoutUndo.Count == 0)
            return;
        (List<string> order, List<string> studioOrder, List<string> hidden) = _layoutUndo.Pop();
        App.Settings.Prop.HomeSectionOrder = order;
        App.Settings.Prop.HomeStudioSectionOrder = studioOrder;
        App.Settings.Prop.HomeHiddenSections = hidden;
        App.Settings.SaveDeferred();
        UpdateUndoButton();
        ApplyHomeLayout();
        PluginAnchors.ApplyAll();
    }

    private void TrayShowAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string[] keys })
            return;
        PushLayoutUndo();
        HiddenSections.RemoveAll(keys.Contains);
        App.Settings.SaveDeferred();
        ApplyHomeLayout();
        PluginAnchors.ApplyAll();
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        PushLayoutUndo();
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
        PushLayoutUndo();
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
        PushLayoutUndo();
        HiddenSections.Remove(key);
        App.Settings.SaveDeferred();
        ApplyHomeLayout();
        PluginAnchors.ApplyAll();
    }

    private void SectionGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement grip || FindSection(grip) is not Border section)
            return;
        _dragSection = section;
        _dragStart = e.GetPosition(this);
        _dragging = false;
        grip.CaptureMouse();
        e.Handled = true;
    }

    private void SectionGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSection == null || sender is not UIElement grip)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSectionDrag(grip, false);
            return;
        }
        Point current = e.GetPosition(this);
        if (
            !_dragging
            && Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance
        )
            return;
        if (!_dragging)
        {
            _dragging = true;
            _dragSection.Opacity = 0.55;
        }
        AutoScrollSectionDrag(_dragSection);
        (Border? target, bool after) = SectionDropTarget(_dragSection);
        SetDropHighlight(target, after);
        e.Handled = true;
    }

    private void SectionGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement grip)
            EndSectionDrag(grip, true);
    }

    private void SectionGrip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is UIElement grip && _dragSection != null && !_endingDrag)
            EndSectionDrag(grip, false);
    }

    private void EndSectionDrag(UIElement grip, bool commit)
    {
        Border? section = _dragSection;
        Border? target = _dropSection;
        bool after = _dropAfter;
        bool dragged = _dragging;
        _dragSection = null;
        _dragging = false;
        _endingDrag = true;
        if (grip.IsMouseCaptured)
            grip.ReleaseMouseCapture();
        _endingDrag = false;
        SetDropHighlight(null, false);
        if (section == null)
            return;
        section.Opacity = 1.0;
        if (
            !commit
            || !dragged
            || target == null
            || ReferenceEquals(target, section)
            || section.Parent is not Panel root
            || !ReferenceEquals(target.Parent, root)
        )
            return;
        int from = root.Children.IndexOf(section);
        int to = root.Children.IndexOf(target) + (after ? 1 : 0);
        if (from < to)
            to--;
        MoveSection(root, section, to);
    }

    private static (Border? Target, bool After) SectionDropTarget(Border dragged)
    {
        if (dragged.Parent is not Panel root)
            return (null, false);
        Point pointer = Mouse.GetPosition(root);
        Border? best = null;
        bool after = false;
        foreach (Border candidate in root.Children.OfType<Border>())
        {
            if (!IsSectionKey(candidate.Tag) || candidate.Visibility != Visibility.Visible || candidate.ActualHeight <= 0)
                continue;
            double top = candidate.TranslatePoint(new Point(0, 0), root).Y;
            if (best == null || pointer.Y >= top)
            {
                best = candidate;
                after = pointer.Y >= top + candidate.ActualHeight / 2;
            }
            if (pointer.Y < top + candidate.ActualHeight)
                break;
        }
        if (best == null || ReferenceEquals(best, dragged))
            return (null, false);
        return (best, after);
    }

    private void MoveSection(Panel root, Border section, int index)
    {
        int from = root.Children.IndexOf(section);
        index = Math.Clamp(index, 0, root.Children.Count - 1);
        if (from < 0 || from == index)
            return;
        PushLayoutUndo();
        root.Children.Remove(section);
        root.Children.Insert(index, section);
        SaveLayout();
        DetachModHosts(root);
        PluginAnchors.ApplyAll();
        section.BringIntoView();
    }

    private void SectionMoveUp_Click(object sender, RoutedEventArgs e) => StepSection(sender, -1);

    private void SectionMoveDown_Click(object sender, RoutedEventArgs e) => StepSection(sender, 1);

    private void SectionMoveTop_Click(object sender, RoutedEventArgs e) => JumpSection(sender, true);

    private void SectionMoveBottom_Click(object sender, RoutedEventArgs e) => JumpSection(sender, false);

    private void JumpSection(object sender, bool toTop)
    {
        if (sender is not FrameworkElement element || FindSection(element) is not Border section || section.Parent is not Panel root)
            return;
        List<Border> sections = root.Children.OfType<Border>().Where(candidate => IsSectionKey(candidate.Tag)).ToList();
        if (sections.Count == 0)
            return;
        MoveSection(root, section, root.Children.IndexOf(toTop ? sections[0] : sections[^1]));
    }

    private void StepSection(object sender, int direction)
    {
        if (sender is not FrameworkElement element || FindSection(element) is not Border section || section.Parent is not Panel root)
            return;
        List<Border> visible = root.Children.OfType<Border>()
            .Where(candidate => IsSectionKey(candidate.Tag) && candidate.Visibility == Visibility.Visible)
            .ToList();
        int position = visible.IndexOf(section) + direction;
        if (position < 0 || position >= visible.Count)
            return;
        int target = root.Children.IndexOf(visible[position]);
        MoveSection(root, section, target);
    }

    private void SetDropHighlight(Border? target, bool after)
    {
        if (ReferenceEquals(_dropSection, target) && _dropAfter == after)
            return;
        if (_dropAdorner != null)
        {
            AdornerLayer.GetAdornerLayer(_dropAdorner.AdornedElement)?.Remove(_dropAdorner);
            _dropAdorner = null;
        }
        _dropSection = target;
        _dropAfter = after;
        if (target == null || AdornerLayer.GetAdornerLayer(target) is not AdornerLayer layer)
            return;
        Brush brush = TryFindResource("AccentColorSecondaryBrush") as Brush ?? Brushes.White;
        _dropAdorner = new DropLineAdorner(target, after, brush);
        layer.Add(_dropAdorner);
    }

    private sealed class DropLineAdorner : Adorner
    {
        private readonly bool _after;

        private readonly Pen _pen;

        public DropLineAdorner(UIElement adorned, bool after, Brush brush)
            : base(adorned)
        {
            _after = after;
            _pen = new Pen(brush.CanFreeze ? brush : brush.CloneCurrentValue(), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (_pen.CanFreeze)
                _pen.Freeze();
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            Size size = AdornedElement.RenderSize;
            double y = _after ? size.Height + 4 : -4;
            drawingContext.DrawLine(_pen, new Point(8, y), new Point(Math.Max(8, size.Width - 8), y));
        }
    }

    private static void AutoScrollSectionDrag(Border section)
    {
        ScrollViewer? scrollViewer = FindAncestor<ScrollViewer>(section);
        if (scrollViewer == null)
            return;
        double y = Mouse.GetPosition(scrollViewer).Y;
        if (y < 60)
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - 24);
        else if (y > scrollViewer.ViewportHeight - 60)
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + 24);
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
