using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Voidstrap.Integrations.GameChat;
using Voidstrap.Models.Entities;

namespace Voidstrap.UI.ViewModels.ContextMenu;

internal sealed class ChatLogsViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private const int MaxLogRows = 2000;

	private bool _disposed;

	private readonly ConcurrentQueue<ActivityData.UserMessage> _incoming = new();

	private int _drainScheduled;

	public ObservableCollection<ActivityData.UserMessage> MessageLogsCollection { get; } = new();
	public ICommand CloseWindowCommand { get; }
	public ICommand ExportCommand { get; }
	public event EventHandler? RequestCloseEvent;

	public ChatLogsViewModel()
	{
		CloseWindowCommand = new RelayCommand(RequestClose);
		ExportCommand = new RelayCommand(Export);
		foreach (ActivityData.UserMessage entry in GameChatLog.Snapshot())
			MessageLogsCollection.Add(entry);
		GameChatLog.Added += OnMessageAdded;
	}

	private void OnMessageAdded(object? sender, ActivityData.UserMessage entry)
	{
		if (_disposed)
			return;

		if (entry != null)
			_incoming.Enqueue(entry);
		while (_incoming.Count > MaxLogRows * 2 && _incoming.TryDequeue(out _))
		{
		}

		if (Interlocked.Exchange(ref _drainScheduled, 1) != 0)
			return;

		var dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
		{
			Interlocked.Exchange(ref _drainScheduled, 0);
			return;
		}

		try
		{
			dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(DrainIncoming));
		}
		catch (InvalidOperationException)
		{
			Interlocked.Exchange(ref _drainScheduled, 0);
		}
	}

	private void DrainIncoming()
	{
		Interlocked.Exchange(ref _drainScheduled, 0);
		if (_disposed)
			return;

		int added = 0;
		while (_incoming.TryDequeue(out ActivityData.UserMessage? entry))
		{
			MessageLogsCollection.Add(entry);
			added++;
		}
		if (added == 0)
			return;

		int overflow = MessageLogsCollection.Count - MaxLogRows;
		if (overflow > 0)
		{
			List<ActivityData.UserMessage> kept = new List<ActivityData.UserMessage>(MaxLogRows);
			for (int i = overflow; i < MessageLogsCollection.Count; i++)
				kept.Add(MessageLogsCollection[i]);
			MessageLogsCollection.Clear();
			foreach (ActivityData.UserMessage keep in kept)
				MessageLogsCollection.Add(keep);
		}

		if (!_incoming.IsEmpty)
			OnMessageAdded(this, null!);
	}

	private void Export()
	{
		var dialog = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "Voidstrap Chat Logs.csv" };
		if (dialog.ShowDialog() != true)
			return;
		var text = new StringBuilder("Time,Channel,Sender,Message\r\n");
		foreach (ActivityData.UserMessage entry in MessageLogsCollection)
			text.Append(Csv(entry.Time.ToString("O"))).Append(',').Append(Csv(entry.Channel)).Append(',').Append(Csv(entry.Sender)).Append(',').Append(Csv(entry.Message)).Append("\r\n");
		File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
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
		GameChatLog.Added -= OnMessageAdded;
		RequestCloseEvent = null;
		_disposed = true;
		GC.SuppressFinalize(this);
	}
}
