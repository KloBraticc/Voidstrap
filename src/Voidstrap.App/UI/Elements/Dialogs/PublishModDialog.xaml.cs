using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Voidstrap.Integrations.Plugins;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class PublishModDialog : WpfUiWindow
{
	private readonly CancellationTokenSource _cts = new();

	private readonly string _mod;

	private GitHubPublisher.Account? _account;

	private string? _pullRequestUrl;

	public PublishModDialog(string mod, string? suggestedNotes = null)
	{
		_mod = mod;
		InitializeComponent();
		Title = string.Format(Strings.Ui_VoidstrapMods_PublishTitle, mod);
		NotesBox.Text = suggestedNotes ?? "";
		Loaded += OnDialogLoaded;
		Closed += OnDialogClosed;
	}

	private async void OnDialogLoaded(object sender, RoutedEventArgs e)
	{
		_account = GitHubSignInDialog.EnsureSignedIn(this);
		if (_account == null)
		{
			Close();
			return;
		}
		InfoText.Text = string.Format(Strings.Ui_VoidstrapMods_PublishInfo, _account.Login);
		try
		{
			GitHubPublisher.Preflight check = await GitHubPublisher.CheckAsync(_mod, _cts.Token);
			SummaryText.Text = check.IsUpdate
				? string.Format(Strings.Ui_VoidstrapMods_PublishSummaryUpdate, check.PublishedVersion, check.Version)
				: string.Format(Strings.Ui_VoidstrapMods_PublishSummaryNew, check.Version);
			NetworkText.Text = check.Network.Count == 0 ? Strings.Ui_VoidstrapMods_LibraryNoNetwork : string.Format(Strings.Ui_VoidstrapMods_LibraryNetwork, string.Join(", ", check.Network));
			FilesExpander.Header = string.Format(Strings.Ui_VoidstrapMods_PublishFiles, check.Files.Count + 1);
			FilesText.Text = string.Join(Environment.NewLine, check.Files.Append("owner.json"));
			ProblemsText.Text = string.Join(Environment.NewLine, check.Problems.Select(p => "• " + p));
			ProblemsBox.Visibility = check.Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			WarningsText.Text = string.Join(Environment.NewLine, check.Warnings.Select(w => "• " + w));
			WarningsBox.Visibility = check.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			ResultPanel.Visibility = Visibility.Visible;
			PublishButton.IsEnabled = check.Problems.Count == 0;
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			ShowError(ex.Message);
		}
		finally
		{
			CheckingPanel.Visibility = Visibility.Collapsed;
		}
	}

	private async void Publish_Click(object sender, RoutedEventArgs e)
	{
		PublishButton.IsEnabled = false;
		ErrorText.Visibility = Visibility.Collapsed;
		CheckingText.Text = Strings.Ui_VoidstrapMods_PublishWorking;
		CheckingPanel.Visibility = Visibility.Visible;
		try
		{
			_pullRequestUrl = await GitHubPublisher.PublishAsync(_mod, NotesBox.Text, null, _cts.Token);
			ResultPanel.Visibility = Visibility.Collapsed;
			DonePanel.Visibility = Visibility.Visible;
			PublishButton.Visibility = Visibility.Collapsed;
			CloseButton.Content = Strings.Common_Close;
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			ShowError(ex.Message);
			PublishButton.IsEnabled = true;
		}
		finally
		{
			CheckingPanel.Visibility = Visibility.Collapsed;
		}
	}

	private void OpenPr_Click(object sender, RoutedEventArgs e)
	{
		if (_pullRequestUrl != null)
			Utilities.OpenWebLink(_pullRequestUrl);
	}

	private void ShowError(string message)
	{
		ErrorText.Text = string.Format(Strings.Ui_VoidstrapMods_PublishFailed, message);
		ErrorText.Visibility = Visibility.Visible;
	}

	private void OnDialogClosed(object? sender, EventArgs e)
	{
		Loaded -= OnDialogLoaded;
		Closed -= OnDialogClosed;
		_cts.Cancel();
		_cts.Dispose();
	}
}
