using System.Globalization;
using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public readonly record struct LinuxDisplayBounds(int Left, int Top, int Width, int Height)
{
	public bool IsUsable => Width >= 320 && Height >= 240;
}

public readonly record struct LinuxDisplayInfo(
	LinuxDisplayBounds Bounds,
	LinuxDisplayBounds WorkArea,
	double Scale,
	string Source);

public static class LinuxDisplayMetrics
{
	private const int MinimumWidth = 320;
	private const int MinimumHeight = 240;
	private const long RefreshIntervalTicks = 30_000;

	private static readonly object Sync = new();
	private static readonly LinuxDisplayBounds Fallback = new(0, 0, 1280, 800);

	private static LinuxDisplayInfo _cached;
	private static long _cachedAtTicks = long.MinValue;
	private static nint _refreshWindow;
	private static double _refreshRate = 60d;
	private static long _refreshAtTicks = long.MinValue;

	public static LinuxDisplayInfo Current => Resolve(false);

	public static LinuxDisplayInfo Refresh() => Resolve(true);

	public static double RefreshRateForWindow(nint window)
	{
		if (!OperatingSystem.IsLinux())
			return 60d;

		lock (Sync)
		{
			long now = Environment.TickCount64;
			if (_refreshAtTicks != long.MinValue
				&& _refreshWindow == window
				&& now - _refreshAtTicks < RefreshIntervalTicks)
				return _refreshRate;

			int centerX = int.MinValue;
			int centerY = int.MinValue;
			if (window != 0
				&& LinuxWindowInterop.TryGetWindowGeometry(window, out int left, out int top, out int width, out int height))
			{
				centerX = left + width / 2;
				centerY = top + height / 2;
			}

			double resolved = TryQueryXrandrRefreshRate(centerX, centerY, out double rate) ? rate : 60d;
			_refreshWindow = window;
			_refreshRate = Math.Clamp(resolved, 24d, 360d);
			_refreshAtTicks = now;
			return _refreshRate;
		}
	}

	public static void Invalidate()
	{
		lock (Sync)
		{
			_cachedAtTicks = long.MinValue;
			_refreshAtTicks = long.MinValue;
		}
	}

	private static bool TryQueryXrandrRefreshRate(int centerX, int centerY, out double refreshRate)
	{
		refreshRate = 0d;
		string output = RunTool("xrandr", "--current");
		if (output.Length == 0)
			return false;

		bool selectedOutput = false;
		double firstActiveRate = 0d;
		foreach (string rawLine in output.Split('\n'))
		{
			if (rawLine.Length == 0)
				continue;
			if (!char.IsWhiteSpace(rawLine[0]))
			{
				selectedOutput = rawLine.Contains(" connected ", StringComparison.Ordinal)
					&& TryParseOutputGeometry(rawLine, out int left, out int top, out int width, out int height)
					&& centerX >= left
					&& centerY >= top
					&& centerX < left + width
					&& centerY < top + height;
				continue;
			}

			int marker = rawLine.IndexOf('*');
			if (marker < 0)
				continue;
			int start = marker - 1;
			while (start >= 0 && (char.IsAsciiDigit(rawLine[start]) || rawLine[start] == '.'))
				start--;
			ReadOnlySpan<char> value = rawLine.AsSpan(start + 1, marker - start - 1);
			if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
				|| parsed < 1d
				|| parsed > 1000d)
				continue;
			if (firstActiveRate == 0d)
				firstActiveRate = parsed;
			if (selectedOutput)
			{
				refreshRate = parsed;
				return true;
			}
		}

		refreshRate = firstActiveRate;
		return refreshRate > 0d;
	}

	private static bool TryParseOutputGeometry(string line, out int left, out int top, out int width, out int height)
	{
		left = 0;
		top = 0;
		width = 0;
		height = 0;
		foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			int separator = token.IndexOf('x');
			if (separator <= 0
				|| !int.TryParse(token.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedWidth))
				continue;
			int firstOffset = IndexOfCoordinateSign(token, separator + 1);
			if (firstOffset < 0
				|| !int.TryParse(token.AsSpan(separator + 1, firstOffset - separator - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedHeight))
				continue;
			int secondOffset = IndexOfCoordinateSign(token, firstOffset + 1);
			if (secondOffset < 0
				|| !int.TryParse(token.AsSpan(firstOffset, secondOffset - firstOffset), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLeft)
				|| !int.TryParse(token.AsSpan(secondOffset), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTop))
				continue;
			if (parsedWidth <= 0 || parsedHeight <= 0)
				continue;
			left = parsedLeft;
			top = parsedTop;
			width = parsedWidth;
			height = parsedHeight;
			return true;
		}
		return false;
	}

	private static int IndexOfCoordinateSign(string value, int start)
	{
		for (int index = start; index < value.Length; index++)
		{
			if (value[index] is '+' or '-')
				return index;
		}
		return -1;
	}

	private static LinuxDisplayInfo Resolve(bool force)
	{
		lock (Sync)
		{
			long now = Environment.TickCount64;
			if (!force && _cachedAtTicks != long.MinValue && now - _cachedAtTicks < RefreshIntervalTicks)
				return _cached;

			LinuxDisplayInfo resolved = Query();
			_cached = resolved;
			_cachedAtTicks = now;
			return resolved;
		}
	}

	private static LinuxDisplayInfo Query()
	{
		if (!OperatingSystem.IsLinux())
			return new LinuxDisplayInfo(Fallback, Fallback, 1.0, "fallback");

		double scale = ReadScale();

		if (TryQueryX11(out LinuxDisplayBounds x11Bounds, out LinuxDisplayBounds x11WorkArea))
			return new LinuxDisplayInfo(x11Bounds, x11WorkArea, scale, "x11");

		if (TryQueryDrm(out LinuxDisplayBounds drmBounds))
			return new LinuxDisplayInfo(drmBounds, drmBounds, scale, "drm");

		if (TryQueryTool(out LinuxDisplayBounds toolBounds, out string tool))
			return new LinuxDisplayInfo(toolBounds, toolBounds, scale, tool);

		return new LinuxDisplayInfo(Fallback, Fallback, scale, "fallback");
	}

	private static bool TryQueryX11(out LinuxDisplayBounds bounds, out LinuxDisplayBounds workArea)
	{
		bounds = default;
		workArea = default;

		if (!LinuxWindowInterop.TryGetRootBounds(out int width, out int height))
			return false;

		if (width < MinimumWidth || height < MinimumHeight)
			return false;

		bounds = new LinuxDisplayBounds(0, 0, width, height);
		workArea = LinuxWindowInterop.TryGetWorkArea(out int left, out int top, out int workWidth, out int workHeight)
			&& workWidth >= MinimumWidth
			&& workHeight >= MinimumHeight
			&& workWidth <= width
			&& workHeight <= height
			? new LinuxDisplayBounds(left, top, workWidth, workHeight)
			: bounds;
		return true;
	}

	private static bool TryQueryDrm(out LinuxDisplayBounds bounds)
	{
		bounds = default;
		try
		{
			const string root = "/sys/class/drm";
			if (!Directory.Exists(root))
				return false;

			int bestWidth = 0;
			int bestHeight = 0;
			foreach (string connector in Directory.EnumerateDirectories(root))
			{
				string statusFile = Path.Combine(connector, "status");
				string modesFile = Path.Combine(connector, "modes");
				if (!File.Exists(statusFile) || !File.Exists(modesFile))
					continue;
				if (!string.Equals(ReadFirstLine(statusFile), "connected", StringComparison.Ordinal))
					continue;

				string mode = ReadFirstLine(modesFile);
				int separator = mode.IndexOf('x');
				if (separator <= 0)
					continue;
				if (!int.TryParse(mode.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out int width))
					continue;
				ReadOnlySpan<char> tail = mode.AsSpan(separator + 1);
				int end = 0;
				while (end < tail.Length && char.IsAsciiDigit(tail[end]))
					end++;
				if (end == 0 || !int.TryParse(tail[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
					continue;

				if ((long)width * height <= (long)bestWidth * bestHeight)
					continue;
				bestWidth = width;
				bestHeight = height;
			}

			if (bestWidth < MinimumWidth || bestHeight < MinimumHeight)
				return false;

			bounds = new LinuxDisplayBounds(0, 0, bestWidth, bestHeight);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool TryQueryTool(out LinuxDisplayBounds bounds, out string source)
	{
		bounds = default;
		source = string.Empty;

		if (TryParseTool("xdpyinfo", string.Empty, "dimensions:", out bounds))
		{
			source = "xdpyinfo";
			return true;
		}

		if (TryParseTool("xrandr", "--current", "current", out bounds))
		{
			source = "xrandr";
			return true;
		}

		return false;
	}

	private static bool TryParseTool(string fileName, string arguments, string marker, out LinuxDisplayBounds bounds)
	{
		bounds = default;
		string output = RunTool(fileName, arguments);
		if (output.Length == 0)
			return false;

		foreach (string line in output.Split('\n'))
		{
			int markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
			if (markerIndex < 0)
				continue;
			if (!TryParseDimensions(line.AsSpan(markerIndex + marker.Length), out int width, out int height))
				continue;
			if (width < MinimumWidth || height < MinimumHeight)
				continue;
			bounds = new LinuxDisplayBounds(0, 0, width, height);
			return true;
		}

		return false;
	}

	private static bool TryParseDimensions(ReadOnlySpan<char> text, out int width, out int height)
	{
		width = 0;
		height = 0;
		for (int i = 0; i < text.Length; i++)
		{
			if (!char.IsAsciiDigit(text[i]))
				continue;

			int start = i;
			while (i < text.Length && char.IsAsciiDigit(text[i]))
				i++;
			if (!int.TryParse(text[start..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int first))
				return false;

			int separator = i;
			while (separator < text.Length && (text[separator] == ' ' || text[separator] == '\t'))
				separator++;
			if (separator >= text.Length || (text[separator] != 'x' && text[separator] != 'X'))
				continue;
			separator++;
			while (separator < text.Length && (text[separator] == ' ' || text[separator] == '\t'))
				separator++;

			int secondStart = separator;
			while (separator < text.Length && char.IsAsciiDigit(text[separator]))
				separator++;
			if (secondStart == separator)
				continue;
			if (!int.TryParse(text[secondStart..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out int second))
				continue;

			width = first;
			height = second;
			return true;
		}

		return false;
	}

	private static double ReadScale()
	{
		double scale = ParseScale(Environment.GetEnvironmentVariable("VOIDSTRAP_SCALE"));
		if (scale > 0.0)
			return scale;

		scale = ParseScale(Environment.GetEnvironmentVariable("GDK_SCALE"));
		if (scale > 0.0)
			return scale;

		scale = ParseScale(Environment.GetEnvironmentVariable("QT_SCALE_FACTOR"));
		if (scale > 0.0)
			return scale;

		return 1.0;
	}

	private static double ParseScale(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return 0.0;
		if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
			return 0.0;
		return parsed >= 0.5 && parsed <= 6.0 ? parsed : 0.0;
	}

	private static string ReadFirstLine(string path)
	{
		try
		{
			using StreamReader reader = new(path);
			return reader.ReadLine()?.Trim() ?? string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static string RunTool(string fileName, string arguments)
	{
		System.Diagnostics.Process? process = null;
		try
		{
			process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName, arguments)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (process == null)
				return string.Empty;

			string output = process.StandardOutput.ReadToEnd();
			if (!process.WaitForExit(2000))
			{
				try
				{
					process.Kill(true);
				}
				catch (Exception)
				{
				}

				return string.Empty;
			}

			return output;
		}
		catch (Exception)
		{
			return string.Empty;
		}
		finally
		{
			process?.Dispose();
		}
	}
}
