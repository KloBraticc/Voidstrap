using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Installer;

public class MainWindowViewModel : NotifyPropertyChangedViewModel
{
	public string NextButtonText { get; private set; } = Strings.Common_Navigation_Next;

	public int CurrentStep { get; private set; }

	public string StepHeading { get; private set; } = Strings.Installer_Welcome_Title;

	public void SetStep(int index, string heading)
	{
		CurrentStep = index;
		StepHeading = heading;
		OnPropertyChanged("CurrentStep");
		OnPropertyChanged("StepHeading");
	}

	public bool BackButtonEnabled { get; private set; }

	public bool NextButtonEnabled { get; private set; }

	public int ButtonWidth { get; } = Locale.CurrentCulture.Name.StartsWith("bg") ? 112 : 96;

	public ICommand BackPageCommand => new RelayCommand(BackPage);

	public ICommand NextPageCommand => new RelayCommand(NextPage);

	public ICommand CloseWindowCommand => new RelayCommand(CloseWindow);

	public event EventHandler<string>? PageRequest;

	public event EventHandler? CloseWindowRequest;

	public void SetButtonEnabled(string type, bool state)
	{
		if (type == "next")
		{
			NextButtonEnabled = state;
			OnPropertyChanged("NextButtonEnabled");
		}
		else if (type == "back")
		{
			BackButtonEnabled = state;
			OnPropertyChanged("BackButtonEnabled");
		}
	}

	public void SetNextButtonText(string text)
	{
		NextButtonText = text;
		OnPropertyChanged("NextButtonText");
	}

	private void BackPage()
	{
		this.PageRequest?.Invoke(this, "back");
	}

	private void NextPage()
	{
		this.PageRequest?.Invoke(this, "next");
	}

	private void CloseWindow()
	{
		this.CloseWindowRequest?.Invoke(this, new EventArgs());
	}
}
