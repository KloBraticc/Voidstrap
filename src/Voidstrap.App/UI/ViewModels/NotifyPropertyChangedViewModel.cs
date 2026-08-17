using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace Voidstrap.UI.ViewModels;

public class NotifyPropertyChangedViewModel : INotifyPropertyChanged
{
	public static ICommand OpenModsFolderCommand => new RelayCommand(delegate
	{
		Process.Start("explorer.exe", Paths.Mods);
	});

	public event PropertyChangedEventHandler? PropertyChanged;

	public void OnPropertyChanged([CallerMemberName] string propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
