using System;
using System.Collections;
using System.Reflection;
using System.Windows;

namespace Voidstrap.UI;

internal static class LinuxAnimationParity
{
	private static readonly (string Field, string Slot)[] Flags = new[]
	{
		("_clientAreaAnimation", "ClientAreaAnimation"),
		("_uiEffects", "UIEffects"),
		("_menuAnimation", "MenuAnimation"),
		("_menuFade", "MenuFade"),
		("_comboBoxAnimation", "ComboBoxAnimation"),
		("_toolTipAnimation", "ToolTipAnimation"),
		("_tooltipFade", "ToolTipFade"),
		("_selectionFade", "SelectionFade"),
		("_listBoxSmoothScrolling", "ListBoxSmoothScrolling"),
		("_hotTracking", "HotTracking"),
		("_dropShadow", "DropShadow")
	};

	private static bool _applied;

	public static void Apply()
	{
		if (_applied || !Voidstrap.Utility.Platform.IsLinux)
			return;

		_applied = true;

		try
		{
			Type type = typeof(SystemParameters);
			Type? slotType = type.GetNestedType("CacheSlot", BindingFlags.NonPublic | BindingFlags.Public);
			BitArray? cacheValid = type
				.GetField("_cacheValid", BindingFlags.NonPublic | BindingFlags.Static)?
				.GetValue(null) as BitArray;

			if (slotType == null || cacheValid == null)
			{
				App.Logger?.WriteLine("LinuxAnimationParity::Apply", "System animation parameters could not be located");
				return;
			}

			int applied = 0;
			lock (cacheValid)
			{
				foreach ((string field, string slot) in Flags)
				{
					FieldInfo? backing = type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static);
					if (backing == null || backing.FieldType != typeof(bool))
						continue;

					if (!Enum.TryParse(slotType, slot, out object? parsed) || parsed == null)
						continue;

					int index = Convert.ToInt32(parsed);
					if (index < 0 || index >= cacheValid.Length)
						continue;

					backing.SetValue(null, true);
					cacheValid[index] = true;
					applied++;
				}
			}

			App.Logger?.WriteLine("LinuxAnimationParity::Apply", $"Enabled {applied} system animation parameters");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("LinuxAnimationParity::Apply", ex);
		}
	}
}
