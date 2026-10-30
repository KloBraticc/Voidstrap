using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace Voidstrap.Integrations.Overlays;

internal static class LinuxGameOverlay
{
	public const string LayerName = "VK_LAYER_VOIDSTRAP_overlay";

	private const uint Magic = 0x564f5356;
	private const uint Version = 1;
	private const int HeaderSize = 64;
	private const int MaxEdge = 4096;
	private const long SlotSize = (long)MaxEdge * MaxEdge * 4;
	private const string LayerLibraryResource = "Voidstrap.Resources.Linux.libvoidstrap_overlay.so";
	private const string LayerLibraryName = "libvoidstrap_overlay.so";
	private const string LayerManifestName = "VkLayer_VOIDSTRAP_overlay.json";

	private const string LayerManifest = """
		{
			"file_format_version": "1.1.2",
			"layer": {
				"name": "VK_LAYER_VOIDSTRAP_overlay",
				"type": "GLOBAL",
				"library_path": "./libvoidstrap_overlay.so",
				"api_version": "1.3.0",
				"implementation_version": "1",
				"description": "Voidstrap draw only overlay",
				"functions": {
					"vkNegotiateLoaderLayerInterfaceVersion": "voidstrap_overlay_NegotiateLoaderLayerInterfaceVersion",
					"vkGetInstanceProcAddr": "voidstrap_overlay_GetInstanceProcAddr",
					"vkGetDeviceProcAddr": "voidstrap_overlay_GetDeviceProcAddr"
				}
			}
		}

		""";

	private static readonly object Gate = new();
	private static MemoryMappedFile? _file;
	private static MemoryMappedViewAccessor? _view;
	private static string? _path;
	private static Timer? _timer;
	private static byte[] _pixels = Array.Empty<byte>();
	private static int _width;
	private static int _height;
	private static int _slot;
	private static uint _sequence;
	private static string _signature = "";
	private static nint[] _windows = Array.Empty<nint>();
	private static readonly HashSet<nint> Concealed = new();
	private static volatile HashSet<nint> _native = new();
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, double> Opacity = new();

	public static bool SetWindowOpacity(nint handle, double opacity)
	{
		Opacity[handle] = Math.Clamp(opacity, 0, 1);
		if (Active && Concealed.Contains(handle))
			return Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowOpacity(handle, 0);
		return Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowOpacity(handle, opacity);
	}

	private static byte[] _capture = Array.Empty<byte>();
	private static int _ticks;
	private static int _rendering;

	public static bool Active { get; private set; }

	public static bool WantsLayer => Voidstrap.Utility.Platform.IsLinux;

	public static IReadOnlyDictionary<string, string>? PrepareLaunch()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || !WantsLayer)
			return null;
		lock (Gate)
		{
			try
			{
				string? layerDirectory = InstallLayer();
				if (layerDirectory == null)
					return null;
				OpenShared();
				if (_path == null)
					return null;
				string layers = Environment.GetEnvironmentVariable("VK_INSTANCE_LAYERS") ?? "";
				return new Dictionary<string, string>(StringComparer.Ordinal)
				{
					["VK_ADD_LAYER_PATH"] = layerDirectory,
					["VK_INSTANCE_LAYERS"] = string.IsNullOrEmpty(layers) ? LayerName : LayerName + ":" + layers,
					["VOIDSTRAP_OVERLAY_SHM"] = _path,
				};
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("LinuxGameOverlay", "The in game overlay could not be prepared: " + ex.Message);
				return null;
			}
		}
	}

	public static void Start()
	{
		lock (Gate)
		{
			if (_view == null || _timer != null)
				return;
			Active = true;
			_signature = "";
			_timer = new Timer(OnTick, null, 0, 33);
			App.Logger.WriteLine("LinuxGameOverlay", "Showing the Voidstrap overlay windows inside the game frame");
		}
	}

	private static void RevealWindows()
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted)
			return;
		application.Dispatcher.BeginInvoke(
			new Action(() =>
			{
				foreach (nint handle in Concealed)
					Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowOpacity(
						handle,
						Opacity.TryGetValue(handle, out double opacity) ? opacity : 1
					);
				Concealed.Clear();
			})
		);
	}

	public static void Stop()
	{
		RevealWindows();
		lock (Gate)
		{
			_timer?.Dispose();
			_timer = null;
			Active = false;
			if (_view != null)
			{
				_view.Write(24, 0u);
				_view.Write(28, 0u);
				_view.Flush();
			}
			_view?.Dispose();
			_view = null;
			_file?.Dispose();
			_file = null;
			if (_path != null)
			{
				try
				{
					File.Delete(_path);
				}
				catch (IOException) { }
				_path = null;
			}
		}
	}

	private static string? InstallLayer()
	{
		using Stream? library = typeof(LinuxGameOverlay).Assembly.GetManifestResourceStream(LayerLibraryResource);
		if (library == null)
		{
			App.Logger.WriteLine("LinuxGameOverlay", "This build has no in game overlay layer");
			return null;
		}
		byte[] content;
		using (MemoryStream buffer = new())
		{
			library.CopyTo(buffer);
			content = buffer.ToArray();
		}
		string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content))[..16];
		string directory = Path.Combine(Voidstrap.Platform.Linux.LinuxRosemary.Root, "layer", hash);
		string libraryPath = Path.Combine(directory, LayerLibraryName);
		if (!File.Exists(libraryPath) || new FileInfo(libraryPath).Length != content.Length)
		{
			Directory.CreateDirectory(directory);
			string temporary = libraryPath + "." + Guid.NewGuid().ToString("N");
			File.WriteAllBytes(temporary, content);
			File.Move(temporary, libraryPath, true);
		}
		string manifestPath = Path.Combine(directory, LayerManifestName);
		string manifest = LayerManifest.Replace("\r\n", "\n", StringComparison.Ordinal);
		if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != manifest)
			File.WriteAllText(manifestPath, manifest);
		string parent = Path.Combine(Voidstrap.Platform.Linux.LinuxRosemary.Root, "layer");
		foreach (string old in Directory.EnumerateDirectories(parent))
		{
			if (string.Equals(old, directory, StringComparison.Ordinal))
				continue;
			try
			{
				Directory.Delete(old, true);
			}
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
		return directory;
	}

	private static void OpenShared()
	{
		if (_view != null)
			return;
		string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
		string folder = !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime) ? runtime : Path.GetTempPath();
		_path = Path.Combine(folder, "voidstrap-overlay-" + Environment.ProcessId + ".shm");
		long size = HeaderSize + 3 * SlotSize;
		using (FileStream stream = new(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
			stream.SetLength(size);
		File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		_file = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, null, size, MemoryMappedFileAccess.ReadWrite);
		_view = _file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
		_view.Write(0, Magic);
		_view.Write(4, Version);
		_view.Write(8, 0u);
		_view.Write(12, 0u);
		_view.Write(24, 0u);
		_view.Write(28, 0u);
		_view.Write(56, 0u);
		_view.Flush();
	}

	public static bool FramesAvailable => Active && _view != null;

	public static uint FrameSequence
	{
		get
		{
			lock (Gate)
				return _view?.ReadUInt32(56) ?? 0;
		}
	}

	public static void RequestFrames(bool wanted)
	{
		lock (Gate)
		{
			_view?.Write(28, wanted ? 1u : 0u);
		}
	}

	public static bool TryReadFrame(ref byte[] buffer, ref uint lastSequence, out int width, out int height, out bool rgba)
	{
		width = 0;
		height = 0;
		rgba = false;
		lock (Gate)
		{
			MemoryMappedViewAccessor? view = _view;
			if (view == null)
				return false;
			for (int attempt = 0; attempt < 2; attempt++)
			{
				uint sequence = view.ReadUInt32(56);
				if (sequence == 0 || sequence == lastSequence)
					return false;
				uint format = view.ReadUInt32(60);
				width = (int)view.ReadUInt32(48);
				height = (int)view.ReadUInt32(52);
				if (format == 0 || width <= 0 || height <= 0 || width > MaxEdge || height > MaxEdge)
					return false;
				int bytes = width * height * 4;
				if (buffer.Length < bytes)
					buffer = new byte[bytes];
				view.ReadArray(HeaderSize + 2 * SlotSize, buffer, 0, bytes);
				if (view.ReadUInt32(56) != sequence)
					continue;
				lastSequence = sequence;
				rgba = format == 2;
				return true;
			}
			return false;
		}
	}

	private static void OnTick(object? state)
	{
		if (Interlocked.Exchange(ref _rendering, 1) != 0)
			return;
		try
		{
			Render();
		}
		catch (Exception ex)
		{
			_signature = "";
			App.Logger.WriteLine("LinuxGameOverlay", "The in game overlay frame could not be drawn: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _rendering, 0);
		}
	}

	private static void RefreshWindows()
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted)
			return;
		application.Dispatcher.BeginInvoke(
			new Action(() =>
			{
				List<nint> handles = new();
				HashSet<nint> native = new();
				foreach (Window window in application.Windows)
				{
					if (!window.IsVisible || window.ShowInTaskbar || !window.Topmost)
						continue;
					nint handle = Voidstrap.UI.LinuxWindowMode.ResolveExactNativeWindow(window);
					if (handle == 0)
						continue;
					handles.Add(handle);
					if (window.GetType().Name.StartsWith("Session", StringComparison.Ordinal) || window.Title == "Session dimmer")
						native.Add(handle);
					else if (Active && Concealed.Add(handle))
						Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowOpacity(handle, 0);
				}
				_native = native;
				_windows = handles.ToArray();
			})
		);
	}

	private static void Render()
	{
		MemoryMappedViewAccessor? view = _view;
		if (view == null)
			return;
		if (_ticks++ % 8 == 0)
			RefreshWindows();
		int width = (int)view.ReadUInt32(48);
		int height = (int)view.ReadUInt32(52);
		if (width <= 0 || height <= 0)
			return;
		width = Math.Min(width, MaxEdge);
		height = Math.Min(height, MaxEdge);

		if (
			!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetPrimaryMonitorBounds(
				out int monitorLeft,
				out int monitorTop,
				out int monitorWidth,
				out int monitorHeight
			)
			|| monitorWidth <= 0
			|| monitorHeight <= 0
		)
		{
			monitorLeft = 0;
			monitorTop = 0;
			monitorWidth = width;
			monitorHeight = height;
		}
		double scaleX = (double)width / monitorWidth;
		double scaleY = (double)height / monitorHeight;

		if (_width != width || _height != height || _pixels.Length != width * height * 4)
		{
			_pixels = new byte[width * height * 4];
			_width = width;
			_height = height;
		}
		else
		{
			Array.Clear(_pixels);
		}

		bool visible = false;
		System.Text.StringBuilder signature = new();
		signature.Append(width).Append('x').Append(height);
		HashSet<nint> native = _native;
		foreach (nint window in _windows)
		{
			if (native.Contains(window))
				continue;
			if (
				!Voidstrap.Platform.Linux.LinuxWindowInterop.TryCaptureOverlayPremultiplied(
					window,
					ref _capture,
					out int left,
					out int top,
					out int captureWidth,
					out int captureHeight
				)
			)
				continue;
			int hash = 17;
			for (int index = 3; index < captureWidth * captureHeight * 4; index += 61)
				hash = hash * 31 + _capture[index] + _capture[index - 1] * 7;
			signature
				.Append('|')
				.Append(window)
				.Append(',')
				.Append(left)
				.Append(',')
				.Append(top)
				.Append(',')
				.Append(captureWidth)
				.Append(',')
				.Append(captureHeight)
				.Append(',')
				.Append(hash);
			double windowOpacity = Opacity.TryGetValue(window, out double stored) ? stored : 1;
			if (windowOpacity <= 0)
				continue;
			signature.Append(',').Append((int)(windowOpacity * 100));
			visible |= Composite(left - monitorLeft, top - monitorTop, captureWidth, captureHeight, scaleX, scaleY, windowOpacity);
		}
		string current = signature.ToString();
		if (current == _signature && _ticks % 4 != 0)
			return;
		_signature = current;

		int slot = _slot ^ 1;
		long offset = HeaderSize + slot * SlotSize;
		view.WriteArray(offset, _pixels, 0, _pixels.Length);
		view.Write(16, (uint)width);
		view.Write(20, (uint)height);
		view.Write(24, visible ? 1u : 0u);
		Thread.MemoryBarrier();
		view.Write(12, (uint)slot);
		_slot = slot;
		Thread.MemoryBarrier();
		view.Write(8, ++_sequence);
		view.Flush();
	}

	private static bool Composite(int left, int top, int sourceWidth, int sourceHeight, double scaleX, double scaleY, double opacity)
	{
		int destinationLeft = (int)Math.Round(left * scaleX);
		int destinationTop = (int)Math.Round(top * scaleY);
		int destinationWidth = Math.Max(1, (int)Math.Round(sourceWidth * scaleX));
		int destinationHeight = Math.Max(1, (int)Math.Round(sourceHeight * scaleY));
		bool drawn = false;
		for (int y = Math.Max(0, -destinationTop); y < destinationHeight; y++)
		{
			int targetY = destinationTop + y;
			if (targetY >= _height)
				break;
			int sourceY = Math.Min(sourceHeight - 1, (int)(y / scaleY));
			for (int x = Math.Max(0, -destinationLeft); x < destinationWidth; x++)
			{
				int targetX = destinationLeft + x;
				if (targetX >= _width)
					break;
				int sourceX = Math.Min(sourceWidth - 1, (int)(x / scaleX));
				int source = (sourceY * sourceWidth + sourceX) * 4;
				int alpha = (int)(_capture[source + 3] * opacity);
				if (alpha == 0)
					continue;
				int target = (targetY * _width + targetX) * 4;
				int inverse = 255 - alpha;
				_pixels[target] = (byte)Math.Min(255, (int)(_capture[source] * opacity) + _pixels[target] * inverse / 255);
				_pixels[target + 1] = (byte)Math.Min(255, (int)(_capture[source + 1] * opacity) + _pixels[target + 1] * inverse / 255);
				_pixels[target + 2] = (byte)Math.Min(255, (int)(_capture[source + 2] * opacity) + _pixels[target + 2] * inverse / 255);
				_pixels[target + 3] = (byte)(alpha + _pixels[target + 3] * inverse / 255);
				drawn = true;
			}
		}
		return drawn;
	}
}
