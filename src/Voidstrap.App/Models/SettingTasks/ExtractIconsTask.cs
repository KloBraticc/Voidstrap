using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.Models.SettingTasks;

public class ExtractIconsTask : BoolBaseTask
{
	private static readonly IReadOnlyDictionary<string, BootstrapperIcon> AllowedIcons = new Dictionary<string, BootstrapperIcon>
	{
		["Icon2008.ico"] = BootstrapperIcon.Icon2008,
		["Icon2011.ico"] = BootstrapperIcon.Icon2011,
		["Icon2017.ico"] = BootstrapperIcon.Icon2017,
		["Icon2019.ico"] = BootstrapperIcon.Icon2019,
		["Icon2022.ico"] = BootstrapperIcon.Icon2022,
		["IconVoidstrap.ico"] = BootstrapperIcon.IconVoidstrap,
		["IconEarly2015.ico"] = BootstrapperIcon.IconEarly2015,
		["IconLate2015.ico"] = BootstrapperIcon.IconLate2015
	};

	private static string IconsPath => Path.Combine(Paths.Base, Strings.Paths_Icons);

	private static string _path => IconsPath;

	public ExtractIconsTask()
		: base("ExtractIcons")
	{
		OriginalState = Directory.Exists(_path);
	}

	public static void ExtractAll(bool overwrite)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			ExtractAllPortable(overwrite);
			return;
		}
		try
		{
			Directory.CreateDirectory(IconsPath);
			foreach (KeyValuePair<string, BootstrapperIcon> entry in AllowedIcons)
			{
				string destination = Path.Combine(IconsPath, entry.Key);
				if (!overwrite && File.Exists(destination))
				{
					continue;
				}
				Filesystem.AssertReadOnly(destination);
				using FileStream stream = File.Create(destination);
				entry.Value.GetIcon().Save(stream);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("ExtractIconsTask::ExtractAll", ex);
		}
	}

	private static void ExtractAllPortable(bool overwrite)
	{
		try
		{
			Directory.CreateDirectory(IconsPath);
			foreach (KeyValuePair<string, BootstrapperIcon> entry in AllowedIcons)
			{
				string destination = Path.Combine(IconsPath, entry.Key);
				string portable = Path.ChangeExtension(destination, ".png");
				if (!overwrite && File.Exists(destination) && File.Exists(portable))
				{
					continue;
				}

				byte[]? bytes = ReadPackedIcon(entry.Key);
				if (bytes == null)
				{
					continue;
				}

				Filesystem.AssertReadOnly(destination);
				File.WriteAllBytes(destination, bytes);
				WritePortableIcon(bytes, portable);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("ExtractIconsTask::ExtractAllPortable", ex);
		}
	}

	private static byte[]? ReadPackedIcon(string fileName)
	{
		string assembly = typeof(ExtractIconsTask).Assembly.GetName().Name ?? "Voidstrap";
		foreach (string candidate in new[]
		{
			"pack://application:,,,/" + assembly + ";component/Resources/" + fileName,
			"pack://application:,,,/Resources/" + fileName
		})
		{
			try
			{
				System.Windows.Resources.StreamResourceInfo? info = System.Windows.Application.GetResourceStream(new Uri(candidate, UriKind.Absolute));
				using Stream? source = info?.Stream;
				if (source == null)
				{
					continue;
				}

				using MemoryStream buffer = new();
				source.CopyTo(buffer);
				return buffer.ToArray();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("ExtractIconsTask::ReadPackedIcon", "Could not read " + candidate + ": " + ex.Message);
			}
		}

		return null;
	}

	private static void WritePortableIcon(byte[] bytes, string destination)
	{
		try
		{
			System.Windows.Media.Imaging.BitmapSource? decoded = SafeImaging.FromBytes(bytes, 256);
			if (decoded == null || decoded.PixelWidth <= 0 || decoded.PixelHeight <= 0)
			{
				return;
			}

			System.Windows.Media.Imaging.BitmapSource source = decoded.Format == System.Windows.Media.PixelFormats.Bgra32
				? decoded
				: new System.Windows.Media.Imaging.FormatConvertedBitmap(decoded, System.Windows.Media.PixelFormats.Bgra32, null, 0d);

			int stride = source.PixelWidth * 4;
			byte[] pixels = new byte[stride * source.PixelHeight];
			source.CopyPixels(pixels, stride, 0);

			using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image =
				SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(pixels, source.PixelWidth, source.PixelHeight);
			Filesystem.AssertReadOnly(destination);
			using FileStream stream = File.Create(destination);
			image.Save(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ExtractIconsTask::WritePortableIcon", "Could not convert " + Path.GetFileName(destination) + ": " + ex.Message);
		}
	}

	private static void RemoveAll()
	{
		try
		{
			if (!Directory.Exists(IconsPath))
			{
				return;
			}

			foreach (string name in AllowedIcons.Keys)
			{
				foreach (string path in new[] { Path.Combine(IconsPath, name), Path.Combine(IconsPath, Path.ChangeExtension(name, ".png")) })
				{
					if (File.Exists(path))
					{
						File.Delete(path);
					}
				}
			}

			if (Directory.GetFileSystemEntries(IconsPath).Length == 0)
			{
				Directory.Delete(IconsPath);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ExtractIconsTask::RemoveAll", "Could not remove extracted icons: " + ex.Message);
		}
	}

	public override void Execute()
	{
		if (!NewState)
		{
			RemoveAll();
			OriginalState = false;
			return;
		}
		ExtractAll(overwrite: true);
		NewState = Directory.Exists(_path);
		OriginalState = NewState;
	}
}
