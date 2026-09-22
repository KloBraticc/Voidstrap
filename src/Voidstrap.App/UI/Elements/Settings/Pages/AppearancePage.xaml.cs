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
using Microsoft.Win32;
using Voidstrap.UI.Elements.ContextMenu;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.Elements.Dialogs;
using Voidstrap.UI.ViewModels.Settings;
using Voidstrap.Utility;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class AppearancePage : UiPage
{
	private readonly AppearanceViewModel _appearanceViewModel;

	private bool isThemeInitialized;

	private bool _customThemeSelectionReady;

	private bool _backdropSelectionReady;

	public AppearancePage()
	{
		_appearanceViewModel = new AppearanceViewModel();
		base.DataContext = _appearanceViewModel;
		InitializeComponent();
		SidebarGrid.Resources[SidebarIconPickerDialog.IconFontResourceKey] = SidebarIconPickerDialog.FullIconFont;
		Loaded += OnAppearancePageLoaded;
		Unloaded += OnAppearancePageUnloaded;
		_ = DownloadCustomThemeAsync();
	}

	private void OnAppearancePageLoaded(object sender, RoutedEventArgs e)
	{
		_appearanceViewModel.OnPropertyChanged(nameof(AppearanceViewModel.SelectedBackdrop));
		Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => _backdropSelectionReady = true));
		GlobalBackground.Changed -= OnLiveBackgroundChanged;
		GlobalBackground.Changed += OnLiveBackgroundChanged;
		_appearanceViewModel.ApplyLiveBackgroundState(GlobalBackground.Current);
		_customThemeSelectionReady = true;
	}

	private void OnAppearancePageUnloaded(object sender, RoutedEventArgs e)
	{
		_backdropSelectionReady = false;
		GlobalBackground.Changed -= OnLiveBackgroundChanged;
	}

	private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_backdropSelectionReady
			&& sender is ComboBox { SelectedItem: Voidstrap.Models.BackdropType backdrop })
			_appearanceViewModel.SelectedBackdrop = backdrop;
	}

	private void SidebarGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
	{
		if (e.Handled || sender is not FrameworkElement { Parent: UIElement parent })
		{
			return;
		}
		e.Handled = true;
		parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
		{
			RoutedEvent = UIElement.MouseWheelEvent,
			Source = sender
		});
	}

	private void SidebarIconButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Wpf.Ui.Controls.Button { Tag: AppearanceViewModel.SidebarItemEditor item })
		{
			return;
		}
		using SidebarIconPickerDialog dialog = new SidebarIconPickerDialog(item.Icon)
		{
			Owner = Window.GetWindow(this)
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		switch (dialog.Choice)
		{
			case SidebarIconPickerDialog.PickerChoice.Symbol:
				_appearanceViewModel.SetSidebarSymbol(item, dialog.SelectedIcon);
				break;
			case SidebarIconPickerDialog.PickerChoice.Image:
				ChooseSidebarImage(item);
				break;
			case SidebarIconPickerDialog.PickerChoice.Default:
				_appearanceViewModel.ResetSidebarIcon(item);
				break;
		}
	}

	private void ChooseSidebarImage(AppearanceViewModel.SidebarItemEditor item)
	{
		OpenFileDialog picker = new OpenFileDialog
		{
			Title = "Choose sidebar image",
			Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.webp",
			CheckFileExists = true,
			Multiselect = false
		};
		if (picker.ShowDialog(Window.GetWindow(this)) != true)
		{
			return;
		}
		string extension = Path.GetExtension(picker.FileName).ToLowerInvariant();
		if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp" and not ".gif" and not ".ico" and not ".webp"
			|| SafeImaging.FromFile(picker.FileName, 48) == null)
		{
			ShowSidebarImageError("The selected image could not be loaded. Choose a PNG, JPEG, BMP, GIF, ICO, or WebP file.");
			return;
		}
		try
		{
			string directory = Path.Combine(Paths.Data, "SidebarIcons");
			Directory.CreateDirectory(directory);
			string target = Path.Combine(directory, item.Key + extension);
			if (!string.Equals(Path.GetFullPath(picker.FileName), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
			{
				File.Copy(picker.FileName, target, true);
			}
			_appearanceViewModel.SetSidebarImage(item, target);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AppearancePage::ChooseSidebarImage", "Could not save the sidebar image: " + ex.Message);
			ShowSidebarImageError("The selected image could not be saved. Choose another image and try again.");
		}
	}

	private void ShowSidebarImageError(string message)
	{
		using FluentMessageBox messageBox = new FluentMessageBox(message, MessageBoxImage.Error, MessageBoxButton.OK)
		{
			Owner = Window.GetWindow(this)
		};
		messageBox.ShowDialog();
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
		ComboBox? combo = FindChild<ComboBox>(parent);
		System.Windows.Controls.Button? button = FindChild<System.Windows.Controls.Button>(parent);
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

	private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, i);
			T? val = (T?)(object?)((child is T) ? child : null);
			if (val != null)
			{
				return val;
			}
			T? val2 = FindChild<T>(child);
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
		customThemeEditor.ShowOwnedDialog();
	}

	private static async Task DownloadCustomThemeAsync()
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
