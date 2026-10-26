using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Settings.Pages;

public sealed class IconSwapItem : INotifyPropertyChanged
{
	private string? _swapPath;

	private ImageSource? _swapImage;

	public required string Name { get; init; }

	public required ImageSource Original { get; init; }

	public string? SwapPath
	{
		get => _swapPath;
		set
		{
			_swapPath = value;
			_swapImage = value == null ? null : LoadThumbnail(value);
			OnPropertyChanged();
			OnPropertyChanged(nameof(Shown));
			OnPropertyChanged(nameof(RestoreVisibility));
		}
	}

	public ImageSource Shown => _swapImage ?? Original;

	public Visibility RestoreVisibility => _swapPath == null ? Visibility.Collapsed : Visibility.Visible;

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

	private static ImageSource? LoadThumbnail(string path)
	{
		try
		{
			BitmapImage image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
			image.DecodePixelWidth = 64;
			image.UriSource = new Uri(Path.GetFullPath(path));
			image.EndInit();
			image.Freeze();
			return image;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModGeneratorIconSwapDialog", "The swap image could not be shown: " + ex.Message);
			return null;
		}
	}
}

public sealed class ModGeneratorIconSwapDialog : WpfUiWindow
{
	private const string LogIdent = "ModGeneratorIconSwapDialog";

	private readonly ObservableCollection<IconSwapItem> _items = [];

	private readonly ListBox _list;

	private readonly Wpf.Ui.Controls.TextBox _search;

	private readonly System.Windows.Controls.TextBlock _status;

	private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250.0) };

	private readonly Dictionary<string, string> _swaps;

	private ICollectionView? _view;

	private readonly Wpf.Ui.Controls.Button _done;

	public IReadOnlyDictionary<string, string> Swaps => _swaps;

	public ModGeneratorIconSwapDialog(IReadOnlyDictionary<string, string> swaps)
	{
		_swaps = new Dictionary<string, string>(swaps, StringComparer.Ordinal);
		Title = Strings.Ui_ModGen_SwapIcons;
		Width = 560;
		Height = 620;
		ResizeMode = ResizeMode.CanResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		ExtendsContentIntoTitleBar = true;
		SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

		System.Windows.Controls.TextBlock help = new System.Windows.Controls.TextBlock { Text = Strings.Ui_ModGen_SwapIconsHelp, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
		_search = new Wpf.Ui.Controls.TextBox { PlaceholderText = Strings.Ui_ModGen_SearchIcons, Icon = Wpf.Ui.Common.SymbolRegular.Search24 };
		_search.TextChanged += Search_TextChanged;
		_searchTimer.Tick += SearchTimer_Tick;
		_status = new System.Windows.Controls.TextBlock { Text = Strings.Ui_ModGen_LoadingIcons, Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
		_status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

		_list = new ListBox
		{
			ItemTemplate = BuildTemplate(),
			BorderThickness = new Thickness(0),
			Background = Brushes.Transparent,
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		VirtualizingPanel.SetIsVirtualizing(_list, true);
		VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
		ScrollViewer.SetCanContentScroll(_list, true);
		_list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(List_ButtonClick));

		Wpf.Ui.Controls.Button done = new Wpf.Ui.Controls.Button { Content = Strings.Ui_ModGen_Done, Appearance = Wpf.Ui.Common.ControlAppearance.Primary, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
		done.Click += Done_Click;
		_done = done;
		Wpf.Ui.Controls.Button cancel = new Wpf.Ui.Controls.Button { Content = Strings.Common_Cancel, MinWidth = 110, IsCancel = true };
		StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
		buttons.Children.Add(done);
		buttons.Children.Add(cancel);

		Grid body = new Grid { Margin = new Thickness(20, 8, 20, 16) };
		for (int i = 0; i < 5; i++)
		{
			body.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
		}
		Grid.SetRow(help, 0);
		Grid.SetRow(_search, 1);
		Grid.SetRow(_status, 2);
		Grid.SetRow(_list, 3);
		Grid.SetRow(buttons, 4);
		body.Children.Add(help);
		body.Children.Add(_search);
		body.Children.Add(_status);
		body.Children.Add(_list);
		body.Children.Add(buttons);

		Content = DialogChrome.Host(DialogChrome.TitleBar(Strings.Ui_ModGen_SwapIcons), body);
		Loaded += Dialog_Loaded;
		Closed += Dialog_Closed;
	}

	private static DataTemplate BuildTemplate()
	{
		FrameworkElementFactory grid = new FrameworkElementFactory(typeof(Grid));
		grid.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 2, 4));
		FrameworkElementFactory c0 = new FrameworkElementFactory(typeof(ColumnDefinition));
		c0.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
		FrameworkElementFactory c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
		c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
		FrameworkElementFactory c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
		c2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
		grid.AppendChild(c0);
		grid.AppendChild(c1);
		grid.AppendChild(c2);

		FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
		frame.SetValue(FrameworkElement.WidthProperty, 40.0);
		frame.SetValue(FrameworkElement.HeightProperty, 40.0);
		frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
		frame.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1C)));
		FrameworkElementFactory image = new FrameworkElementFactory(typeof(Image));
		image.SetValue(FrameworkElement.MarginProperty, new Thickness(5));
		image.SetValue(Image.StretchProperty, Stretch.Uniform);
		image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
		image.SetBinding(Image.SourceProperty, new Binding(nameof(IconSwapItem.Shown)));
		frame.AppendChild(image);
		grid.AppendChild(frame);

		FrameworkElementFactory name = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
		name.SetValue(Grid.ColumnProperty, 1);
		name.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 8, 0));
		name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		name.SetValue(System.Windows.Controls.TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
		name.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(IconSwapItem.Name)));
		grid.AppendChild(name);

		FrameworkElementFactory actions = new FrameworkElementFactory(typeof(StackPanel));
		actions.SetValue(Grid.ColumnProperty, 2);
		actions.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
		actions.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		FrameworkElementFactory restore = new FrameworkElementFactory(typeof(Wpf.Ui.Controls.Button));
		restore.SetValue(ContentControl.ContentProperty, Strings.Ui_ModGen_RestoreIcon);
		restore.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
		restore.SetValue(ButtonBase.CommandParameterProperty, "restore");
		restore.SetBinding(FrameworkElement.TagProperty, new Binding());
		restore.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(IconSwapItem.RestoreVisibility)));
		FrameworkElementFactory replace = new FrameworkElementFactory(typeof(Wpf.Ui.Controls.Button));
		replace.SetValue(ContentControl.ContentProperty, Strings.Ui_ModGen_ReplaceIcon);
		replace.SetValue(ButtonBase.CommandParameterProperty, "replace");
		replace.SetBinding(FrameworkElement.TagProperty, new Binding());
		actions.AppendChild(restore);
		actions.AppendChild(replace);
		grid.AppendChild(actions);

		return new DataTemplate { VisualTree = grid };
	}

	private async void Dialog_Loaded(object sender, RoutedEventArgs e)
	{
		Loaded -= Dialog_Loaded;
		IReadOnlyList<ModGeneratorIcon>? icons = null;
		try
		{
			icons = await Task.Run(ModGenerator.ListIcons);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The icon list could not be built: " + ex.Message);
		}
		if (!IsLoaded)
		{
			return;
		}
		if (icons == null || icons.Count == 0)
		{
			_status.Text = Strings.Ui_ModGen_IconsUnavailable;
			return;
		}
		foreach (ModGeneratorIcon icon in icons.OrderByDescending(icon => _swaps.ContainsKey(icon.Name)))
		{
			IconSwapItem item = new IconSwapItem { Name = icon.Name, Original = icon.Thumbnail };
			if (_swaps.TryGetValue(icon.Name, out string? path) && File.Exists(path))
			{
				item.SwapPath = path;
			}
			_items.Add(item);
		}
		_view = CollectionViewSource.GetDefaultView(_items);
		_view.Filter = Matches;
		_list.ItemsSource = _view;
		UpdateStatus();
	}

	private bool Matches(object value)
	{
		string query = _search.Text.Trim();
		return query.Length == 0 || (value is IconSwapItem item && item.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
	}

	private void Search_TextChanged(object sender, TextChangedEventArgs e)
	{
		_searchTimer.Stop();
		_searchTimer.Start();
	}

	private void SearchTimer_Tick(object? sender, EventArgs e)
	{
		_searchTimer.Stop();
		_view?.Refresh();
	}

	private void List_ButtonClick(object sender, RoutedEventArgs e)
	{
		if (e.OriginalSource is not ButtonBase { Tag: IconSwapItem item } button)
		{
			return;
		}
		e.Handled = true;
		if (Equals(button.CommandParameter, "restore"))
		{
			item.SwapPath = null;
			_swaps.Remove(item.Name);
			UpdateStatus();
			return;
		}
		OpenFileDialog dialog = new OpenFileDialog
		{
			Filter = Strings.Ui_ModGen_ImagesFilter + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
			Multiselect = false
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}
		item.SwapPath = dialog.FileName;
		_swaps[item.Name] = dialog.FileName;
		UpdateStatus();
	}

	private void UpdateStatus()
	{
		_status.Text = _swaps.Count == 0 ? Strings.Ui_ModGen_NoSwaps : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Ui_ModGen_SwappedCount, _swaps.Count);
	}

	private void Done_Click(object sender, RoutedEventArgs e)
	{
		DialogResult = true;
		Close();
	}

	private void Dialog_Closed(object? sender, EventArgs e)
	{
		Closed -= Dialog_Closed;
		Loaded -= Dialog_Loaded;
		_searchTimer.Stop();
		_searchTimer.Tick -= SearchTimer_Tick;
		_search.TextChanged -= Search_TextChanged;
		_done.Click -= Done_Click;
		_list.RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(List_ButtonClick));
		_list.ItemsSource = null;
	}
}
