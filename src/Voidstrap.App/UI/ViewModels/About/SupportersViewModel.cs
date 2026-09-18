using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Voidstrap.Enums;
using Voidstrap.Models.APIs.Config;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.About;

public class SupportersViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
	private bool _disposed;

	private const string SupportersUrl = "https://raw.githubusercontent.com/KloBraticc/Voidstrap/main/assets/supportersdata7.json";

	private const string SupportersFallbackUrl = "https://cdn.jsdelivr.net/gh/KloBraticc/Voidstrap@main/assets/supportersdata7.json";

	private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);

	private const double ColumnWidth = 200.0;

	private const double ColumnHysteresis = 24.0;

	public SizeChangedEventHandler? WindowResizeEvent;

	public SupporterData? SupporterData { get; private set; }

	public GenericTriState LoadedState { get; set; } = GenericTriState.Unknown;

	public string LoadError { get; set; } = "";

	public int Columns { get; set; } = 3;

	public ICommand RetryCommand => new Voidstrap.UI.ViewModels.ContextMenu.RelayCommand(Retry);

	public SupportersViewModel()
	{
		WindowResizeEvent = (SizeChangedEventHandler)Delegate.Combine(WindowResizeEvent, new SizeChangedEventHandler(OnWindowResize));
		_ = LoadSupporterDataAsync();
	}

	private void OnWindowResize(object sender, SizeChangedEventArgs e)
	{
		if (e.WidthChanged)
		{
			Size newSize = e.NewSize;
			int num = (int)Math.Floor(newSize.Width / ColumnWidth);
			if (num < 1)
			{
				num = 1;
			}

			if (num == Columns)
			{
				return;
			}

			if (num > Columns && newSize.Width < (Columns + 1) * ColumnWidth + ColumnHysteresis)
			{
				return;
			}

			if (num < Columns && newSize.Width > Columns * ColumnWidth - ColumnHysteresis)
			{
				return;
			}

			Columns = num;
			OnPropertyChanged(nameof(Columns));
		}
	}

	private void Retry()
	{
		if (_disposed || LoadedState == GenericTriState.Unknown)
		{
			return;
		}
		_ = LoadSupporterDataAsync(forceRefresh: true);
	}

	public async Task LoadSupporterDataAsync(bool forceRefresh = false)
	{
		LoadedState = GenericTriState.Unknown;
		OnPropertyChanged(nameof(LoadedState));
		SupporterData? data = null;
		string error = "";
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
		timeout.CancelAfter(LoadTimeout);
		try
		{
			TimeSpan maxAge = forceRefresh ? TimeSpan.Zero : TimeSpan.FromHours(1);
			data = await GitHubCache.GetJsonWithFallbackAsync<SupporterData>(SupportersUrl, SupportersFallbackUrl, maxAge, timeout.Token);
		}
		catch (OperationCanceledException)
		{
			if (_lifetimeCts.IsCancellationRequested)
			{
				return;
			}
			error = "The request timed out.";
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("SupportersViewModel::LoadSupporterData", "Could not load supporter data");
			App.Logger.WriteException("SupportersViewModel::LoadSupporterData", ex);
			error = ex.Message;
		}
		if (_disposed)
		{
			return;
		}
		if (data == null)
		{
			LoadedState = GenericTriState.Failed;
			LoadError = string.IsNullOrEmpty(error) ? "The supporter list could not be downloaded. Check your connection and try again." : error;
			OnPropertyChanged(nameof(LoadError));
			OnPropertyChanged(nameof(LoadedState));
			return;
		}
		SupporterData = data;
		LoadedState = GenericTriState.Successful;
		OnPropertyChanged(nameof(SupporterData));
		OnPropertyChanged(nameof(LoadedState));
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		WindowResizeEvent = null;
		_lifetimeCts.Cancel();
		_lifetimeCts.Dispose();
		GC.SuppressFinalize(this);
	}
}
