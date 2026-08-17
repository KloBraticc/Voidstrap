using System;
using System.CodeDom.Compiler;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Dialogs;
using Voidstrap.Utility;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class AddCustomThemeDialog : WpfUiWindow{
	private const int CreateNewTabId = 0;

	private const int ImportTabId = 1;

	private readonly AddCustomThemeViewModel _viewModel;

	public bool Created { get; private set; }

	public string ThemeName { get; private set; } = "";

	public bool OpenEditor { get; private set; }

	public AddCustomThemeDialog()
	{
		_viewModel = new AddCustomThemeViewModel();
		_viewModel.Name = GenerateRandomName();
		base.DataContext = _viewModel;
		InitializeComponent();
	}

	private static string GetThemePath(string name)
	{
		return Path.Combine(Paths.CustomThemes, name, "Theme.xml");
	}

	private static string GenerateRandomName()
	{
		int num = Directory.GetDirectories(Paths.CustomThemes).Count();
		string text = $"Custom Theme {num + 1}";
		if (File.Exists(GetThemePath(text)))
		{
			text = text + " " + Random.Shared.Next(1, 100000);
		}
		return text;
	}

	private static string GetUniqueName(string name)
	{
		if (!Directory.Exists(Path.Combine(Paths.CustomThemes, name)))
		{
			return name;
		}
		for (int i = 1; i <= 100; i++)
		{
			string text = $"{name}_{i}";
			if (!Directory.Exists(Path.Combine(Paths.CustomThemes, text)))
			{
				return text;
			}
		}
		return $"{name}_{Random.Shared.Next(101, 1000000)}";
	}

	private static void CreateCustomTheme(string name, CustomThemeTemplate template)
	{
		string destination = Path.Combine(Paths.CustomThemes, name);
		if (Directory.Exists(destination))
		{
			throw new IOException("A theme with this name already exists");
		}
		string staging = Path.Combine(Paths.CustomThemes, "." + Guid.NewGuid().ToString("N") + ".staging");
		try
		{
			Directory.CreateDirectory(staging);
			File.WriteAllText(Path.Combine(staging, "Theme.xml"), Encoding.UTF8.GetString(Resource.Get(template.GetFileName())));
			if (template == CustomThemeTemplate.Html)
			{
				File.WriteAllText(Path.Combine(staging, "panel.html"), Encoding.UTF8.GetString(Resource.Get("CustomBootstrapperTemplate_Panel.html")));
				WriteThemeIcon(Path.Combine(staging, "Icon.png"));
			}
			Directory.Move(staging, destination);
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, true);
			}
		}
	}

	private static void WriteThemeIcon(string target)
	{
		try
		{
			var source = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Voidstrap.png"));

			if (source == null)
				return;

			using Stream input = source.Stream;
			using FileStream output = File.Create(target);
			input.CopyTo(output);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AddCustomThemeDialog::WriteThemeIcon", "Could not add the icon: " + ex.Message);
		}
	}

	private bool ValidateCreateNew()
	{
		if (string.IsNullOrEmpty(_viewModel.Name))
		{
			_viewModel.NameError = Strings.CustomTheme_Add_Errors_NameEmpty;
			return false;
		}
		PathValidator.ValidationResult validationResult = PathValidator.IsFileNameValid(_viewModel.Name);
		if (validationResult != PathValidator.ValidationResult.Ok)
		{
			switch (validationResult)
			{
			case PathValidator.ValidationResult.IllegalCharacter:
				_viewModel.NameError = Strings.CustomTheme_Add_Errors_NameIllegalCharacters;
				break;
			case PathValidator.ValidationResult.ReservedFileName:
				_viewModel.NameError = Strings.CustomTheme_Add_Errors_NameReserved;
				break;
			default:
				App.Logger.WriteLine("AddCustomThemeDialog::ValidateCreateNew", $"Got unhandled PathValidator::ValidationResult {validationResult}");
				_viewModel.NameError = Strings.CustomTheme_Add_Errors_Unknown;
				break;
			}
			return false;
		}
		if (Directory.Exists(Path.Combine(Paths.CustomThemes, _viewModel.Name)))
		{
			_viewModel.NameError = Strings.CustomTheme_Add_Errors_NameTaken;
			return false;
		}
		return true;
	}

	private bool ValidateImport()
	{
		if (!_viewModel.FilePath.EndsWith(".zip"))
		{
			_viewModel.FileError = Strings.CustomTheme_Add_Errors_FileNotZip;
			return false;
		}
		try
		{
			using ZipArchive zipArchive = System.IO.Compression.ZipFile.OpenRead(_viewModel.FilePath);
			ReadOnlyCollection<ZipArchiveEntry> entries = zipArchive.Entries;
			bool flag = false;
			foreach (ZipArchiveEntry item in entries)
			{
				if (item.FullName == "Theme.xml")
				{
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				_viewModel.FileError = Strings.CustomTheme_Add_Errors_ZipMissingThemeFile;
				return false;
			}
			return true;
		}
		catch (InvalidDataException ex)
		{
			App.Logger.WriteLine("AddCustomThemeDialog::ValidateImport", "Got invalid data");
			App.Logger.WriteException("AddCustomThemeDialog::ValidateImport", ex);
			_viewModel.FileError = Strings.CustomTheme_Add_Errors_ZipInvalidData;
			return false;
		}
	}

	private void CreateNew()
	{
		if (ValidateCreateNew())
		{
			CreateCustomTheme(_viewModel.Name, _viewModel.Template);
			Created = true;
			ThemeName = _viewModel.Name;
			OpenEditor = true;
			Close();
		}
	}

	private void Import()
	{
		if (!ValidateImport())
		{
			return;
		}
		string uniqueName = GetUniqueName(Path.GetFileNameWithoutExtension(_viewModel.FilePath));
		string destination = Path.Combine(Paths.CustomThemes, uniqueName);
		string staging = Path.Combine(Paths.CustomThemes, "." + Guid.NewGuid().ToString("N") + ".staging");
		try
		{
			Voidstrap.Utility.SafeZipExtractor.ExtractToDirectory(_viewModel.FilePath, staging, true, 536870912L, 10000);
			if (!File.Exists(Path.Combine(staging, "Theme.xml")))
			{
				throw new InvalidDataException("The archive has no root theme file");
			}
			Directory.Move(staging, destination);
			Created = true;
			ThemeName = uniqueName;
			OpenEditor = false;
			Close();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("AddCustomThemeDialog::Import", ex);
			_viewModel.FileError = Strings.CustomTheme_Add_Errors_ZipInvalidData;
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, true);
			}
		}
	}

	private void OnOkButtonClicked(object sender, RoutedEventArgs e)
	{
		if (_viewModel.SelectedTab == 0)
		{
			CreateNew();
		}
		else
		{
			Import();
		}
	}

	private void OnImportButtonClicked(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = Strings.FileTypes_ZipArchive + "|*.zip"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			_viewModel.FilePath = openFileDialog.FileName;
		}
	}
}
