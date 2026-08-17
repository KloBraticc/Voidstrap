using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Bootstrapper;

namespace Voidstrap.UI.ViewModels.Editor;

public class BootstrapperEditorWindowViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private CustomDialog? _dialog;

	private bool _disposed;

	public ICommand PreviewCommand => new RelayCommand(Preview);

	public ICommand SaveCommand => new RelayCommand(Save);

	public ICommand OpenThemeFolderCommand => new RelayCommand(OpenThemeFolder);

	public Action<bool, string>? ThemeSavedCallback { get; set; }

	public string Directory { get; set; } = "";

	public string Name { get; set; } = "";

	public string Title { get; set; } = "Editing \"Custom Theme\"";

	public string Code { get; set; } = "";

	public bool CodeChanged { get; set; }

	private void Preview()
	{
		if (_disposed)
		{
			return;
		}
		CustomDialog? customDialog = null;
		try
		{
			customDialog = new CustomDialog(true);
			customDialog.ApplyCustomTheme(Name, Code);
			_dialog?.CloseBootstrapper();
			_dialog = customDialog;
			customDialog.Message = Strings.Bootstrapper_StylePreview_TextCancel;
			customDialog.CancelEnabled = true;
			customDialog.ShowBootstrapper();
		}
		catch (Exception ex)
		{
			try { customDialog?.Close(); } catch { }
			App.Logger.WriteLine("BootstrapperEditorWindowViewModel::Preview", "Failed to preview custom theme");
			App.Logger.WriteException("BootstrapperEditorWindowViewModel::Preview", ex);
			Frontend.ShowMessageBox("Failed to preview theme: " + ex.Message, MessageBoxImage.Hand);
		}
	}

	public static bool LooksLikeThemeXml(string content)
	{
		string trimmed = (content ?? "").TrimStart();

		if (trimmed.Length == 0)
			return true;

		if (trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
			|| trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
			return false;

		return true;
	}

	private void Save()
	{
		string path = Path.Combine(Directory, "Theme.xml");
		try
		{
			if (!LooksLikeThemeXml(Code))
			{
				App.Logger.WriteLine("BootstrapperEditorWindowViewModel::Save", "Refused to write non theme content into Theme.xml");
				ThemeSavedCallback?.Invoke(arg1: false, "That content is not a theme file, so Theme.xml was left alone.");
				return;
			}

			File.WriteAllText(path, Code);
			CodeChanged = false;
			ThemeSavedCallback?.Invoke(arg1: true, "Your theme has been saved!");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("BootstrapperEditorWindowViewModel::Save", "Failed to save custom theme");
			App.Logger.WriteException("BootstrapperEditorWindowViewModel::Save", ex);
			ThemeSavedCallback?.Invoke(arg1: false, ex.Message);
		}
	}

	private void OpenThemeFolder()
	{
		using Process? process = Process.Start("explorer.exe", Directory);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_dialog?.CloseBootstrapper();
		_dialog = null;
		ThemeSavedCallback = null;
		GC.SuppressFinalize(this);
	}
}
