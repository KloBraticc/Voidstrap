using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Search;
using Voidstrap.Integrations.Plugins;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class VoidstrapModsPage : UiPage
{
	public VoidstrapModsPage()
	{
		InitializeComponent();
	}

	private void Page_Loaded(object sender, RoutedEventArgs e)
	{
		PluginHost.PanesChanged -= OnPanesChanged;
		PluginHost.PanesChanged += OnPanesChanged;
		PluginHost.PaneInvalidated -= OnPaneInvalidated;
		PluginHost.PaneInvalidated += OnPaneInvalidated;
		PluginHost.StatusChanged -= OnStatusChanged;
		PluginHost.StatusChanged += OnStatusChanged;
		PluginHost.ModsChanged -= OnModsChanged;
		PluginHost.ModsChanged += OnModsChanged;
		RenderMods();
		RenderAll();
		RenderStatus();
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e)
	{
		PluginHost.PanesChanged -= OnPanesChanged;
		PluginHost.PaneInvalidated -= OnPaneInvalidated;
		PluginHost.StatusChanged -= OnStatusChanged;
		PluginHost.ModsChanged -= OnModsChanged;
	}

	private void OnModsChanged() => Dispatcher.BeginInvoke(new Action(RenderMods));

	private void RenderMods()
	{
		ModsHost.Children.Clear();
		IReadOnlyList<PluginHost.ModInfo> mods = PluginHost.ListMods();
		EmptyText.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		foreach (PluginHost.ModInfo mod in mods)
		{
			Grid header = new();
			header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			header.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
			header.Children.Add(BuildModIcon(mod));
			StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
			Grid.SetColumn(text, 1);
			WrapPanel titleRow = new() { Orientation = Orientation.Horizontal };
			titleRow.Children.Add(PluginUiRenderer.MakeText(mod.Name + "  " + mod.Version, new JsonObject { ["bold"] = true, ["size"] = 14 }));
			Border stateBadge = new()
			{
				CornerRadius = new CornerRadius(8),
				Padding = new Thickness(7, 1, 7, 1),
				Margin = new Thickness(6, 0, 6, 6),
				VerticalAlignment = VerticalAlignment.Center,
				Background = new SolidColorBrush(mod.Loaded ? Color.FromArgb(45, 46, 204, 113) : Color.FromArgb(35, 160, 168, 180)),
				Child = new System.Windows.Controls.TextBlock
				{
					Text = mod.Loaded ? Strings.Ui_VoidstrapMods_LoadedBadge : Strings.Ui_VoidstrapMods_DisabledBadge,
					FontSize = 11,
					FontWeight = FontWeights.SemiBold,
					Foreground = new SolidColorBrush(mod.Loaded ? Color.FromRgb(88, 214, 141) : Color.FromRgb(190, 196, 206))
				}
			};
			titleRow.Children.Add(stateBadge);
			text.Children.Add(titleRow);
			if (mod.Description.Length > 0)
				text.Children.Add(PluginUiRenderer.MakeText(mod.Description, new JsonObject { ["dim"] = true }));
			if (mod.Consent == "on" && !mod.Loaded)
				text.Children.Add(PluginUiRenderer.MakeText(Strings.Ui_VoidstrapMods_NotLoaded, new JsonObject()));
			header.Children.Add(text);

			WrapPanel actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
			Wpf.Ui.Controls.Button editBtn = new()
			{
				Content = Strings.Ui_VoidstrapMods_EditButton,
				Icon = SymbolRegular.Code24,
				Appearance = ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 6, 0),
				Tag = mod.Name
			};
			editBtn.Click += EditMod_Click;
			actions.Children.Add(editBtn);

			Wpf.Ui.Controls.Button iconBtn = new()
			{
				Icon = SymbolRegular.ImageEdit24,
				ToolTip = Strings.Ui_VoidstrapMods_ChangeIcon,
				Appearance = ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 6, 0),
				Tag = mod.Name
			};
			iconBtn.Click += ChangeIcon_Click;
			actions.Children.Add(iconBtn);

			Wpf.Ui.Controls.Button exportBtn = new()
			{
				Icon = SymbolRegular.ArrowExportUp24,
				ToolTip = Strings.Ui_VoidstrapMods_ExportTooltip,
				Appearance = ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 6, 0),
				Tag = mod.Name
			};
			exportBtn.Click += ExportMod_Click;
			actions.Children.Add(exportBtn);

			Wpf.Ui.Controls.Button deleteBtn = new()
			{
				Icon = SymbolRegular.Delete24,
				ToolTip = Strings.Common_Delete,
				Appearance = ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 10, 0),
				Tag = mod.Name
			};
			deleteBtn.Click += DeleteMod_Click;
			actions.Children.Add(deleteBtn);

			ToggleSwitch toggle = new() { IsChecked = mod.Consent == "on", Tag = mod.Name, VerticalAlignment = VerticalAlignment.Center };
			toggle.Click += ModToggle_Click;
			actions.Children.Add(toggle);

			Grid.SetColumn(actions, 2);
			header.Children.Add(actions);

			StackPanel body = new();
			body.Children.Add(header);
			if (mod.Problems.Count > 0)
			{
				System.Windows.Controls.TextBlock problems = new()
				{
					Text = string.Join(Environment.NewLine, mod.Problems.TakeLast(10)),
					FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono, monospace"),
					FontSize = 11,
					TextWrapping = TextWrapping.Wrap
				};
				problems.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
				body.Children.Add(new Expander { Header = Strings.Ui_VoidstrapMods_RecentProblems, Content = problems, Margin = new Thickness(0, 8, 0, 0) });
			}
			Border card = new()
			{
				Child = body,
				Padding = new Thickness(14, 10, 14, 10),
				Margin = new Thickness(0, 0, 0, 6),
				CornerRadius = new CornerRadius(6),
				BorderThickness = new Thickness(1)
			};
			card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
			card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
			ModsHost.Children.Add(card);
		}
	}

	private static FrameworkElement BuildModIcon(PluginHost.ModInfo mod)
	{
		Border frame = new()
		{
			Width = 40,
			Height = 40,
			Margin = new Thickness(0, 0, 12, 0),
			CornerRadius = new CornerRadius(8),
			VerticalAlignment = VerticalAlignment.Center
		};
		frame.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
		string? iconPath = ModFiles.GetIconPath(mod.Directory);
		if (iconPath != null && Voidstrap.UI.Elements.Dialogs.CreateVoidstrapModDialog.LoadIcon(iconPath) is { } image)
			frame.Child = new Image { Source = image, Width = 28, Height = 28, Stretch = Stretch.Uniform };
		else
			frame.Child = new SymbolIcon { Symbol = SymbolRegular.PuzzlePiece24, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		return frame;
	}

	private void NewMod_Click(object sender, RoutedEventArgs e)
	{
		Voidstrap.UI.Elements.Dialogs.CreateVoidstrapModDialog dialog = new();
		if (dialog.ShowOwnedDialog() != true || dialog.CreatedMod is not string name)
			return;
		PluginHost.SetEnabled(name, true);
		OpenModInEditor(name);
	}

	private void ChangeIcon_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string modName })
			return;
		Microsoft.Win32.OpenFileDialog picker = new() { Filter = Strings.Ui_VoidstrapMods_IconFilter, CheckFileExists = true };
		if (picker.ShowDialog(Window.GetWindow(this)) != true)
			return;
		try
		{
			if (Voidstrap.UI.Elements.Dialogs.CreateVoidstrapModDialog.LoadIcon(picker.FileName) == null)
				throw new InvalidDataException(Strings.Ui_VoidstrapMods_IconFailed);
			ModFiles.SetIcon(modName, picker.FileName);
			RenderMods();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
		}
	}

	private void ExportMod_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string modName })
			return;
		Microsoft.Win32.SaveFileDialog picker = new()
		{
			Filter = Strings.Ui_VoidstrapMods_PackageFilter,
			FileName = modName + ModFiles.PackageExtension,
			DefaultExt = ModFiles.PackageExtension,
			AddExtension = true
		};
		if (picker.ShowDialog(Window.GetWindow(this)) != true)
			return;
		try
		{
			ModFiles.Export(modName, picker.FileName);
			PlatformShell.TryOpenFolder(Path.GetDirectoryName(picker.FileName)!);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox(string.Format(Strings.Ui_VoidstrapMods_ExportFailed, ex.Message), MessageBoxImage.Error);
		}
	}

	private void Import_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog picker = new() { Filter = Strings.Ui_VoidstrapMods_PackageFilter, CheckFileExists = true, Multiselect = true };
		if (picker.ShowDialog(Window.GetWindow(this)) != true)
			return;
		ImportPackages(picker.FileNames);
	}

	private static bool IsPackageFile(string path) => path.EndsWith(ModFiles.PackageExtension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

	private void Page_DragOver(object sender, DragEventArgs e)
	{
		e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsPackageFile) ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private void Page_Drop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
			ImportPackages(files.Where(IsPackageFile).ToArray());
		e.Handled = true;
	}

	private async void ImportPackages(string[] files)
	{
		string? last = null;
		foreach (string file in files)
		{
			try
			{
				ModFiles.Package package = await Task.Run(() => ModFiles.OpenPackage(file));
				string name = package.Manifest.Name;
				if (Directory.Exists(Path.Combine(Paths.Plugins, name)) && Frontend.ShowMessageBox(
					string.Format(Strings.Ui_VoidstrapMods_ImportReplacePrompt, name),
					MessageBoxImage.Question,
					MessageBoxButton.YesNo,
					MessageBoxResult.No) != MessageBoxResult.Yes)
				{
					ModFiles.TryDelete(package.StagingFolder);
					continue;
				}
				await Task.Run(() => ModFiles.Install(package));
				App.Logger.WriteLine("VoidstrapMods", "Imported " + name + " from a mod file");
				last = name;
			}
			catch (Exception ex)
			{
				Frontend.ShowMessageBox(string.Format(Strings.Ui_VoidstrapMods_ImportFailed, Path.GetFileName(file), ex.Message), MessageBoxImage.Error);
			}
		}
		if (last != null)
			await Task.Run(PluginHost.Reload);
		RenderMods();
	}

	private void EditMod_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string modName })
			OpenModInEditor(modName);
	}

	private void DeleteMod_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string modName })
			return;
		if (Frontend.ShowMessageBox(
			string.Format(Strings.Ui_VoidstrapMods_DeleteModPrompt, modName),
			MessageBoxImage.Warning,
			MessageBoxButton.YesNo,
			MessageBoxResult.No) != MessageBoxResult.Yes)
			return;
		try
		{
			string folder = Path.Combine(Paths.Plugins, modName);
			if (Directory.Exists(folder))
				Directory.Delete(folder, true);
			PluginHost.ForgetConsent(modName);
			Task.Run(PluginHost.Reload);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
		}
	}

	public void OpenModInEditor(string modName)
	{
		if (Directory.Exists(Path.Combine(Paths.Plugins, modName)))
			new Voidstrap.UI.Elements.Editor.VoidstrapModEditorWindow(modName).ShowOwnedDialog();
	}

	private void ModToggle_Click(object sender, RoutedEventArgs e)
	{
		if (sender is ToggleSwitch { Tag: string name } toggle)
			PluginHost.SetEnabled(name, toggle.IsChecked == true);
	}

	private void ConnectAi_Click(object sender, RoutedEventArgs e) => Voidstrap.Integrations.Mcp.McpConnectInfo.Show();

	private void Reload_Click(object sender, RoutedEventArgs e)
	{
		Task.Run(PluginHost.Reload);
	}

	private void OnPanesChanged() => Dispatcher.BeginInvoke(new Action(RenderAll));

	private void OnPaneInvalidated(string plugin) => Dispatcher.BeginInvoke(new Action(RenderAll));

	private void OnStatusChanged() => Dispatcher.BeginInvoke(new Action(RenderStatus));

	private void RenderStatus()
	{
		StatusText.Text = string.Join("   ", PluginHost.StatusLines);
	}

	private void RenderAll()
	{
		PanesHost.Children.Clear();
		IReadOnlyList<PluginHost.PaneInfo> panes = PluginHost.Panes;
		ActivePanesHeader.Visibility = panes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
		foreach (PluginHost.PaneInfo pane in panes)
		{
			StackPanel body = new();
			body.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = pane.Title,
				FontSize = 16,
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 0, 0, 8)
			});
			UIElement? content = null;
			try
			{
				string dir = Path.Combine(Paths.Plugins, pane.Plugin);
				content = PluginUiRenderer.BuildTree(PluginHost.RenderPane(pane), dir);
			}
			catch (Exception ex)
			{
				PluginHost.Problem(pane.Plugin, "the pane " + pane.Id + " could not be drawn: " + PluginHost.Describe(ex));
			}
			if (content != null)
				body.Children.Add(content);
			Border card = new()
			{
				Child = body,
				Padding = new Thickness(16),
				Margin = new Thickness(0, 0, 0, 12),
				CornerRadius = new CornerRadius(8),
				BorderThickness = new Thickness(1)
			};
			card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
			card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
			PanesHost.Children.Add(card);
		}
	}

	private void OpenFolder_Click(object sender, RoutedEventArgs e)
	{
		Directory.CreateDirectory(Paths.Plugins);
		PlatformShell.TryOpenFolder(Paths.Plugins);
	}
}
