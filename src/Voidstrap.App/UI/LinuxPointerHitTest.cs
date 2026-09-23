using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Voidstrap.UI;

public static class LinuxPointerHitTest
{
	public static void Install()
	{
#if CROSSPLAT
		if (_installed || !Voidstrap.Utility.Platform.IsLinux)
			return;

		_installed = true;
		InputManager.Current.PostProcessInput += OnPostProcessInput;
#endif
	}

#if CROSSPLAT
	private static readonly ConditionalWeakTable<IPortablePresentationSourceHost, object> Released = new();
	private static readonly object Marker = new();
	private static bool _installed;

	private static void OnPostProcessInput(object sender, ProcessInputEventArgs e)
	{
		if (Mouse.PrimaryDevice.ActiveSource is not IPortablePresentationSourceHost source)
			return;

		if (source.HitTestOverride is not null)
			source.HitTestOverride = null!;
		if (source.HitTestAllOverride is not null)
			source.HitTestAllOverride = null!;
		if (source.HitTestAllBufferOverride is not null)
			source.HitTestAllBufferOverride = null!;

		if (!Released.TryGetValue(source, out _))
		{
			Released.Add(source, Marker);
			App.Logger.WriteLine("LinuxPointerHitTest::Release", "Pointer hit testing for " + source.RootVisual?.GetType().Name + " now runs on the UI thread");
		}
	}
#endif
}
