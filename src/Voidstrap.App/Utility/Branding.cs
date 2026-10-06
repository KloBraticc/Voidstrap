using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Voidstrap.Utility;

internal static class Branding
{
	public const string DefaultName = "Voidstrap";

	public const int MaxNameLength = 32;

	private const string UpperName = "VOIDSTRAP";

	private const string LOG_IDENT = "Branding";

	private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

	private static readonly object Sync = new();

	private static bool? _hasIcon;

	private static BitmapSource? _icon;

	public static string Folder => Path.Combine(Paths.Data, "Branding");

	public static string IconPngPath => Path.Combine(Folder, "AppIcon.png");

	public static string IconIcoPath => Path.Combine(Folder, "AppIcon.ico");

	public static string CustomName
	{
		get
		{
			try
			{
				string name = App.Settings?.Prop?.BrandName ?? "";
				return IsValidName(name) && !string.Equals(name, DefaultName, StringComparison.Ordinal) ? name : "";
			}
			catch (Exception)
			{
				return "";
			}
		}
	}

	public static string Name
	{
		get
		{
			string custom = CustomName;
			return custom.Length > 0 ? custom : DefaultName;
		}
	}

	public static bool RenameActive => CustomName.Length > 0;

	public static string RpcLogoUrl => App.ProjectLogoUrl;

	public static bool HasCustomIcon
	{
		get
		{
			lock (Sync)
			{
				if (_hasIcon == null)
				{
					if (string.IsNullOrEmpty(Paths.Data))
						return false;
					try
					{
						_hasIcon = File.Exists(IconPngPath) && File.Exists(IconIcoPath);
					}
					catch (Exception)
					{
						return false;
					}
				}
				return _hasIcon.Value;
			}
		}
	}

	public static bool Active => RenameActive || HasCustomIcon;

	public static long IconStamp
	{
		get
		{
			try
			{
				return HasCustomIcon ? File.GetLastWriteTimeUtc(IconPngPath).Ticks : 0;
			}
			catch (Exception)
			{
				return 0;
			}
		}
	}

	public static bool IsValidName(string? name)
	{
		return !string.IsNullOrEmpty(name)
			&& name.Length <= MaxNameLength
			&& string.Equals(name, name.Trim(), StringComparison.Ordinal)
			&& !name.Any(char.IsControl);
	}

	public static string Apply(string? text)
	{
		if (string.IsNullOrEmpty(text))
			return text ?? "";
		string custom = CustomName;
		if (custom.Length == 0)
			return text;
		if (text.Contains(DefaultName, StringComparison.Ordinal))
			text = ReplaceWord(text, DefaultName, custom);
		if (text.Contains(UpperName, StringComparison.Ordinal))
			text = ReplaceWord(text, UpperName, custom.ToUpperInvariant());
		return text;
	}

	private static string ReplaceWord(string text, string word, string name)
	{
		StringBuilder? builder = null;
		int last = 0;
		int index = 0;
		while ((index = text.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
		{
			int end = index + word.Length;
			bool wholeWord = (index == 0 || !char.IsLetterOrDigit(text[index - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]));
			if (wholeWord && !IsInsideName(text, index, name, word) && !IsMachineToken(text, index, end))
			{
				builder ??= new StringBuilder(text.Length + name.Length);
				builder.Append(text, last, index - last).Append(name);
				last = end;
			}
			index = end;
		}
		if (builder == null)
			return text;
		builder.Append(text, last, text.Length - last);
		return builder.ToString();
	}

	private static bool IsInsideName(string text, int index, string name, string word)
	{
		for (int offset = name.IndexOf(word, StringComparison.Ordinal); offset >= 0; offset = name.IndexOf(word, offset + 1, StringComparison.Ordinal))
		{
			int start = index - offset;
			if (start >= 0 && start + name.Length <= text.Length && string.CompareOrdinal(text, start, name, 0, name.Length) == 0)
				return true;
		}
		return false;
	}

	private static bool IsMachineToken(string text, int index, int end)
	{
		int start = index;
		while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
			start--;
		int stop = end;
		while (stop < text.Length && !char.IsWhiteSpace(text[stop]))
			stop++;
		ReadOnlySpan<char> token = text.AsSpan(start, stop - start).Trim("([{\"'").TrimEnd(".,;:!?)]}\"'");
		return token.Contains("://", StringComparison.Ordinal)
			|| token.IndexOfAny("\\/@_%=<>|-") >= 0
			|| token.Contains('.')
			|| token.Contains(':');
	}

	public static BitmapSource? Icon
	{
		get
		{
			if (!HasCustomIcon)
				return null;
			lock (Sync)
			{
				if (_icon != null)
					return _icon;
			}
			BitmapSource? loaded = SafeImaging.FromFile(IconPngPath, 256);
			loaded?.Freeze();
			lock (Sync)
			{
				_icon ??= loaded;
				return _icon;
			}
		}
	}

	public static BitmapSource? LoadIcon(int decodeWidth)
	{
		return HasCustomIcon ? SafeImaging.FromFile(IconPngPath, decodeWidth) : null;
	}

	public static System.Drawing.Icon? CreateDrawingIcon()
	{
		if (!Platform.IsWindows || !HasCustomIcon)
			return null;
		try
		{
			return new System.Drawing.Icon(IconIcoPath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The custom icon could not be loaded: " + ex.Message);
			return null;
		}
	}

	public static bool IsDefaultLogo(object? value)
	{
		return value is ImageSource source
			&& source.ToString().EndsWith("/Voidstrap.png", StringComparison.OrdinalIgnoreCase);
	}

	public static void ImportIcon(string path)
	{
		byte[] data = File.ReadAllBytes(path);
		if (data.Length == 0 || data.Length > 32 * 1024 * 1024)
			throw new InvalidDataException("The image is empty or larger than 32 MB.");
		using Image<Rgba32> image = Decode(data, path) ?? throw new InvalidDataException("This image format could not be read. Use a PNG, JPG, BMP, GIF, WEBP, TIFF, TGA or ICO file.");
		if (image.Width < 8 || image.Height < 8)
			throw new InvalidDataException("The image is too small, use one at least 16 by 16 pixels.");
		Directory.CreateDirectory(Folder);
		string png = IconPngPath + "." + Environment.ProcessId + ".tmp";
		try
		{
			using (Image<Rgba32> square = image.Clone(context => context.Resize(new ResizeOptions
			{
				Size = new SixLabors.ImageSharp.Size(256, 256),
				Mode = ResizeMode.Pad,
				Sampler = KnownResamplers.Lanczos3,
				PadColor = SixLabors.ImageSharp.Color.Transparent
			})))
			{
				square.SaveAsPng(png);
			}
			IconFile.Write(image, IconIcoPath, IcoSizes, ResizeMode.Pad);
			File.Move(png, IconPngPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(png))
				File.Delete(png);
		}
		ResetIconCache();
		App.Logger?.WriteLine(LOG_IDENT, "Imported a custom application icon");
	}

	public static void RemoveIcon()
	{
		foreach (string file in new[] { IconPngPath, IconIcoPath })
		{
			if (File.Exists(file))
				File.Delete(file);
		}
		ResetIconCache();
		App.Logger?.WriteLine(LOG_IDENT, "Removed the custom application icon");
	}

	private static void ResetIconCache()
	{
		lock (Sync)
		{
			_hasIcon = null;
			_icon = null;
		}
	}

	private static Image<Rgba32>? Decode(byte[] data, string path)
	{
		try
		{
			Image<Rgba32>? icon = IconFile.ReadLargestFrame(data);
			if (icon != null)
				return icon;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The icon file could not be read directly: " + ex.Message);
		}
		try
		{
			DecoderOptions options = new DecoderOptions { TargetSize = new SixLabors.ImageSharp.Size(1024, 1024), MaxFrames = 1 };
			return SixLabors.ImageSharp.Image.Load<Rgba32>(options, data);
		}
		catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The image library could not read the file, trying the system decoder: " + ex.Message);
		}
		BitmapSource? system = SafeImaging.FromFile(path, 1024);
		if (system == null)
			return null;
		BitmapSource bgra = system.Format == PixelFormats.Bgra32 ? system : new FormatConvertedBitmap(system, PixelFormats.Bgra32, null, 0);
		int stride = checked(bgra.PixelWidth * 4);
		byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
		bgra.CopyPixels(pixels, stride, 0);
		using Image<Bgra32> converted = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(pixels, bgra.PixelWidth, bgra.PixelHeight);
		return converted.CloneAs<Rgba32>();
	}
}
