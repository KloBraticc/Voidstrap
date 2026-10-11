using System;
using System.IO;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.About.Pages;

public partial class LicensesPage : UiPage{

	public LicensesPage()
	{
		InitializeComponent();
		using Stream stream = typeof(LicensesPage).Assembly.GetManifestResourceStream("Voidstrap.Licenses.Notices")
			?? throw new InvalidOperationException("The bundled license notices are missing.");
		using StreamReader reader = new(stream);
		LicenseNotices.Text = reader.ReadToEnd();
	}
}
