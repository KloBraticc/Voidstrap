using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;

namespace Voidstrap.UI.ViewModels.ContextMenu;

internal sealed class OutputConsoleViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private readonly ActivityWatcher _activityWatcher;
	private readonly ConcurrentQueue<ActivityData.UserLog> _pendingEntries = new();
	private readonly ConcurrentQueue<ActivityData.UserLog> _pendingUpdates = new();
	private readonly DispatcherTimer _flushTimer;
	private bool _disposed;

	public event EventHandler? RequestCloseEvent;
	public ObservableCollection<ActivityData.UserLog> PlayerLogsCollection { get; } = new();
	public ICommand CloseWindowCommand { get; }
	public ICommand ExportCommand { get; }
	public ICommand ClearCommand { get; }
	public bool HasEntries => PlayerLogsCollection.Count != 0;
	public string StatusText => PlayerLogsCollection.Count == 1 ? "1 player event" : PlayerLogsCollection.Count + " player events";

	public OutputConsoleViewModel(ActivityWatcher activityWatcher)
	{
		_activityWatcher = activityWatcher ?? throw new ArgumentNullException(nameof(activityWatcher));
		CloseWindowCommand = new RelayCommand(RequestClose);
		ExportCommand = new RelayCommand(Export);
		ClearCommand = new RelayCommand(Clear);
		foreach (ActivityData.UserLog entry in _activityWatcher.GetPlayerLogSnapshot())
			PlayerLogsCollection.Add(entry);
		_activityWatcher.OnNewPlayerRequest += OnNewPlayer;
		_activityWatcher.OnPlayerLogUpdated += OnPlayerUpdated;
		_flushTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(500)
		};
		_flushTimer.Tick += FlushTimer_Tick;
		_flushTimer.Start();
	}

	private void OnNewPlayer(object? sender, ActivityData.UserLog entry)
	{
		if (!_disposed)
			_pendingEntries.Enqueue(entry);
	}

	private void OnPlayerUpdated(object? sender, ActivityData.UserLog entry)
	{
		if (!_disposed)
			_pendingUpdates.Enqueue(entry);
	}

	private void FlushTimer_Tick(object? sender, EventArgs e)
	{
		if (_disposed)
			return;
		bool changed = false;
		while (_pendingEntries.TryDequeue(out ActivityData.UserLog? entry))
		{
			PlayerLogsCollection.Add(entry);
			changed = true;
		}
		while (PlayerLogsCollection.Count > ActivityWatcher.MaxPlayerLogEntries)
		{
			PlayerLogsCollection.RemoveAt(0);
			changed = true;
		}
		while (_pendingUpdates.TryDequeue(out ActivityData.UserLog? updated))
		{
			int index = PlayerLogsCollection.IndexOf(updated);
			if (index >= 0)
			{
				PlayerLogsCollection[index] = updated;
				changed = true;
			}
		}
		if (changed)
		{
			OnPropertyChanged(nameof(HasEntries));
			OnPropertyChanged(nameof(StatusText));
		}
	}

	private void Clear()
	{
		PlayerLogsCollection.Clear();
		while (_pendingEntries.TryDequeue(out _))
		{
		}
		while (_pendingUpdates.TryDequeue(out _))
		{
		}
		OnPropertyChanged(nameof(HasEntries));
		OnPropertyChanged(nameof(StatusText));
	}

	private void Export()
	{
		var dialog = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "Voidstrap Player Logs.csv" };
		if (dialog.ShowDialog() != true)
			return;
		try
		{
			var text = new StringBuilder("Time,User ID,Username,Type\r\n");
			foreach (ActivityData.UserLog entry in PlayerLogsCollection)
				text.Append(Csv(entry.Time.ToString("O"))).Append(',').Append(Csv(entry.UserId)).Append(',').Append(Csv(entry.Username)).Append(',').Append(Csv(entry.Type)).Append("\r\n");
			File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("OutputConsoleViewModel::Export", ex);
			MessageBox.Show("Player logs could not be saved: " + ex.Message, "Player Logs", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private static string Csv(string value)
	{
		if (value.Length > 0 && "=+-@".Contains(value[0]))
			value = "'" + value;
		return '"' + value.Replace("\"", "\"\"") + '"';
	}
	private void RequestClose() => RequestCloseEvent?.Invoke(this, EventArgs.Empty);

	public void Dispose()
	{
		if (_disposed)
			return;
		_flushTimer.Stop();
		_flushTimer.Tick -= FlushTimer_Tick;
		_activityWatcher.OnNewPlayerRequest -= OnNewPlayer;
		_activityWatcher.OnPlayerLogUpdated -= OnPlayerUpdated;
		RequestCloseEvent = null;
		_disposed = true;
		GC.SuppressFinalize(this);
	}
}
