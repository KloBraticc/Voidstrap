using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Controls;

public partial class RobloxVersionList : UserControl
{
    private bool _updatingSelection;

    public RobloxVersionList()
    {
        InitializeComponent();
    }

    public event EventHandler<object>? ItemChosen;

    public event EventHandler<object>? ItemScrolled;

    public event EventHandler<object>? ItemPlayed;

    public IEnumerable? ItemsSource
    {
        get => VersionsListBox.ItemsSource;
        set => VersionsListBox.ItemsSource = value;
    }

    public object? SelectedItem
    {
        get => VersionsListBox.SelectedItem;
        set
        {
            _updatingSelection = true;
            VersionsListBox.SelectedItem = value;
            _updatingSelection = false;
            if (value != null)
                VersionsListBox.ScrollIntoView(value);
        }
    }

    public static bool IsSelectable(object? item)
    {
        return item switch
        {
            DownloadsViewModel.ClientItem => true,
            DownloadsViewModel.DownloadItem => true,
            DownloadsViewModel.VrItem vr => vr.IsInstalled,
            _ => false
        };
    }

    public void FocusList()
    {
        object? selected = VersionsListBox.SelectedItem;
        if (selected != null && VersionsListBox.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container)
            container.Focus();
        else
            VersionsListBox.Focus();
    }

    private void VersionItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem container && !IsSelectable(container.DataContext) && !IsInsideButton(e.OriginalSource as DependencyObject, container))
            e.Handled = true;
    }

    private void VersionItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem container || IsInsideButton(e.OriginalSource as DependencyObject, container))
            return;
        object? item = container.DataContext;
        if (item == null || !IsSelectable(item))
            return;
        ItemChosen?.Invoke(this, item);
    }

    private static readonly IEasingFunction HoverEaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static readonly IEasingFunction HoverEaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };

    private void VersionItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem container && IsSelectable(container.DataContext))
            AnimateHover(container, true);
    }

    private void VersionItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem container)
            AnimateHover(container, false);
    }

    private void VersionList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            return;
        foreach (object item in VersionsListBox.Items)
            if (VersionsListBox.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                AnimateHover(container, false);
    }

    private static void AnimateHover(ListBoxItem container, bool hovered)
    {
        if (container.Template?.FindName("PlayButton", container) is not UIElement play
            || container.Template.FindName("PlayShift", container) is not TranslateTransform shift)
            return;
        Duration duration = new(TimeSpan.FromMilliseconds(hovered ? 220 : 160));
        IEasingFunction ease = hovered ? HoverEaseOut : HoverEaseIn;
        play.IsHitTestVisible = hovered;
        if (play.Opacity == (hovered ? 1 : 0) && play.IsHitTestVisible == hovered)
            return;
        if (FindNamed(container, "VersionSubtitle") is UIElement subtitle)
            subtitle.BeginAnimation(OpacityProperty, new DoubleAnimation(hovered ? 0 : 1, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        play.BeginAnimation(OpacityProperty, new DoubleAnimation(hovered ? 1 : 0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(hovered ? 0 : 10, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
    }

    private static FrameworkElement? FindNamed(DependencyObject parent, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name)
                return element;
            if (FindNamed(child, name) is FrameworkElement found)
                return found;
        }
        return null;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: { } item } || !IsSelectable(item))
            return;
        ItemPlayed?.Invoke(this, item);
    }

    private void VersionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || e.AddedItems.Count == 0 || IsSelectable(e.AddedItems[0]))
            return;
        _updatingSelection = true;
        VersionsListBox.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
        _updatingSelection = false;
    }

    private void VersionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Space)
            return;
        if (e.OriginalSource is ButtonBase)
            return;
        object? item = VersionsListBox.SelectedItem;
        if (item == null || !IsSelectable(item))
            return;
        ItemChosen?.Invoke(this, item);
        e.Handled = true;
    }

    private void VersionsListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || FindScrollViewer(VersionsListBox)?.ScrollableHeight > 0)
            return;
        int direction = e.Delta < 0 ? 1 : -1;
        int index = VersionsListBox.SelectedIndex;
        if (index < 0)
            index = direction > 0 ? -1 : VersionsListBox.Items.Count;
        for (int i = index + direction; i >= 0 && i < VersionsListBox.Items.Count; i += direction)
        {
            object item = VersionsListBox.Items[i];
            if (!IsSelectable(item))
                continue;
            VersionsListBox.SelectedItem = item;
            VersionsListBox.ScrollIntoView(item);
            FocusList();
            ItemScrolled?.Invoke(this, item);
            break;
        }
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindScrollViewer(child) is ScrollViewer nested)
                return nested;
        }
        return null;
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject stop)
    {
        while (source != null && !ReferenceEquals(source, stop))
        {
            if (source is ButtonBase)
                return true;
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}

public sealed class RobloxVersionSelectableConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        return values.Length > 0 && RobloxVersionList.IsSelectable(values[0]);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
