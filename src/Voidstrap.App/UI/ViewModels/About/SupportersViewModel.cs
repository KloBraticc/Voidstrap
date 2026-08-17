using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Voidstrap.Enums;
using Voidstrap.Models.APIs.Config;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.About;

public class SupportersViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
	private bool _disposed;

	public SizeChangedEventHandler? WindowResizeEvent;

	public SupporterData? SupporterData { get; private set; }

	public GenericTriState LoadedState { get; set; } = GenericTriState.Unknown;

	public string LoadError { get; set; } = "";

	public int Columns { get; set; } = 3;

	public SupportersViewModel()
	{
		WindowResizeEvent = (SizeChangedEventHandler)Delegate.Combine(WindowResizeEvent, new SizeChangedEventHandler(OnWindowResize));
		_ = LoadSupporterDataAsync();
	}

	private void OnWindowResize(object sender, SizeChangedEventArgs e)
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		if (e.WidthChanged)
		{
			Size newSize = e.NewSize;
			int num = (int)Math.Floor(newSize.Width / 200.0);
			if (Columns != num)
			{
				Columns = num;
				OnPropertyChanged("Columns");
			}
		}
	}

	public async Task LoadSupporterDataAsync()
	{
		try
		{
			SupporterData = await GitHubCache.GetJsonAsync<SupporterData>("https://raw.githubusercontent.com/KloBraticc/Voidstrap/main/supportersdata7.json", TimeSpan.FromHours(1), _lifetimeCts.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("AboutViewModel::LoadSupporterData", "Could not load supporter data");
			App.Logger.WriteException("AboutViewModel::LoadSupporterData", ex);
			LoadedState = GenericTriState.Failed;
			LoadError = ex.Message;
			OnPropertyChanged("LoadError");
		}
		if (SupporterData != null)
		{
			LoadedState = GenericTriState.Successful;
			OnPropertyChanged("SupporterData");
		}
		OnPropertyChanged("LoadedState");
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
