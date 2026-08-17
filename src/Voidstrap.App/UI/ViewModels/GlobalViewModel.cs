using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace Voidstrap.UI.ViewModels;

public static class GlobalViewModel
{
	public static ICommand OpenWebpageCommand => new RelayCommand<string>(OpenWebpage);

	private static void OpenWebpage(string? location)
	{
		if (location != null)
		{
			Utilities.ShellExecute(location);
		}
	}
}
