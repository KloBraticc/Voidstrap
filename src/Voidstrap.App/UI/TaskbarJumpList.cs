using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Voidstrap.Models.Entities;

namespace Voidstrap.UI;

internal static partial class TaskbarJumpList
{
	public const string LibraryView = "library";

	public const string NotificationsView = "notifications";

	private const string LogIdent = "TaskbarJumpList";

	private const string WindowMapName = "Local\\Voidstrap.SettingsWindow";

	private const int GameIconMaxBytes = 4 * 1024 * 1024;

	private const int HistoryMaxBytes = 16 * 1024 * 1024;

	private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 256];

	private static readonly TimeSpan IconRefreshTimeout = TimeSpan.FromSeconds(20);

	private static readonly object WindowMapGate = new();

	private static MemoryMappedFile? _windowMap;

	private static int _iconRefreshActive;

	private static uint _openViewMessage;

	public static uint OpenViewMessage
	{
		get
		{
			if (_openViewMessage == 0 && OperatingSystem.IsWindows())
			{
				try
				{
					_openViewMessage = RegisterWindowMessageW("Voidstrap.OpenSettingsView");
				}
				catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
				{
					_openViewMessage = 0;
				}
			}
			return _openViewMessage;
		}
	}

	public static void Apply()
	{
		if (!OperatingSystem.IsWindows())
			return;
		string? executable = ResolveExecutable();
		if (executable == null)
			return;
		SetTasks(executable, null, null);
		if (Interlocked.Exchange(ref _iconRefreshActive, 1) != 0)
			return;
		_ = Task.Run(() => RefreshLibraryIconAsync(executable));
	}

	private static string? ResolveExecutable()
	{
		try
		{
			string executable = Paths.Application;
			if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
				executable = Paths.Process;
			return !string.IsNullOrEmpty(executable) && File.Exists(executable) ? executable : null;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The Voidstrap executable could not be found for the taskbar tasks: " + ex.Message);
			return null;
		}
	}

	private static void SetTasks(string executable, string? libraryIcon, string? gameName)
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted)
			return;
		if (!application.Dispatcher.CheckAccess())
		{
			application.Dispatcher.BeginInvoke(() => SetTasks(executable, libraryIcon, gameName));
			return;
		}
		try
		{
			bool useGameIcon = libraryIcon != null && File.Exists(libraryIcon);
			JumpList list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
			list.JumpItems.Add(new JumpTask
			{
				Title = "Library",
				Description = useGameIcon && !string.IsNullOrWhiteSpace(gameName) ? "Open your library, last played: " + gameName : "Open your Roblox library in Voidstrap",
				ApplicationPath = executable,
				Arguments = "-settings " + LibraryView,
				IconResourcePath = useGameIcon ? libraryIcon : executable,
				IconResourceIndex = 0
			});
			list.JumpItems.Add(new JumpTask
			{
				Title = "Notifications",
				Description = "Open your Voidstrap notification settings",
				ApplicationPath = executable,
				Arguments = "-settings " + NotificationsView,
				IconResourcePath = executable,
				IconResourceIndex = 0
			});
			JumpList.SetJumpList(application, list);
			list.Apply();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The taskbar tasks could not be set: " + ex.Message);
		}
	}

	private static async Task RefreshLibraryIconAsync(string executable)
	{
		try
		{
			if (!Paths.Initialized || string.IsNullOrEmpty(Paths.Cache))
				return;
			using CancellationTokenSource timeout = new CancellationTokenSource(IconRefreshTimeout);
			(long universeId, long placeId)? latest = await ReadLatestGameAsync(timeout.Token).ConfigureAwait(false);
			if (latest is not { } game || game.universeId <= 0)
			{
				CleanIconCache(null);
				return;
			}
			string directory = Path.Combine(Paths.Cache, "TaskbarIcons");
			string iconPath = Path.Combine(directory, game.universeId + ".ico");
			string? gameName = await ResolveGameNameAsync(game.universeId, timeout.Token).ConfigureAwait(false);
			if (!IsUsableIcon(iconPath))
			{
				string? url = await ResolveIconUrlAsync(game.universeId, timeout.Token).ConfigureAwait(false);
				if (string.IsNullOrWhiteSpace(url))
				{
					App.Logger.WriteLine(LogIdent, $"No icon for universe {game.universeId}, keeping the Voidstrap icon on the Library task");
					return;
				}
				byte[] image = await DownloadAsync(url, timeout.Token).ConfigureAwait(false);
				if (image.Length == 0 || !WriteIcon(image, directory, iconPath))
					return;
			}
			CleanIconCache(iconPath);
			SetTasks(executable, iconPath, gameName);
			App.Logger.WriteLine(LogIdent, $"The Library task now shows the icon of universe {game.universeId}");
		}
		catch (OperationCanceledException)
		{
			App.Logger.WriteLine(LogIdent, "The latest game icon took too long, keeping the Voidstrap icon on the Library task");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The latest game icon could not be prepared, keeping the Voidstrap icon on the Library task: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _iconRefreshActive, 0);
		}
	}

	private static async Task<(long universeId, long placeId)?> ReadLatestGameAsync(CancellationToken token)
	{
		string path = Paths.ServerHistory;
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
			return null;
		FileInfo info = new FileInfo(path);
		if (info.Length <= 0 || info.Length > HistoryMaxBytes)
			return null;
		string source;
		using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		using (StreamReader reader = new StreamReader(stream))
			source = await reader.ReadToEndAsync(token).ConfigureAwait(false);
		IReadOnlyCollection<Voidstrap.Core.GameHistoryEntry> entries = Voidstrap.Core.GameHistoryStore.Parse(source);
		Voidstrap.Core.GameHistoryEntry? newest = entries
			.Where(entry => entry.UniverseId > 0)
			.OrderByDescending(entry => entry.LeftAt ?? entry.JoinedAt ?? DateTimeOffset.MinValue)
			.FirstOrDefault();
		return newest == null ? null : (newest.UniverseId, newest.PlaceId);
	}

	private static async Task<string?> ResolveGameNameAsync(long universeId, CancellationToken token)
	{
		UniverseDetails? details = UniverseDetails.LoadFromCache(universeId);
		if (string.IsNullOrWhiteSpace(details?.Data?.Name))
		{
			await UniverseDetails.FetchSingle(universeId, token).ConfigureAwait(false);
			details = UniverseDetails.LoadFromCache(universeId);
		}
		return details?.Data?.Name;
	}

	private static async Task<string?> ResolveIconUrlAsync(long universeId, CancellationToken token)
	{
		UniverseDetails? details = UniverseDetails.LoadFromCache(universeId);
		if (string.IsNullOrWhiteSpace(details?.Thumbnail?.ImageUrl))
		{
			await UniverseDetails.FetchSingle(universeId, token).ConfigureAwait(false);
			details = UniverseDetails.LoadFromCache(universeId);
		}
		string? url = details?.Thumbnail?.ImageUrl;
		return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
	}

	private static async Task<byte[]> DownloadAsync(string url, CancellationToken token)
	{
		using HttpResponseMessage response = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
			return Array.Empty<byte>();
		return await Voidstrap.Utility.Http.ReadBytesBoundedAsync(response.Content, GameIconMaxBytes, token).ConfigureAwait(false);
	}

	private static bool IsUsableIcon(string path)
	{
		try
		{
			FileInfo info = new FileInfo(path);
			return info.Exists && info.Length > 22 && DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromDays(3);
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool WriteIcon(byte[] image, string directory, string iconPath)
	{
		List<byte[]> frames = new List<byte[]>(IconSizes.Length);
		DecoderOptions options = new DecoderOptions { TargetSize = new SixLabors.ImageSharp.Size(512, 512), MaxFrames = 1 };
		using (Image<Rgba32> source = Image.Load<Rgba32>(options, image))
		{
			if (source.Width < 8 || source.Height < 8)
				return false;
			foreach (int size in IconSizes)
			{
				using Image<Rgba32> frame = source.Clone(context => context.Resize(new ResizeOptions { Size = new SixLabors.ImageSharp.Size(size, size), Mode = SixLabors.ImageSharp.Processing.ResizeMode.Crop, Sampler = KnownResamplers.Lanczos3 }));
				using MemoryStream png = new MemoryStream();
				frame.SaveAsPng(png, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
				frames.Add(png.ToArray());
			}
		}
		Directory.CreateDirectory(directory);
		string temp = iconPath + "." + Environment.ProcessId + ".tmp";
		try
		{
			using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				writer.Write((ushort)0);
				writer.Write((ushort)1);
				writer.Write((ushort)frames.Count);
				int offset = 6 + 16 * frames.Count;
				for (int index = 0; index < frames.Count; index++)
				{
					int size = IconSizes[index];
					writer.Write((byte)(size >= 256 ? 0 : size));
					writer.Write((byte)(size >= 256 ? 0 : size));
					writer.Write((byte)0);
					writer.Write((byte)0);
					writer.Write((ushort)1);
					writer.Write((ushort)32);
					writer.Write(frames[index].Length);
					writer.Write(offset);
					offset += frames[index].Length;
				}
				foreach (byte[] frame in frames)
					writer.Write(frame);
			}
			File.Move(temp, iconPath, overwrite: true);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LogIdent, "The game icon file could not be written: " + ex.Message);
			TryDelete(temp);
			return IsUsableIcon(iconPath);
		}
	}

	private static void CleanIconCache(string? keep)
	{
		try
		{
			string directory = Path.Combine(Paths.Cache, "TaskbarIcons");
			if (!Directory.Exists(directory))
				return;
			foreach (string file in Directory.EnumerateFiles(directory))
			{
				if (keep == null || !string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
					TryDelete(file);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	public static int ToViewId(string? view)
	{
		return view?.Trim().ToLowerInvariant() switch
		{
			LibraryView => 1,
			NotificationsView => 2,
			_ => 0
		};
	}

	public static string? FromViewId(nint id)
	{
		return id switch
		{
			1 => LibraryView,
			2 => NotificationsView,
			_ => null
		};
	}

	public static void PublishWindow(nint windowHandle)
	{
		if (!OperatingSystem.IsWindows() || windowHandle == 0)
			return;
		lock (WindowMapGate)
		{
			try
			{
				_windowMap ??= MemoryMappedFile.CreateOrOpen(WindowMapName, 16, MemoryMappedFileAccess.ReadWrite);
				using MemoryMappedViewAccessor view = _windowMap.CreateViewAccessor(0, 16, MemoryMappedFileAccess.Write);
				view.Write(0, (long)windowHandle);
				view.Write(8, (long)Environment.ProcessId);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger.WriteLine(LogIdent, "The settings window could not be shared with taskbar tasks: " + ex.Message);
			}
		}
	}

	public static void UnpublishWindow(nint windowHandle)
	{
		if (!OperatingSystem.IsWindows())
			return;
		lock (WindowMapGate)
		{
			if (_windowMap == null)
				return;
			try
			{
				using MemoryMappedViewAccessor view = _windowMap.CreateViewAccessor(0, 16, MemoryMappedFileAccess.ReadWrite);
				if (view.ReadInt64(0) == (long)windowHandle)
				{
					view.Write(0, 0L);
					view.Write(8, 0L);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
			_windowMap.Dispose();
			_windowMap = null;
		}
	}

	public static bool TryForwardToOpenWindow(string? view)
	{
		int id = ToViewId(view);
		if (!OperatingSystem.IsWindows() || id == 0 || OpenViewMessage == 0)
			return false;
		try
		{
			using MemoryMappedFile map = MemoryMappedFile.OpenExisting(WindowMapName, MemoryMappedFileRights.Read);
			using MemoryMappedViewAccessor accessor = map.CreateViewAccessor(0, 16, MemoryMappedFileAccess.Read);
			nint window = (nint)accessor.ReadInt64(0);
			long processId = accessor.ReadInt64(8);
			if (window == 0 || processId <= 0 || IsWindow(window) == 0)
				return false;
			if (GetWindowThreadProcessId(window, out uint owner) == 0 || owner != processId)
				return false;
			if (IsIconic(window) != 0)
				ShowWindow(window, SwRestore);
			SetForegroundWindow(window);
			return PostMessageW(window, OpenViewMessage, id, 0) != 0;
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
	}

	public static bool PostOpenView(nint windowHandle, string? view)
	{
		int id = ToViewId(view);
		if (!OperatingSystem.IsWindows() || id == 0 || windowHandle == 0 || OpenViewMessage == 0)
			return false;
		return PostMessageW(windowHandle, OpenViewMessage, id, 0) != 0;
	}

	private const int SwRestore = 9;

	[LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial uint RegisterWindowMessageW(string name);

	[LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
	private static partial int PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

	[LibraryImport("user32.dll", EntryPoint = "IsWindow")]
	private static partial int IsWindow(nint hWnd);

	[LibraryImport("user32.dll", EntryPoint = "IsIconic")]
	private static partial int IsIconic(nint hWnd);

	[LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
	private static partial int ShowWindow(nint hWnd, int command);

	[LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
	private static partial int SetForegroundWindow(nint hWnd);

	[LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
	private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
