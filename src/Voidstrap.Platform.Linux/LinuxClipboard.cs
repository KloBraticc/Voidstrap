using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Platform.Linux;

public static partial class LinuxClipboard
{
	private const int PropertyNotify = 28;
	private const int SelectionClear = 29;
	private const int SelectionRequest = 30;
	private const int SelectionNotify = 31;
	private const int ClientMessage = 33;

	private const int PropModeReplace = 0;
	private const nint AnyPropertyType = 0;
	private const nint AtomAtom = 4;
	private const nint AtomString = 31;
	private const long StructureNotifyMask = 1L << 17;
	private const long PropertyChangeMask = 1L << 22;

	private static readonly object Gate = new();
	private static Thread? _owner;
	private static bool _unavailable;

	private static nint _display;
	private static nint _window;
	private static nint _clipboardAtom;
	private static nint _primaryAtom;
	private static nint _targetsAtom;
	private static nint _utf8Atom;
	private static nint _textAtom;
	private static nint _wakeAtom;

	private static string _text = string.Empty;
	private static bool _hasText;

	public static bool IsAvailable
	{
		get
		{
			if (_unavailable)
				return false;
			return EnsureOwner();
		}
	}

	public static bool SetText(string? text)
	{
		if (!OperatingSystem.IsLinux() || !EnsureOwner())
			return false;

		lock (Gate)
		{
			_text = text ?? string.Empty;
			_hasText = true;
		}

		return Wake();
	}

	public static string? GetText()
	{
		if (!OperatingSystem.IsLinux())
			return null;

		lock (Gate)
		{
			if (_hasText && _window != 0 && XGetSelectionOwner(_display, _clipboardAtom) == _window)
				return _text;
		}

		return ReadSelection();
	}

	private static bool Wake()
	{
		lock (Gate)
		{
			if (_display == 0 || _window == 0)
				return false;

			byte[] buffer = new byte[192];
			WriteInt(buffer, 0, ClientMessage);
			WriteNint(buffer, 24, _display);
			WriteNint(buffer, 32, _window);
			WriteNint(buffer, 40, _wakeAtom);
			WriteInt(buffer, 48, 32);
			XSendEvent(_display, _window, false, 0, buffer);
			_ = XFlush(_display);
			return true;
		}
	}

	private static bool EnsureOwner()
	{
		lock (Gate)
		{
			if (_unavailable)
				return false;
			if (_owner is not null)
				return _display != 0;

			try
			{
				_ = XInitThreads();
				nint display = XOpenDisplay(null);
				if (display == 0)
				{
					_unavailable = true;
					return false;
				}

				nint root = XDefaultRootWindow(display);
				nint window = XCreateSimpleWindow(display, root, -10, -10, 1, 1, 0, 0, 0);
				if (window == 0)
				{
					_ = XCloseDisplay(display);
					_unavailable = true;
					return false;
				}

				_ = XSelectInput(display, window, StructureNotifyMask | PropertyChangeMask);

				_display = display;
				_window = window;
				_clipboardAtom = XInternAtom(display, "CLIPBOARD", false);
				_primaryAtom = XInternAtom(display, "PRIMARY", false);
				_targetsAtom = XInternAtom(display, "TARGETS", false);
				_utf8Atom = XInternAtom(display, "UTF8_STRING", false);
				_textAtom = XInternAtom(display, "TEXT", false);
				_wakeAtom = XInternAtom(display, "VOIDSTRAP_CLIPBOARD_WAKE", false);

				_owner = new Thread(PumpEvents)
				{
					IsBackground = true,
					Name = "Voidstrap clipboard"
				};
				_owner.Start();
				return true;
			}
			catch (Exception)
			{
				_unavailable = true;
				return false;
			}
		}
	}

	private static void PumpEvents()
	{
		byte[] buffer = new byte[192];
		while (true)
		{
			try
			{
				XNextEvent(_display, buffer);
			}
			catch (Exception)
			{
				return;
			}

			int type = ReadInt(buffer, 0);
			if (type == ClientMessage)
			{
				lock (Gate)
				{
					if (_hasText)
					{
						_ = XSetSelectionOwner(_display, _clipboardAtom, _window, 0);
						_ = XSetSelectionOwner(_display, _primaryAtom, _window, 0);
						_ = XFlush(_display);
					}
				}
				continue;
			}

			if (type == SelectionClear)
			{
				lock (Gate)
				{
					_hasText = false;
					_text = string.Empty;
				}
				continue;
			}

			if (type == SelectionRequest)
				ServeRequest(buffer);
		}
	}

	private static void ServeRequest(byte[] buffer)
	{
		nint requestor = ReadNint(buffer, 40);
		nint selection = ReadNint(buffer, 48);
		nint target = ReadNint(buffer, 56);
		nint property = ReadNint(buffer, 64);
		nint time = ReadNint(buffer, 72);

		if (property == 0)
			property = target;

		nint result = 0;
		try
		{
			if (target == _targetsAtom)
			{
				nint[] targets = [_targetsAtom, _utf8Atom, AtomString, _textAtom];
				byte[] payload = new byte[targets.Length * IntPtr.Size];
				for (int index = 0; index < targets.Length; index++)
					WriteNint(payload, index * IntPtr.Size, targets[index]);
				XChangeProperty(_display, requestor, property, AtomAtom, 32, PropModeReplace, payload, targets.Length);
				result = property;
			}
			else if (target == _utf8Atom || target == AtomString || target == _textAtom)
			{
				string text;
				lock (Gate)
				{
					text = _hasText ? _text : string.Empty;
				}

				byte[] payload = Encoding.UTF8.GetBytes(text);
				nint type = target == AtomString ? AtomString : _utf8Atom;
				XChangeProperty(_display, requestor, property, type, 8, PropModeReplace, payload, payload.Length);
				result = property;
			}
		}
		catch (Exception)
		{
			result = 0;
		}

		byte[] reply = new byte[192];
		WriteInt(reply, 0, SelectionNotify);
		WriteNint(reply, 24, _display);
		WriteNint(reply, 32, requestor);
		WriteNint(reply, 40, selection);
		WriteNint(reply, 48, target);
		WriteNint(reply, 56, result);
		WriteNint(reply, 64, time);
		XSendEvent(_display, requestor, false, 0, reply);
		_ = XFlush(_display);
	}

	private static string? ReadSelection()
	{
		nint display = 0;
		nint window = 0;
		try
		{
			display = XOpenDisplay(null);
			if (display == 0)
				return null;

			nint root = XDefaultRootWindow(display);
			window = XCreateSimpleWindow(display, root, -10, -10, 1, 1, 0, 0, 0);
			if (window == 0)
				return null;

			_ = XSelectInput(display, window, PropertyChangeMask);
			nint clipboard = XInternAtom(display, "CLIPBOARD", false);
			nint utf8 = XInternAtom(display, "UTF8_STRING", false);
			nint destination = XInternAtom(display, "VOIDSTRAP_CLIPBOARD_IN", false);

			if (XGetSelectionOwner(display, clipboard) == 0)
				return null;

			_ = XConvertSelection(display, clipboard, utf8, destination, window, 0);
			_ = XFlush(display);

			byte[] buffer = new byte[192];
			long deadline = Environment.TickCount64 + 1000;
			while (Environment.TickCount64 < deadline)
			{
				if (XPending(display) == 0)
				{
					Thread.Sleep(5);
					continue;
				}

				XNextEvent(display, buffer);
				if (ReadInt(buffer, 0) != SelectionNotify)
					continue;

				nint property = ReadNint(buffer, 56);
				if (property == 0)
					return null;

				return ReadProperty(display, window, property);
			}

			return null;
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			try
			{
				if (window != 0)
					_ = XDestroyWindow(display, window);
				if (display != 0)
					_ = XCloseDisplay(display);
			}
			catch (Exception)
			{
			}
		}
	}

	private static string? ReadProperty(nint display, nint window, nint property)
	{
		nint data = 0;
		try
		{
			int status = XGetWindowProperty(
				display,
				window,
				property,
				0,
				int.MaxValue / 4,
				true,
				AnyPropertyType,
				out _,
				out int format,
				out nint count,
				out _,
				out data);

			if (status != 0)
				return null;

			if (count <= 0)
				return string.Empty;

			if (data == 0 || format != 8)
				return null;

			byte[] bytes = new byte[(int)count];
			Marshal.Copy(data, bytes, 0, bytes.Length);
			return Encoding.UTF8.GetString(bytes);
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			if (data != 0)
			{
				try { _ = XFree(data); } catch (Exception) { }
			}
		}
	}

	private static int ReadInt(byte[] buffer, int offset) => BitConverter.ToInt32(buffer, offset);

	private static nint ReadNint(byte[] buffer, int offset) => (nint)BitConverter.ToInt64(buffer, offset);

	private static void WriteInt(byte[] buffer, int offset, int value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);

	private static void WriteNint(byte[] buffer, int offset, nint value) => BitConverter.GetBytes((long)value).CopyTo(buffer, offset);

	[LibraryImport("libX11.so.6")]
	private static partial int XInitThreads();

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XOpenDisplay(string? display);

	[LibraryImport("libX11.so.6")]
	private static partial int XCloseDisplay(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial nint XDefaultRootWindow(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, nint border, nint background);

	[LibraryImport("libX11.so.6")]
	private static partial int XDestroyWindow(nint display, nint window);

	[LibraryImport("libX11.so.6")]
	private static partial int XSelectInput(nint display, nint window, long mask);

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

	[LibraryImport("libX11.so.6")]
	private static partial int XSetSelectionOwner(nint display, nint selection, nint owner, nint time);

	[LibraryImport("libX11.so.6")]
	private static partial nint XGetSelectionOwner(nint display, nint selection);

	[LibraryImport("libX11.so.6")]
	private static partial int XConvertSelection(nint display, nint selection, nint target, nint property, nint requestor, nint time);

	[LibraryImport("libX11.so.6")]
	private static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, [In] byte[] data, int count);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetWindowProperty(
		nint display,
		nint window,
		nint property,
		nint offset,
		nint length,
		[MarshalAs(UnmanagedType.Bool)] bool delete,
		nint requestedType,
		out nint actualType,
		out int actualFormat,
		out nint itemCount,
		out nint bytesAfter,
		out nint data);

	[LibraryImport("libX11.so.6")]
	private static partial int XSendEvent(nint display, nint window, [MarshalAs(UnmanagedType.Bool)] bool propagate, long mask, [In] byte[] eventData);

	[LibraryImport("libX11.so.6")]
	private static partial int XNextEvent(nint display, [In, Out] byte[] eventData);

	[LibraryImport("libX11.so.6")]
	private static partial int XPending(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XFlush(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XFree(nint data);
}
