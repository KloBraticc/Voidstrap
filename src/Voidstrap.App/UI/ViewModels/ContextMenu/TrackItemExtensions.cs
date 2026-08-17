using System;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public static class TrackItemExtensions
{
	public static void SetDuration(this TrackItem item, TimeSpan dur)
	{
		item.Duration = dur;
	}
}
