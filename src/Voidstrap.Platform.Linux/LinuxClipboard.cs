using System.Diagnostics;
using System.Text;
using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public static class LinuxClipboard
{
	private sealed record ClipboardBackend(
		string CopyCommand,
		string[] CopyArguments,
		string PasteCommand,
		string[] PasteArguments);

	private static readonly ClipboardBackend WlClipboard = new(
		"wl-copy",
		[],
		"wl-paste",
		["--no-newline"]);

	private static readonly ClipboardBackend XClip = new(
		"xclip",
		["-selection", "clipboard"],
		"xclip",
		["-selection", "clipboard", "-out"]);

	private static readonly ClipboardBackend XSel = new(
		"xsel",
		["--clipboard", "--input"],
		"xsel",
		["--clipboard", "--output"]);

	private static readonly SystemProcessService Processes = new();
	private static readonly object Gate = new();
	private static ClipboardBackend? _activeBackend;
	private static string _cachedText = string.Empty;
	private static bool _hasCachedText;

	public static event Action? Changed;

	public static bool IsAvailable => OperatingSystem.IsLinux();

	public static bool TracksChanges => false;

	public static bool SetText(string? text)
	{
		if (!OperatingSystem.IsLinux())
			return false;

		string value = text ?? string.Empty;

		lock (Gate)
		{
			_cachedText = value;
			_hasCachedText = true;
		}

		if (_activeBackend is not null && TrySetText(_activeBackend, value))
		{
			RaiseChanged();
			return true;
		}

		_activeBackend = null;
		foreach (ClipboardBackend backend in GetCandidates())
		{
			if (TrySetText(backend, value))
			{
				_activeBackend = backend;
				RaiseChanged();
				return true;
			}
		}

		RaiseChanged();
		return true;
	}

	public static string? GetText()
	{
		if (!OperatingSystem.IsLinux())
			return null;

		if (_activeBackend is not null && TryGetText(_activeBackend, out string? current))
		{
			if (current is not null)
			{
				lock (Gate)
				{
					_cachedText = current;
					_hasCachedText = true;
				}
				return current;
			}
		}

		_activeBackend = null;
		foreach (ClipboardBackend backend in GetCandidates())
		{
			if (TryGetText(backend, out string? text))
			{
				_activeBackend = backend;
				if (text is not null)
				{
					lock (Gate)
					{
						_cachedText = text;
						_hasCachedText = true;
					}
					return text;
				}
			}
		}

		lock (Gate)
		{
			return _hasCachedText ? _cachedText : null;
		}
	}

	private static void RaiseChanged()
	{
		Action? handlers = Changed;
		if (handlers is null)
			return;

		foreach (Delegate handler in handlers.GetInvocationList())
		{
			try
			{
				((Action)handler)();
			}
			catch (Exception)
			{
			}
		}
	}

	private static IReadOnlyList<ClipboardBackend> GetCandidates()
	{
		return IsWaylandSession()
			? [WlClipboard, XClip, XSel]
			: [XClip, XSel, WlClipboard];
	}

	private static bool IsWaylandSession()
	{
		return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
			|| string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
	}

	private static bool TrySetText(ClipboardBackend backend, string text)
	{
		if (!LinuxFlatpakHost.TryCreateHostCommand(Processes, backend.CopyCommand, backend.CopyArguments, out ProcessCommand command, captureOutput: false))
			return false;

		try
		{
			ProcessStartInfo startInfo = new()
			{
				FileName = command.FileName,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = false,
				RedirectStandardError = true,
				StandardInputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false)
			};

			foreach (string argument in command.Arguments)
				startInfo.ArgumentList.Add(argument);

			using Process process = new() { StartInfo = startInfo };
			if (!process.Start())
				return false;

			Task<string> errorTask = process.StandardError.ReadToEndAsync();
			process.StandardInput.Write(text);
			process.StandardInput.Flush();
			process.StandardInput.Close();

			if (!process.WaitForExit(1000))
			{
				try
				{
					process.Kill(true);
				}
				catch (Exception)
				{
				}
				return false;
			}

			_ = errorTask.GetAwaiter().GetResult();
			return process.ExitCode == 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool TryGetText(ClipboardBackend backend, out string? text)
	{
		text = null;
		if (!LinuxFlatpakHost.TryCreateHostCommand(Processes, backend.PasteCommand, backend.PasteArguments, out ProcessCommand command))
			return false;

		try
		{
			ProcessStartInfo startInfo = new()
			{
				FileName = command.FileName,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false)
			};

			foreach (string argument in command.Arguments)
				startInfo.ArgumentList.Add(argument);

			using Process process = new() { StartInfo = startInfo };
			if (!process.Start())
				return false;

			Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorTask = process.StandardError.ReadToEndAsync();

			if (!process.WaitForExit(1000))
			{
				try
				{
					process.Kill(true);
				}
				catch (Exception)
				{
				}
				return false;
			}

			_ = errorTask.GetAwaiter().GetResult();

			if (process.ExitCode is 126 or 127)
				return false;

			if (process.ExitCode == 0)
			{
				text = outputTask.GetAwaiter().GetResult();
				return true;
			}

			text = string.Empty;
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
