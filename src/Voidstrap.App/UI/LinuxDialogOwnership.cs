using System;
using System.Collections.Generic;
using System.Windows;
using Voidstrap.Integrations.Overlays;
using Voidstrap.UI.Elements.Crosshair;
using Voidstrap.UI.Elements.Overlay;
using Voidstrap.Utility;

namespace Voidstrap.UI;

internal static class LinuxDialogOwnership
{
	private static readonly List<Window> OpenDialogs = [];

	public static void Adopt(Window window)
	{
		if (!Voidstrap.Utility.Platform.IsLinux || window.Owner is not null || IsStandalone(window) || RoundedWindowChrome.IsOverlaySurface(window))
			return;

		Window? owner = ResolveOwner(window);

		if (owner is null)
			return;

		try
		{
			window.Owner = owner;
			App.Logger.WriteLine("LinuxDialogOwnership", $"Adopted {owner.GetType().Name} as the owner of {window.GetType().Name}");
		}
		catch (InvalidOperationException ex)
		{
			App.Logger.WriteLine("LinuxDialogOwnership", $"Could not adopt an owner for {window.GetType().Name}: {ex.Message}");
		}
	}

	public static bool? ShowOwnedDialog(this Window window)
	{
		Adopt(window);
		return ShowTracked(window);
	}

	public static bool? ShowTopLevelDialog(this Window window)
	{
		return ShowTracked(window);
	}

	private static bool? ShowTracked(Window window)
	{
		OpenDialogs.Add(window);
		try
		{
			return window.ShowDialog();
		}
		finally
		{
			OpenDialogs.Remove(window);
		}
	}

	private static Window? ResolveOwner(Window window)
	{
		for (int index = OpenDialogs.Count - 1; index >= 0; index--)
		{
			Window dialog = OpenDialogs[index];
			if (!ReferenceEquals(dialog, window) && CanOwn(dialog))
				return dialog;
		}

		Window? fallback = null;

		foreach (Window candidate in Application.Current.Windows)
		{
			if (ReferenceEquals(candidate, window) || !CanOwn(candidate))
				continue;

			if (candidate.IsActive)
				return candidate;

			fallback = candidate;
		}

		return fallback;
	}

	private static bool CanOwn(Window window) =>
		window.IsVisible
		&& window.WindowState != System.Windows.WindowState.Minimized
		&& (window.ShowInTaskbar || window.Owner is not null)
		&& !IsStandalone(window)
		&& !RoundedWindowChrome.IsOverlaySurface(window);

	private static bool IsStandalone(Window window) =>
		window is OverlayWindow or CrosshairWindow or LinuxHomepageOverlayWindow or NotificationWindow or Voidstrap.UI.Elements.ContextMenu.MenuContainer;
}
