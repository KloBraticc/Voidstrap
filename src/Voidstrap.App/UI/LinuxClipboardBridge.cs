using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Voidstrap.UI;

public static class LinuxClipboardBridge
{
	private const string LogIdent = "LinuxClipboardBridge";

#if CROSSPLAT
	private static readonly ProGPU.Wpf.Interop.PortableWpfServiceKey[] ServiceKeys =
	[
		ProGPU.Wpf.Interop.PortableWpfServiceKey.PresentationCore,
		ProGPU.Wpf.Interop.PortableWpfServiceKey.WinForms
	];

	private static readonly object Sync = new();
	private static readonly Dictionary<ProGPU.Wpf.Interop.PortableWpfServiceKey, IDisposable> Registrations = new();
	private static Dispatcher? _dispatcher;
	private static int _invalidationQueued;
	private static long _macChangeCount = -1;
	private static bool _installed;

	public static bool IsActive => _installed;
#else
	public static bool IsActive => false;
#endif

	public static void Install()
	{
#if CROSSPLAT
		if (_installed || !(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
			return;

		if (Environment.GetEnvironmentVariable("VOIDSTRAP_NATIVE_CLIPBOARD") == "0")
		{
			App.Logger.WriteLine(LogIdent, "The system clipboard bridge is turned off by VOIDSTRAP_NATIVE_CLIPBOARD");
			return;
		}

		if (OperatingSystem.IsMacOS() ? !Voidstrap.Platform.MacOS.MacOSClipboard.IsAvailable : !Voidstrap.Platform.Linux.LinuxClipboard.IsAvailable)
		{
			App.Logger.WriteLine(LogIdent, "No system clipboard is available, text boxes keep the renderer clipboard");
			return;
		}

		_installed = true;
		_dispatcher = Dispatcher.CurrentDispatcher;
		foreach (ProGPU.Wpf.Interop.PortableWpfServiceKey key in ServiceKeys)
			Register(key);

		ProGPU.Wpf.Interop.PortableWpfServiceRegistry.ClipboardServiceRegistered += OnClipboardServiceRegistered;
		if (OperatingSystem.IsLinux())
			Voidstrap.Platform.Linux.LinuxClipboard.Changed += OnClipboardChanged;
		EventManager.RegisterClassHandler(typeof(UIElement), CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(OnPreviewExecuted), true);
		EventManager.RegisterClassHandler(typeof(UIElement), CommandManager.PreviewCanExecuteEvent, new CanExecuteRoutedEventHandler(OnPreviewCanExecute), true);
		App.Logger.WriteLine(LogIdent, OperatingSystem.IsLinux() && Voidstrap.Platform.Linux.LinuxClipboard.TracksChanges
			? "Copy and paste now use the system clipboard and follow changes from other apps"
			: "Copy and paste now use the system clipboard, other apps are read again on every paste");
#endif
	}

	public static void Invalidate()
	{
#if CROSSPLAT
		if (!_installed)
			return;

		Dispatcher? dispatcher = _dispatcher;
		if (dispatcher is null || dispatcher.HasShutdownStarted)
			return;

		if (dispatcher.CheckAccess())
		{
			RegisterAll();
			return;
		}

		if (Interlocked.Exchange(ref _invalidationQueued, 1) == 1)
			return;

		dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(OnInvalidationDispatched));
#endif
	}

#if CROSSPLAT
	private static void OnInvalidationDispatched()
	{
		Volatile.Write(ref _invalidationQueued, 0);
		RegisterAll();
	}

	private static void RegisterAll()
	{
		lock (Sync)
		{
			foreach (ProGPU.Wpf.Interop.PortableWpfServiceKey key in ServiceKeys)
				Register(key);
		}
	}

	private static void Register(ProGPU.Wpf.Interop.PortableWpfServiceKey key, ProGPU.Wpf.Interop.IPortableClipboardServiceRegistrar? registrar = null)
	{
		lock (Sync)
		{
			try
			{
				if (registrar is null
					&& (!ProGPU.Wpf.Interop.PortableWpfServiceRegistry.TryGetClipboardService(key, out registrar) || registrar is null))
					return;

				if (Registrations.Remove(key, out IDisposable? previous))
					previous.Dispose();

				Registrations[key] = registrar.Register(ReadText, WriteText);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogIdent, "The system clipboard could not be connected for " + key + ": " + ex.Message);
			}
		}
	}

	private static void OnClipboardServiceRegistered(ProGPU.Wpf.Interop.IPortableClipboardServiceRegistrar service)
	{
		if (Array.IndexOf(ServiceKeys, service.ServiceKey) >= 0)
			Register(service.ServiceKey, service);
	}

	private static void OnClipboardChanged()
	{
		Invalidate();
	}

	private static void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
	{
		if (e.Command == ApplicationCommands.Paste)
			RegisterAll();
	}

	private static void OnPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
	{
		if (e.Command != ApplicationCommands.Paste || !OperatingSystem.IsMacOS())
			return;
		long count = Voidstrap.Platform.MacOS.MacOSClipboard.ChangeCount;
		if (Interlocked.Exchange(ref _macChangeCount, count) != count)
			RegisterAll();
	}

	private static string? ReadText()
	{
		string? text = OperatingSystem.IsMacOS() ? Voidstrap.Platform.MacOS.MacOSClipboard.GetText() : Voidstrap.Platform.Linux.LinuxClipboard.GetText();
		return string.IsNullOrEmpty(text) ? null : text;
	}

	private static void WriteText(string? text)
	{
		if (!(OperatingSystem.IsMacOS() ? Voidstrap.Platform.MacOS.MacOSClipboard.SetText(text ?? string.Empty) : Voidstrap.Platform.Linux.LinuxClipboard.SetText(text ?? string.Empty)))
			App.Logger.WriteLine(LogIdent, "The system clipboard could not be updated");
	}
#endif
}
