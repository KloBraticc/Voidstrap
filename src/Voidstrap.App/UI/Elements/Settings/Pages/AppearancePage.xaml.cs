using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Voidstrap.UI.Elements.ContextMenu;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class AppearancePage : UiPage{
	private readonly AppearanceViewModel _appearanceViewModel;

	private bool isThemeInitialized;

	private bool _customThemeSelectionReady;

	public AppearancePage()
	{
		_appearanceViewModel = new AppearanceViewModel();
		base.DataContext = _appearanceViewModel;
		InitializeComponent();
		Loaded += OnAppearancePageLoaded;
		Unloaded += OnAppearancePageUnloaded;
		DownloadCustomThemeAsync();
	}

	private void OnAppearancePageLoaded(object sender, RoutedEventArgs e)
	{
		GlobalBackground.Changed -= OnLiveBackgroundChanged;
		GlobalBackground.Changed += OnLiveBackgroundChanged;
		_appearanceViewModel.ApplyLiveBackgroundState(GlobalBackground.Current);
		_customThemeSelectionReady = true;
	}

	private void OnAppearancePageUnloaded(object sender, RoutedEventArgs e)
	{
		GlobalBackground.Changed -= OnLiveBackgroundChanged;
	}

	private void OnLiveBackgroundChanged(GlobalBackground.State state)
	{
		if (Dispatcher.CheckAccess())
		{
			_appearanceViewModel.ApplyLiveBackgroundState(state);
		}
		else
		{
			Dispatcher.BeginInvoke((Action)(() => _appearanceViewModel.ApplyLiveBackgroundState(state)));
		}
	}

	public void CustomThemeSelection(object sender, SelectionChangedEventArgs e)
	{
		string? selectedTheme = ((ListBox)sender).SelectedItem as string;
		_appearanceViewModel.SelectedCustomTheme = selectedTheme;
		_appearanceViewModel.SelectedCustomThemeName = selectedTheme ?? "";
		if (_customThemeSelectionReady && selectedTheme != null)
			_appearanceViewModel.Dialog = Voidstrap.Enums.BootstrapperStyle.CustomDialog;
		_appearanceViewModel.OnPropertyChanged("SelectedCustomTheme");
		_appearanceViewModel.OnPropertyChanged("SelectedCustomThemeName");
	}

	private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!isThemeInitialized)
		{
			isThemeInitialized = true;
		}
	}

	private void OptionControl_Loaded(object sender, RoutedEventArgs e)
	{
		DependencyObject parent = (DependencyObject)sender;
		ComboBox combo = FindChild<ComboBox>(parent);
		System.Windows.Controls.Button button = FindChild<System.Windows.Controls.Button>(parent);
		if (combo != null && button != null)
		{
			combo.SelectionChanged -= CustomThemeComboBox_SelectionChanged;
			combo.SelectionChanged += CustomThemeComboBox_SelectionChanged;
			button.Visibility = combo.SelectedItem?.ToString() == "Custom" ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private void CustomThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (sender is not ComboBox combo)
			return;

		DependencyObject? parent = combo;
		while (parent != null && parent is not OptionControl)
			parent = VisualTreeHelper.GetParent(parent);

		if (parent != null && FindChild<System.Windows.Controls.Button>(parent) is { } button)
			button.Visibility = combo.SelectedItem?.ToString() == "Custom" ? Visibility.Visible : Visibility.Collapsed;
	}

	private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			T val = (T)(object)((child is T) ? child : null);
			if (val != null)
			{
				return val;
			}
			T val2 = FindChild<T>(child);
			if (val2 != null)
			{
				return val2;
			}
		}
		return default(T);
	}

	private void OpenCustomThemeEditor_Click(object sender, RoutedEventArgs e)
	{
		CustomThemeEditor customThemeEditor = new CustomThemeEditor();
		customThemeEditor.Owner = Window.GetWindow((DependencyObject)(object)this);
		customThemeEditor.ShowDialog();
	}

	private async Task DownloadCustomThemeAsync()
	{
		string requestUri = "https://raw.githubusercontent.com/KloBraticc/VoidstrapCustomThemes/main/Custom.xaml";
		string destinationPath = Paths.CustomThemeXaml;
		try
		{
			if (!File.Exists(destinationPath))
			{
				string contents = await Voidstrap.Utility.Http.GetString(requestUri);
				Directory.CreateDirectory(Paths.Themes);
				await File.WriteAllTextAsync(destinationPath, contents);
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to download custom theme:\n" + ex.Message, MessageBoxImage.Exclamation);
		}
	}
}
