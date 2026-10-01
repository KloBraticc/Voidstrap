using System;
using System.Collections.Generic;

namespace Voidstrap.UI;

public static class LinuxWindowGlContext
{
	private const string LogIdent = "LinuxWindowGlContext";

	public static void Install()
	{
#if CROSSPLAT
		if (!OperatingSystem.IsLinux() || !Voidstrap.Utility.LinuxStartup.UsesOpenGl)
			return;

		Silk.NET.Windowing.IWindowPlatform? inner = null;
		foreach (Silk.NET.Windowing.IWindowPlatform platform in Silk.NET.Windowing.Window.Platforms)
		{
			if (platform is ContextReleasingPlatform)
				return;
			if (!platform.IsViewOnly && platform.IsApplicable)
			{
				inner = platform;
				break;
			}
		}

		if (inner is null)
		{
			App.Logger.WriteLine(LogIdent, "No window platform was found, transparent windows keep their GL context current");
			return;
		}

		ContextReleasingPlatform wrapper = new(inner);
		Silk.NET.Windowing.Window.Add(wrapper);
		Silk.NET.Windowing.Window.Prioritize(wrapper);
		App.Logger.WriteLine(LogIdent, "Transparent windows release their GL context before the OpenGL renderer draws");
#endif
	}

#if CROSSPLAT
	private static void Release(Silk.NET.Windowing.IWindow window)
	{
		try
		{
			if (window.GLContext is { IsCurrent: true } context)
				context.Clear();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Could not release a window GL context: " + ex.Message);
		}
	}

	private sealed class ContextReleasingPlatform(Silk.NET.Windowing.IWindowPlatform inner) : Silk.NET.Windowing.IWindowPlatform
	{
		public string Name => inner.Name;

		public bool IsViewOnly => inner.IsViewOnly;

		public bool IsApplicable => inner.IsApplicable;

		public Silk.NET.Windowing.IView GetView(Silk.NET.Windowing.ViewOptions? opts = null) => inner.GetView(opts);

		public void ClearContexts() => inner.ClearContexts();

		public IEnumerable<Silk.NET.Windowing.IMonitor> GetMonitors() => inner.GetMonitors();

		public Silk.NET.Windowing.IMonitor GetMainMonitor() => inner.GetMainMonitor();

		public bool IsSourceOfView(Silk.NET.Windowing.IView view) => inner.IsSourceOfView(view);

		public Silk.NET.Windowing.IWindow CreateWindow(Silk.NET.Windowing.WindowOptions opts)
		{
			if (opts.API.API is not (Silk.NET.Windowing.ContextAPI.OpenGL or Silk.NET.Windowing.ContextAPI.OpenGLES))
				return inner.CreateWindow(opts);

			opts.IsContextControlDisabled = true;
			Silk.NET.Windowing.IWindow window = inner.CreateWindow(opts);
			window.Load += () => Release(window);
			return window;
		}
	}
#endif
}
