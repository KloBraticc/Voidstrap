using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.About.Pages;

public partial class LicensesPage : UiPage{

	private static Task<string[]>? _notices;

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
			LicenseNotices.ItemsSource = await _notices;
		}
		catch (Exception ex)
		{
			_notices = null;
			App.Logger.WriteException("LicensesPage", ex);
			LicenseNotices.ItemsSource = new[] { "The license notices could not be loaded." };
		}
	}

	private static string[] ReadNotices()
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
		return lines.ToArray();
	}
}
