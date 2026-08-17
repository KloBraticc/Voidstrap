using System;
using System.Drawing;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Enums;
using Voidstrap.Resources;
using Voidstrap.UI;

namespace Voidstrap.Extensions;

public static class IconEx
{
	public static Icon GetSized(this Icon icon, int width, int height)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			return icon;
		}
		return new Icon(icon, new Size(width, height));
	}

	public static ImageSource GetBootstrapperWindowIcon()
	{
		return GetIconSource(App.Settings.Prop.ActiveBootstrapperIcon);
	}

	public static ImageSource GetIconSource(BootstrapperIcon icon)
	{
		if (icon == BootstrapperIcon.IconCustom)
		{
			string custom = App.Settings.Prop.BootstrapperIconCustomLocation;
			if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
			{
				ImageSource? loaded = DecodeIcon(() => File.OpenRead(custom));
				if (loaded != null)
				{
					return loaded;
				}
			}
			icon = BootstrapperIcon.IconVoidstrap;
		}

		if (Voidstrap.Utility.Platform.IsWindows)
		{
			try
			{
				return icon.GetIcon().GetImageSource();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("IconEx::GetIconSource", "System.Drawing fallback failed: " + ex.Message);
			}
		}

		return Voidstrap.Utility.SafeImaging.FromUri(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute));
	}

	private static ImageSource? DecodeIcon(Func<Stream?> open)
	{
		try
		{
			using Stream? stream = open();
			if (stream == null)
			{
				return null;
			}
			using MemoryStream buffer = new MemoryStream();
			stream.CopyTo(buffer);
			buffer.Position = 0L;
			ImageSource source = GetLargestFrame(buffer);
			source.Freeze();
			return source;
		}
		catch
		{
			return null;
		}
	}

	public static ImageSource GetImageSource(this Icon icon, bool handleException = true)
	{
		using MemoryStream memoryStream = new MemoryStream();
		icon.Save(memoryStream);
		memoryStream.Position = 0L;
		if (handleException)
		{
			try
			{
				return GetLargestFrame(memoryStream);
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("IconEx::GetImageSource", ex);
				Frontend.ShowMessageBox(string.Format(Strings.Dialog_IconLoadFailed, ex.Message));
				return BootstrapperIcon.IconVoidstrap.GetIcon().GetImageSource(handleException: false);
			}
		}
		return GetLargestFrame(memoryStream);
	}

	private static ImageSource GetLargestFrame(MemoryStream stream)
	{
		BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
		BitmapFrame best = decoder.Frames[0];
		foreach (BitmapFrame frame in decoder.Frames)
		{
			if (frame.PixelWidth > best.PixelWidth)
			{
				best = frame;
			}
		}
		return best;
	}
}
