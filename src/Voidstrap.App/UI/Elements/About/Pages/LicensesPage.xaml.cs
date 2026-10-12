using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.About.Pages;

public partial class LicensesPage : UiPage{

	public sealed record Notice(string Title, string Detail, string[] Lines);

	private static Task<List<Notice>>? _notices;

	public LicensesPage()
	{
		InitializeComponent();
		Loaded += OnLoaded;
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnLoaded;
		try
		{
			_notices ??= Task.Run(ReadNotices);
			List<Notice> notices = await _notices;
			NoticeRows.ItemsSource = notices;
			NoticesStatus.Visibility = Visibility.Collapsed;
		}
		catch (Exception ex)
		{
			_notices = null;
			App.Logger.WriteException("LicensesPage", ex);
			NoticesStatus.Text = "The license notices could not be loaded.";
		}
	}

	private void OnNoticeClick(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is not Notice notice)
			return;
		DetailTitle.Text = notice.Title;
		DetailLines.ItemsSource = notice.Lines;
		ListView.Visibility = Visibility.Collapsed;
		DetailView.Visibility = Visibility.Visible;
		if (notice.Lines.Length > 0)
			DetailLines.ScrollIntoView(notice.Lines[0]);
	}

	private void OnBackClick(object sender, RoutedEventArgs e)
	{
		DetailView.Visibility = Visibility.Collapsed;
		DetailLines.ItemsSource = null;
		ListView.Visibility = Visibility.Visible;
	}

	private static List<Notice> ReadNotices()
	{
		Stream? compressed = typeof(LicensesPage).Assembly.GetManifestResourceStream("Voidstrap.Licenses.Notices.gz");
		using Stream stream = compressed is not null
			? new GZipStream(compressed, CompressionMode.Decompress)
			: typeof(LicensesPage).Assembly.GetManifestResourceStream("Voidstrap.Licenses.Notices")
			?? throw new InvalidOperationException("The bundled license notices are missing.");
		using StreamReader reader = new(stream);
		List<string> lines = new();
		while (reader.ReadLine() is string line)
			lines.Add(line);

		List<Notice> notices = new();
		int components = lines.FindIndex(line => line.StartsWith("## Components", StringComparison.Ordinal));
		int texts = lines.FindIndex(line => line.StartsWith("## Full license texts", StringComparison.Ordinal));
		if (components >= 0)
		{
			int end = texts > components ? texts : lines.Count;
			string[] inventory = lines.GetRange(components + 1, end - components - 1).ToArray();
			notices.Add(new Notice("Component inventory", "Every bundled component with its terms and source", inventory));
		}

		int start = -1;
		for (int index = Math.Max(0, texts); index <= lines.Count; index++)
		{
			bool boundary = index == lines.Count || lines[index].StartsWith("### ", StringComparison.Ordinal);
			if (!boundary)
				continue;
			if (start >= 0)
				notices.Add(CreateNotice(lines, start + 1, index));
			start = index;
		}
		return notices;
	}

	private static Notice CreateNotice(List<string> lines, int from, int to)
	{
		string title = string.Empty;
		string detail = string.Empty;
		int body = from;
		for (int index = from; index < to; index++)
		{
			string line = lines[index].Trim();
			if (line.Length == 0)
				continue;
			if (title.Length == 0)
			{
				title = line;
				body = index + 1;
				continue;
			}
			if (line.StartsWith("Origin:", StringComparison.Ordinal))
			{
				detail = line["Origin:".Length..].Trim();
				body = index + 1;
			}
			break;
		}
		while (body < to && lines[body].Trim().Length == 0)
			body++;
		return new Notice(title.Length > 0 ? title : "Notice", detail, lines.GetRange(body, Math.Max(0, to - body)).ToArray());
	}
}
