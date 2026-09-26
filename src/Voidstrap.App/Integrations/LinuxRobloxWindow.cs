using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Voidstrap.Models.Entities;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations;

internal sealed class LinuxRobloxWindow : IDisposable
{
	private const string LOG_IDENT = "LinuxRobloxWindow";
	private const string DefaultTitle = "Voidstrap";
	private const int PollIntervalMs = 1000;
	private const int GameIconMaxBytes = 2 * 1024 * 1024;
	private static readonly int[] IconSizes = [32, 64];

	private readonly ActivityWatcher _activityWatcher;
	private readonly CancellationTokenSource _cts = new();
	private readonly object _gate = new();
	private readonly Dictionary<nint, nint[]> _originalIcons = new();
	private readonly HashSet<nint> _iconApplied = new();
	private string _title;
	private nint[] _gameIcon = [];
	private int _iconVersion;
	private int _appliedIconVersion = -1;
	private bool _titleReported;
	private bool _disposed;

	private LinuxRobloxWindow(ActivityWatcher activityWatcher)
	{
		_activityWatcher = activityWatcher;
		_title = BaseTitle;
	}

	public static bool IsEnabled
	{
		get
		{
			var prop = App.Settings.Prop;
			return prop.CycleTitleWithGameName
				|| prop.UseGameIconForRobloxWindow
				|| (!string.IsNullOrWhiteSpace(prop.RobloxTitle) && prop.RobloxTitle != DefaultTitle);
		}
	}

	private static string BaseTitle => string.IsNullOrWhiteSpace(App.Settings.Prop.RobloxTitle) ? DefaultTitle : App.Settings.Prop.RobloxTitle;

	public static LinuxRobloxWindow Start(ActivityWatcher activityWatcher)
	{
		LinuxRobloxWindow window = new(activityWatcher);
		activityWatcher.OnGameJoin += window.OnGameJoin;
		activityWatcher.OnGameLeave += window.OnGameLeave;
		if (activityWatcher.InGame && activityWatcher.Data?.UniverseId > 0)
			_ = window.RefreshForGameAsync(activityWatcher.Data.UniverseId);
		_ = Task.Run(() => window.LoopAsync(window._cts.Token));
		App.Logger?.WriteLine(LOG_IDENT, "Keeping the Sober window title as '" + window._title + "'");
		return window;
	}

	private async void OnGameJoin(object? sender, EventArgs e)
	{
		try
		{
			long universeId = _activityWatcher.Data?.UniverseId ?? 0;
			if (universeId > 0)
				await RefreshForGameAsync(universeId).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException(LOG_IDENT + "::OnGameJoin", ex);
		}
	}

	private void OnGameLeave(object? sender, EventArgs e)
	{
		lock (_gate)
		{
			_title = BaseTitle;
			_gameIcon = [];
			_iconVersion++;
		}
		App.Logger?.WriteLine(LOG_IDENT, "Left the game, reverting the Sober window title to '" + BaseTitle + "'");
	}

	private async Task RefreshForGameAsync(long universeId)
	{
		UniverseDetails? details = UniverseDetails.LoadFromCache(universeId);
		if (details?.Data == null || string.IsNullOrWhiteSpace(details.Thumbnail?.ImageUrl))
		{
			await UniverseDetails.FetchSingle(universeId).ConfigureAwait(false);
			details = UniverseDetails.LoadFromCache(universeId);
		}
		if (_disposed)
			return;

		if (App.Settings.Prop.CycleTitleWithGameName)
		{
			string gameName = details?.Data?.Name ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(gameName))
			{
				string title = BaseTitle + ": " + gameName;
				if (App.Settings.Prop.ShowServerInfoInTitle)
				{
					long playing = details?.Data?.Playing ?? 0;
					if (playing > 0)
						title += $" ({playing:N0} playing)";
				}
				lock (_gate)
					_title = title;
				App.Logger?.WriteLine(LOG_IDENT, "Updating the Sober window title to '" + title + "'");
			}
		}

		string? iconUrl = details?.Thumbnail?.ImageUrl;
		if (App.Settings.Prop.UseGameIconForRobloxWindow && !string.IsNullOrWhiteSpace(iconUrl))
		{
			nint[] icon = await DownloadIconAsync(iconUrl).ConfigureAwait(false);
			if (icon.Length > 2 && !_disposed)
			{
				lock (_gate)
				{
					_gameIcon = icon;
					_iconVersion++;
				}
				App.Logger?.WriteLine(LOG_IDENT, $"Applying the game icon for universe {universeId} to the Sober window");
			}
		}
	}

	private async Task LoopAsync(CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				Apply();
				await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException(LOG_IDENT + "::Loop", ex);
		}
	}

	private void Apply()
	{
		string title;
		nint[] icon;
		int iconVersion;
		lock (_gate)
		{
			title = _title;
			icon = _gameIcon;
			iconVersion = _iconVersion;
		}

		IReadOnlyList<nint> windows = LinuxWindowInterop.FindSoberWindows();
		if (windows.Count == 0)
			return;

		bool iconChanged = iconVersion != _appliedIconVersion;
		foreach (nint window in windows)
		{
			if (!string.Equals(LinuxWindowInterop.ReadWindowTitle(window), title, StringComparison.Ordinal)
				&& LinuxWindowInterop.TrySetWindowTitle(window, title)
				&& !_titleReported)
			{
				_titleReported = true;
				App.Logger?.WriteLine(LOG_IDENT, "Set the Sober window title to '" + title + "'");
			}

			if (!iconChanged && (icon.Length == 0 || _iconApplied.Contains(window)))
				continue;

			if (icon.Length > 2)
			{
				if (!_originalIcons.ContainsKey(window))
					_originalIcons[window] = LinuxWindowInterop.ReadWindowIcon(window);
				if (LinuxWindowInterop.TrySetWindowIcon(window, icon))
					_iconApplied.Add(window);
			}
			else if (_iconApplied.Remove(window) && _originalIcons.TryGetValue(window, out nint[]? original))
			{
				LinuxWindowInterop.TrySetWindowIcon(window, original);
			}
		}
		_appliedIconVersion = iconVersion;
	}

	private static async Task<nint[]> DownloadIconAsync(string url)
	{
		try
		{
			using HttpResponseMessage response = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
				return [];
			byte[] data = await Voidstrap.Utility.Http.ReadBytesBoundedAsync(response.Content, GameIconMaxBytes, CancellationToken.None).ConfigureAwait(false);
			if (data.Length == 0)
				return [];

			using Image<Rgba32> source = Image.Load<Rgba32>(data);
			List<nint> native = new();
			foreach (int size in IconSizes)
			{
				using Image<Rgba32> scaled = source.Clone(context => context.Resize(size, size));
				native.Add(size);
				native.Add(size);
				scaled.ProcessPixelRows(rows =>
				{
					for (int y = 0; y < rows.Height; y++)
					{
						foreach (Rgba32 pixel in rows.GetRowSpan(y))
							native.Add((nint)(((uint)pixel.A << 24) | ((uint)pixel.R << 16) | ((uint)pixel.G << 8) | pixel.B));
					}
				});
			}
			return native.ToArray();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The game icon could not be prepared: " + ex.Message);
			return [];
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_activityWatcher.OnGameJoin -= OnGameJoin;
		_activityWatcher.OnGameLeave -= OnGameLeave;
		try
		{
			_cts.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		_cts.Dispose();
	}
}
