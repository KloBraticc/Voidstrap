using System;
using System.Media;
using System.Windows;

namespace Voidstrap.Utility;

internal static class SafeSystemSounds
{
	public static SystemSound? Get(MessageBoxImage image)
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		try
		{
			return image switch
			{
				MessageBoxImage.Hand => SystemSounds.Hand,
				MessageBoxImage.Question => SystemSounds.Question,
				MessageBoxImage.Exclamation => SystemSounds.Exclamation,
				MessageBoxImage.Asterisk => SystemSounds.Asterisk,
				_ => null
			};
		}
		catch
		{
			return null;
		}
	}

	public static void Play(SystemSound? sound)
	{
		if (sound == null || !OperatingSystem.IsWindows())
		{
			return;
		}
		try
		{
			sound.Play();
		}
		catch
		{
		}
	}
}
