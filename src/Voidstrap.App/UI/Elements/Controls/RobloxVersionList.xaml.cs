using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
			DownloadsViewModel.ClientItem client => client.IsInstalled,
			DownloadsViewModel.DownloadItem => true,
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
