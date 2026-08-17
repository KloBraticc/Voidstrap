using System;
using System.Windows;
using Voidstrap.Enums;

namespace Voidstrap.UI.ViewModels.Dialogs;

internal class AddCustomThemeViewModel : NotifyPropertyChangedViewModel
{
	private string _filePath = "";

	private string _nameError = "";

	private string _fileError = "";

	public static CustomThemeTemplate[] Templates => Enum.GetValues<CustomThemeTemplate>();

	public CustomThemeTemplate Template { get; set; } = CustomThemeTemplate.Simple;

	public string Name { get; set; } = "";

	public string FilePath
	{
		get
		{
			return _filePath;
		}
		set
		{
			if (_filePath != value)
			{
				_filePath = value;
				OnPropertyChanged("FilePath");
				OnPropertyChanged("FilePathVisibility");
			}
		}
	}

	public Visibility FilePathVisibility
	{
		get
		{
			if (!string.IsNullOrEmpty(FilePath))
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public int SelectedTab { get; set; }

	public string NameError
	{
		get
		{
			return _nameError;
		}
		set
		{
			if (_nameError != value)
			{
				_nameError = value;
				OnPropertyChanged("NameError");
				OnPropertyChanged("NameErrorVisibility");
			}
		}
	}

	public Visibility NameErrorVisibility
	{
		get
		{
			if (!string.IsNullOrEmpty(NameError))
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public string FileError
	{
		get
		{
			return _fileError;
		}
		set
		{
			if (_fileError != value)
			{
				_fileError = value;
				OnPropertyChanged("FileError");
				OnPropertyChanged("FileErrorVisibility");
			}
		}
	}

	public Visibility FileErrorVisibility
	{
		get
		{
			if (!string.IsNullOrEmpty(FileError))
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}
}
