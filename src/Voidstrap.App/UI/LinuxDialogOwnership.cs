using System;
using System.Windows;
using Voidstrap.Integrations.Overlays;
using Voidstrap.UI.Elements.Crosshair;
using Voidstrap.UI.Elements.Overlay;
using Voidstrap.Utility;

namespace Voidstrap.UI;

internal static class LinuxDialogOwnership
{
	public static void Adopt(Window window)
	{
		if (!Voidstrap.Utility.Platform.IsLinux || window.Owner is not null || IsStandalone(window))
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
		return window.ShowDialog();
	}

	private static Window? ResolveOwner(Window window)
	{
		Window? fallback = null;

		foreach (Window candidate in Application.Current.Windows)
		{
			if (ReferenceEquals(candidate, window) || IsStandalone(candidate) || !candidate.IsVisible)
				continue;

			if (candidate.IsActive)
				return candidate;

			fallback = candidate;
		}

		return fallback;
	}

	private static bool IsStandalone(Window window) =>
		window is OverlayWindow or CrosshairWindow or LinuxHomepageOverlayWindow;
}
