using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;
using Windows.Win32;
using Windows.Win32.Foundation;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class ExceptionDialog : WpfUiWindow{
	private static readonly int MaxGitHubUrlLength = 8192;

	private static readonly int MaxLogLength = 7000;
	private readonly string _issueUrl;
	private readonly string _backupIssueUrl;

	public ExceptionDialog(Exception exception)
	{
		InitializeComponent();
		AddExceptionToTextBox(exception);
		if (!App.Logger.Initialized)
		{
			LocateLogFileButton.Content = Strings.Dialog_Exception_CopyLogContents;
		}
		string text = "https://github.com/KloBraticc/Voidstrap";
		string wikiUrl = App.WebsiteBaseUrl + "/documentation/documentation";
		string text2 = HttpUtility.UrlEncode($"[BUG] {exception.GetType()}: {exception.Message}");
		string value = HttpUtility.UrlEncode((App.Logger.AsDocument.Length > MaxLogLength) ? App.Logger.AsDocument.Substring(0, MaxLogLength) : App.Logger.AsDocument);
		_issueUrl = $"{text}/issues/new?template=bug_report.yaml&title={text2}&log={value}";
		if (_issueUrl.Length > MaxGitHubUrlLength)
		{
			_issueUrl = text + "/issues/new?template=bug_report.yaml&title=" + text2;
			if (_issueUrl.Length > MaxGitHubUrlLength)
			{
				_issueUrl = text + "/issues/new?template=bug_report.yaml";
			}
		}
		HelpMessageMDTextBlock.MarkdownText = GetHelpMessage(wikiUrl, _issueUrl);
		VersionText.Text = string.Format(Strings.Dialog_Exception_Version, App.Version);
		ReportExceptionButton.Click += OnReportExceptionClick;
		_backupIssueUrl = _issueUrl.Replace(text, App.ProjectFallbackRepository);
		ReportExceptionBackupButton.Click += OnReportExceptionBackupClick;
		LocateLogFileButton.Click += OnLocateLogFileClick;
		CopyDetailsButton.Click += OnCopyDetailsClick;
		CloseButton.Click += OnCloseClick;
		base.Closed += OnDialogClosed;
		Voidstrap.Utility.SafeSystemSounds.Play(Voidstrap.Utility.SafeSystemSounds.Get(MessageBoxImage.Hand));
		base.Loaded += OnLoaded;
	}

	private string _exceptionText = string.Empty;

	private System.Windows.Threading.DispatcherTimer? _copyFeedbackTimer;

	private void AddExceptionToTextBox(Exception exception)
	{
		AppendException(exception, isInner: false);
		_exceptionText = ErrorRichTextBox.Selection.Text;
		void AppendException(Exception ex, bool isInner)
		{
			if (ex != null)
			{
				if (!isInner)
				{
					ErrorRichTextBox.Selection.Text = $"{ex.GetType()}: {ex.Message}";
				}
				else
				{
					ErrorRichTextBox.Selection.Text += $"\n\n[Inner Exception]\n{ex.GetType()}: {ex.Message}";
				}
				AppendException(ex.InnerException, isInner: true);
			}
		}
	}

	private void OnCopyDetailsClick(object sender, RoutedEventArgs e)
	{
		string details = _exceptionText;
		if (string.IsNullOrWhiteSpace(details))
		{
			details = new TextRange(ErrorRichTextBox.Document.ContentStart, ErrorRichTextBox.Document.ContentEnd).Text;
		}
		string payload = string.Format(Strings.Dialog_Exception_Version, App.Version) + Environment.NewLine + Environment.NewLine + details.Trim();
		try
		{
			Clipboard.SetDataObject(payload, copy: true);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ExceptionDialog::Copy", "Clipboard write failed: " + ex.Message);
			return;
		}
		ShowCopyFeedback();
	}

	private void ShowCopyFeedback()
	{
		CopyDetailsButton.Content = "Copied";
		if (_copyFeedbackTimer == null)
		{
			_copyFeedbackTimer = new System.Windows.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(1600.0)
			};
			_copyFeedbackTimer.Tick += OnCopyFeedbackTick;
		}
		_copyFeedbackTimer.Stop();
		_copyFeedbackTimer.Start();
	}

	private void OnCopyFeedbackTick(object? sender, EventArgs e)
	{
		_copyFeedbackTimer?.Stop();
		CopyDetailsButton.Content = "Copy";
	}

	private void OnDialogClosed(object? sender, EventArgs e)
	{
		base.Closed -= OnDialogClosed;
		base.Loaded -= OnLoaded;
		ReportExceptionButton.Click -= OnReportExceptionClick;
		ReportExceptionBackupButton.Click -= OnReportExceptionBackupClick;
		CloseButton.Click -= OnCloseClick;
		CopyDetailsButton.Click -= OnCopyDetailsClick;
		LocateLogFileButton.Click -= OnLocateLogFileClick;
		if (_copyFeedbackTimer != null)
		{
			_copyFeedbackTimer.Stop();
			_copyFeedbackTimer.Tick -= OnCopyFeedbackTick;
			_copyFeedbackTimer = null;
		}
	}

	private void OnReportExceptionClick(object sender, RoutedEventArgs e)
	{
		Utilities.ShellExecute(_issueUrl);
	}

	private void OnReportExceptionBackupClick(object sender, RoutedEventArgs e)
	{
		Utilities.ShellExecute(_backupIssueUrl);
	}

	private void OnCloseClick(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		FlashWindowOnLoad();
	}

	private string GetHelpMessage(string wikiUrl, string issueUrl)
	{
		if (!App.IsActionBuild && !App.BuildMetadata.Machine.Contains("pizzaboxer", StringComparison.Ordinal))
		{
			return string.Format(Strings.Dialog_Exception_Info_2_Alt, wikiUrl);
		}
		return string.Format(Strings.Dialog_Exception_Info_2, wikiUrl, issueUrl);
	}

	private void OnLocateLogFileClick(object sender, RoutedEventArgs e)
	{
		if (App.Logger.Initialized && !string.IsNullOrEmpty(App.Logger.FileLocation))
		{
			Utilities.ShellExecute(App.Logger.FileLocation);
		}
		else
		{
			Clipboard.SetDataObject(App.Logger.AsDocument);
		}
	}

	private void FlashWindowOnLoad()
	{
		if (Voidstrap.Utility.Platform.IsWindows) { Windows.Win32.PInvoke.FlashWindow((HWND)new WindowInteropHelper(this).Handle, true); }
	}
}
