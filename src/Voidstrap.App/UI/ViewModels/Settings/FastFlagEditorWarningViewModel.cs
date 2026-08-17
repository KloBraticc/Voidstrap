using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Settings.Pages;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.ViewModels.Settings;

internal class FastFlagEditorWarningViewModel : NotifyPropertyChangedViewModel
{
	private Page _page;

	private CancellationTokenSource? _cancellationTokenSource;

	public string ContinueButtonText { get; set; } = "";

	public bool CanContinue { get; set; } = true;

	public FastFlagEditorWarningViewModel(Page page)
	{
		_page = page;
	}

	public void StopCountdown()
	{
		CancellationTokenSource? cancellationTokenSource = Interlocked.Exchange(ref _cancellationTokenSource, null);
		cancellationTokenSource?.Cancel();
		cancellationTokenSource?.Dispose();
	}

	public void StartCountdown()
	{
		StopCountdown();
		CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		_cancellationTokenSource = cancellationTokenSource;
		_ = DoCountdownAsync(cancellationTokenSource.Token);
	}

	private async Task DoCountdownAsync(CancellationToken token)
	{
		CanContinue = false;
		OnPropertyChanged("CanContinue");
		for (int i = 10; i > 0; i--)
		{
			ContinueButtonText = $"({i}) {Strings.Menu_FastFlagEditor_Warning_Continue}";
			OnPropertyChanged("ContinueButtonText");
			try
			{
				await Task.Delay(1000, token);
			}
			catch (TaskCanceledException)
			{
				return;
			}
		}
		ContinueButtonText = Strings.Menu_FastFlagEditor_Warning_Continue;
		OnPropertyChanged("ContinueButtonText");
		CanContinue = true;
		OnPropertyChanged("CanContinue");
	}

	private void Continue()
	{
		if (CanContinue)
		{
			App.State.Save();
			if (Window.GetWindow((DependencyObject)(object)_page) is INavigationWindow navigationWindow)
			{
				navigationWindow.Navigate(typeof(FastFlagEditorPage));
			}
		}
	}

	private void GoBack()
	{
		if (Window.GetWindow((DependencyObject)(object)_page) is INavigationWindow navigationWindow)
		{
			navigationWindow.Navigate(typeof(FastFlagsPage));
		}
	}
}
