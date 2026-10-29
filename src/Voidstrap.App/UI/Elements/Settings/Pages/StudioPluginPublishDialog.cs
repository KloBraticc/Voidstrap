using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Studio;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public sealed class StudioPluginPublishDialog : WpfUiWindow
{
	private const string LogIdent = "StudioPluginPublishDialog";

	private readonly System.Windows.Controls.TextBlock _file;

	private readonly Wpf.Ui.Controls.TextBox _name;

	private readonly Wpf.Ui.Controls.TextBox _description;

	private readonly CheckBox _update;

	private readonly Wpf.Ui.Controls.TextBox _assetId;

	private readonly System.Windows.Controls.TextBlock _account;

	private readonly System.Windows.Controls.TextBlock _status;

	private readonly ProgressBar _progress;

	private readonly Wpf.Ui.Controls.Button _choose;

	private readonly Wpf.Ui.Controls.Button _publish;

	private readonly CancellationTokenSource _cancel = new();

	private string? _path;

	private bool _signedIn;

	private bool _busy;

	public StudioPluginPublishDialog()
	{
		Title = Strings.Ui_Mods_StudioPlugins_PublishTitle;
		Width = 480;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		ExtendsContentIntoTitleBar = true;
		SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

		_choose = new Wpf.Ui.Controls.Button { Content = Strings.Ui_Mods_StudioPlugins_PublishChoose, Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24 } };
		_choose.Click += Choose_Click;
		_file = new System.Windows.Controls.TextBlock { Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
		_name = new Wpf.Ui.Controls.TextBox { MaxLength = StudioPluginPublisher.MaxNameLength, Margin = new Thickness(0, 4, 0, 0) };
		_description = new Wpf.Ui.Controls.TextBox { MaxLength = StudioPluginCommunity.MaxTextLength, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, Margin = new Thickness(0, 4, 0, 0) };
		_update = new CheckBox { Content = Strings.Ui_Mods_StudioPlugins_PublishUpdate, Margin = new Thickness(0, 12, 0, 0) };
		_update.Checked += Update_Changed;
		_update.Unchecked += Update_Changed;
		_assetId = new Wpf.Ui.Controls.TextBox { PlaceholderText = Strings.Ui_Mods_StudioPlugins_PublishAssetId, MaxLength = 20, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
		_account = new System.Windows.Controls.TextBlock { Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
		_status = new System.Windows.Controls.TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
		_status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
		_progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

		_publish = new Wpf.Ui.Controls.Button { Content = Strings.Ui_Mods_StudioPlugins_PublishConfirm, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, IsEnabled = false };
		_publish.Click += Publish_Click;
		Wpf.Ui.Controls.Button close = new() { Content = Strings.Common_Close, MinWidth = 110, IsCancel = true };

		StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
		buttons.Children.Add(_publish);
		buttons.Children.Add(close);

		StackPanel body = new() { Margin = new Thickness(24, 12, 24, 20) };
		body.Children.Add(_choose);
		body.Children.Add(_file);
		body.Children.Add(Label(Strings.Ui_Mods_StudioPlugins_PublishName));
		body.Children.Add(_name);
		body.Children.Add(Label(Strings.Ui_Mods_StudioPlugins_PublishDescription));
		body.Children.Add(_description);
		body.Children.Add(_update);
		body.Children.Add(_assetId);
		body.Children.Add(_account);
		body.Children.Add(_status);
		body.Children.Add(_progress);
		body.Children.Add(buttons);

		Content = DialogChrome.Host(DialogChrome.TitleBar(Strings.Ui_Mods_StudioPlugins_PublishTitle), body);
		Loaded += Dialog_Loaded;
		Closing += Dialog_Closing;
		Closed += Dialog_Closed;
	}

	private static System.Windows.Controls.TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 12, 0, 0) };

	private async void Dialog_Loaded(object sender, RoutedEventArgs e)
	{
		Loaded -= Dialog_Loaded;
		try
		{
			RobloxAccount? account = await RobloxCookie.GetAccountAsync(_cancel.Token);
			_signedIn = account != null && account.UserId > 0;
			_account.Text = _signedIn ? string.Format(Strings.Ui_Mods_StudioPlugins_PublishAccount, account!.Username) : Strings.Ui_Mods_StudioPlugins_SignIn;
		}
		catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
		{
			return;
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
		{
			App.Logger.WriteException(LogIdent + "::Account", ex);
			_account.Text = Strings.Ui_Mods_StudioPlugins_SignIn;
		}
		_publish.IsEnabled = _signedIn;
	}

	private void Update_Changed(object sender, RoutedEventArgs e)
	{
		_assetId.Visibility = _update.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
	}

	private void Choose_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog dialog = new()
		{
			Title = Strings.Ui_Mods_StudioPlugins_PublishChoose,
			Filter = Strings.Ui_Mods_StudioPlugins_FileFilter
		};
		if (dialog.ShowDialog(this) != true)
			return;
		StudioPluginPublishError error = StudioPluginPublisher.ValidateFile(dialog.FileName);
		if (error != StudioPluginPublishError.None)
		{
			_status.Text = Describe(error, string.Empty);
			return;
		}
		_path = dialog.FileName;
		_file.Text = Path.GetFileName(_path);
		if (_name.Text.Trim().Length == 0)
			_name.Text = Path.GetFileNameWithoutExtension(_path);
		_status.Text = "";
	}

	private async void Publish_Click(object sender, RoutedEventArgs e)
	{
		if (_busy || !_signedIn)
			return;
		if (_path == null)
		{
			_status.Text = Strings.Ui_Mods_StudioPlugins_PublishFileMissing;
			return;
		}
		long existing = 0;
		if (_update.IsChecked == true && (!long.TryParse(_assetId.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out existing) || existing <= 0))
		{
			_status.Text = Strings.Ui_Mods_StudioPlugins_PublishAssetId;
			return;
		}

		SetBusy(true);
		_status.Text = Strings.Ui_Mods_StudioPlugins_PublishWorking;
		Progress<double> progress = new(value => _progress.Value = value);
		try
		{
			StudioPluginPublishResult result = await StudioPluginPublisher.PublishAsync(_path, _name.Text, _description.Text, existing, progress, _cancel.Token);
			_status.Text = result.Error == StudioPluginPublishError.None
				? string.Format(Strings.Ui_Mods_StudioPlugins_PublishDone, result.AssetId.ToString(CultureInfo.InvariantCulture))
				: Describe(result.Error, result.Detail);
		}
		catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
		{
			return;
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
		{
			App.Logger.WriteException(LogIdent + "::Publish", ex);
			_status.Text = string.Format(Strings.Ui_Mods_StudioPlugins_PublishFailed, ex.GetType().Name);
		}
		SetBusy(false);
	}

	private void SetBusy(bool busy)
	{
		_busy = busy;
		_publish.IsEnabled = !busy && _signedIn;
		_choose.IsEnabled = !busy;
		_name.IsEnabled = !busy;
		_description.IsEnabled = !busy;
		_update.IsEnabled = !busy;
		_assetId.IsEnabled = !busy;
		_progress.Value = 0;
		_progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
	}

	private static string Describe(StudioPluginPublishError error, string detail) => error switch
	{
		StudioPluginPublishError.FileMissing => Strings.Ui_Mods_StudioPlugins_PublishFileMissing,
		StudioPluginPublishError.FileType => Strings.Ui_Mods_StudioPlugins_PublishFileType,
		StudioPluginPublishError.FileTooLarge => Strings.Ui_Mods_StudioPlugins_PublishFileTooLarge,
		StudioPluginPublishError.FileNotRoblox => Strings.Ui_Mods_StudioPlugins_PublishFileNotRoblox,
		StudioPluginPublishError.NameLength => Strings.Ui_Mods_StudioPlugins_PublishNameLength,
		StudioPluginPublishError.NotSignedIn => Strings.Ui_Mods_StudioPlugins_SignIn,
		StudioPluginPublishError.NotAllowed => Strings.Ui_Mods_StudioPlugins_PublishNotAllowed,
		StudioPluginPublishError.RateLimited => Strings.Ui_Mods_StudioPlugins_RateLimited,
		_ => string.Format(Strings.Ui_Mods_StudioPlugins_PublishFailed, detail.Length > 0 ? detail : Strings.Ui_Mods_StudioPlugins_ActionFailed)
	};

	private void Dialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		if (_busy)
			e.Cancel = true;
	}

	private void Dialog_Closed(object? sender, EventArgs e)
	{
		Closing -= Dialog_Closing;
		Closed -= Dialog_Closed;
		Loaded -= Dialog_Loaded;
		_choose.Click -= Choose_Click;
		_publish.Click -= Publish_Click;
		_update.Checked -= Update_Changed;
		_update.Unchecked -= Update_Changed;
		_cancel.Cancel();
		_cancel.Dispose();
	}
}
