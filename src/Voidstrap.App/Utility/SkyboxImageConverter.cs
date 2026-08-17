using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Voidstrap.Utility;

public static class SkyboxImageConverter
{
	public const string CustomPackName = "Voidstrap Custom";

	private static readonly string[] FaceFileNames = ["sky512_bk.tex", "sky512_dn.tex", "sky512_ft.tex", "sky512_lf.tex", "sky512_rt.tex", "sky512_up.tex"];

	private const long MaximumInputBytes = 67108864L;

	private const long MaximumPixels = 16777216L;

	private const int MaximumDimension = 16384;

	private const int FaceSize = 512;

	public static string CustomPackDirectory => Paths.CustomSkybox;

	public static bool HasCustomPack()
	{
		return IsValidPackDirectory(CustomPackDirectory);
	}

	public static bool IsValidPackDirectory(string directory)
	{
		try
		{
			return FaceFileNames.All(name => IsValidFaceFile(Path.Combine(directory, name)));
		}
		catch
		{
			return false;
		}
	}

	private static bool IsValidFaceFile(string path)
	{
		FileInfo file = new(path);
		if (!file.Exists || file.Length <= 0 || file.Length > 16777216L)
			return false;
		using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
		if (TryReadDdsDimensions(stream, out int ddsWidth, out int ddsHeight))
			return ddsWidth > 0 && ddsHeight > 0 && ddsWidth <= MaximumDimension && ddsHeight <= MaximumDimension && (long)ddsWidth * ddsHeight <= MaximumPixels;
		stream.Position = 0;
		var info = Image.Identify(new DecoderOptions { MaxFrames = 1 }, stream);
		return info != null && info.Width > 0 && info.Height > 0 && info.Width <= MaximumDimension && info.Height <= MaximumDimension && (long)info.Width * info.Height <= MaximumPixels;
	}

	public static Task ImportAsync(IReadOnlyDictionary<string, string> sourceFiles, CancellationToken token)
	{
		return Task.Run(() => Import(sourceFiles, token), token);
	}

	public static void Remove()
	{
		if (!Directory.Exists(CustomPackDirectory))
			return;
		NormalizeAttributes(CustomPackDirectory);
		Directory.Delete(CustomPackDirectory, true);
	}

	private static void Import(IReadOnlyDictionary<string, string> sourceFiles, CancellationToken token)
	{
		if (sourceFiles.Count != FaceFileNames.Length || FaceFileNames.Any(name => !sourceFiles.TryGetValue(name, out string? source) || string.IsNullOrWhiteSpace(source)))
			throw new InvalidDataException("Choose an image for every skybox face");

		string operationId = Guid.NewGuid().ToString("N");
		string stagingDirectory = CustomPackDirectory + ".new." + operationId;
		string backupDirectory = CustomPackDirectory + ".backup." + operationId;
		Dictionary<string, byte[]> converted = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			Directory.CreateDirectory(stagingDirectory);
			foreach (string faceName in FaceFileNames)
			{
				token.ThrowIfCancellationRequested();
				string sourcePath = Path.GetFullPath(sourceFiles[faceName]);
				if (!converted.TryGetValue(sourcePath, out byte[]? bytes))
				{
					bytes = ConvertImage(sourcePath, token);
					converted[sourcePath] = bytes;
				}
				File.WriteAllBytes(Path.Combine(stagingDirectory, faceName), bytes);
			}
			if (!IsValidPackDirectory(stagingDirectory))
				throw new InvalidDataException("The custom skybox could not be completed");
			if (Directory.Exists(CustomPackDirectory))
				Directory.Move(CustomPackDirectory, backupDirectory);
			Directory.Move(stagingDirectory, CustomPackDirectory);
			TryDeleteDirectory(backupDirectory);
		}
		catch
		{
			if (!Directory.Exists(CustomPackDirectory) && Directory.Exists(backupDirectory))
				Directory.Move(backupDirectory, CustomPackDirectory);
			throw;
		}
		finally
		{
			TryDeleteDirectory(stagingDirectory);
			if (Directory.Exists(CustomPackDirectory))
				TryDeleteDirectory(backupDirectory);
			converted.Clear();
		}
	}

	private static byte[] ConvertImage(string sourcePath, CancellationToken token)
	{
		FileInfo file = new(sourcePath);
		if (!file.Exists || file.Length <= 0 || file.Length > MaximumInputBytes)
			throw new InvalidDataException("The selected image is empty or too large");
		using FileStream stream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
		if (TryReadDdsDimensions(stream, out int ddsWidth, out int ddsHeight))
		{
			if (file.Length > 16777216L)
				throw new InvalidDataException("The selected DDS image is too large");
			if (ddsWidth < 1 || ddsHeight < 1 || ddsWidth > MaximumDimension || ddsHeight > MaximumDimension || (long)ddsWidth * ddsHeight > MaximumPixels)
				throw new InvalidDataException("The selected DDS image dimensions are not supported");
			stream.Position = 0;
			byte[] copy = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
			stream.ReadExactly(copy);
			return copy;
		}

		using Image<Rgba32> image = DecodeImage(stream);
		image.Mutate(context => context.AutoOrient().Resize(new ResizeOptions
		{
			Size = new Size(FaceSize, FaceSize),
			Mode = ResizeMode.Crop,
			Position = AnchorPositionMode.Center,
			Sampler = KnownResamplers.Lanczos3
		}));
		token.ThrowIfCancellationRequested();
		using MemoryStream output = new();
		image.Save(output, new PngEncoder
		{
			ColorType = PngColorType.RgbWithAlpha,
			CompressionLevel = PngCompressionLevel.Level6,
			FilterMethod = PngFilterMethod.Adaptive
		});
		if (output.Length <= 0 || output.Length > 16777216L)
			throw new InvalidDataException("The converted skybox face is too large");
		return output.ToArray();
	}

	private static Image<Rgba32> DecodeImage(Stream stream)
	{
		DecoderOptions decoderOptions = new() { MaxFrames = 1 };
		try
		{
			stream.Position = 0;
			var info = Image.Identify(decoderOptions, stream);
			ValidateDimensions(info?.Width ?? 0, info?.Height ?? 0);
			stream.Position = 0;
			return Image.Load<Rgba32>(decoderOptions, stream);
		}
		catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
		{
			try
			{
				stream.Position = 0;
				System.Windows.Media.Imaging.BitmapDecoder decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
				if (decoder.Frames.Count == 0)
					throw new InvalidDataException("The selected image has no usable frames");
				System.Windows.Media.Imaging.BitmapSource source = decoder.Frames[0];
				ValidateDimensions(source.PixelWidth, source.PixelHeight);
				System.Windows.Media.Imaging.FormatConvertedBitmap converted = new(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
				int stride = checked(converted.PixelWidth * 4);
				byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * converted.PixelHeight));
				converted.CopyPixels(pixels, stride, 0);
				using Image<Bgra32> bgra = Image.LoadPixelData<Bgra32>(pixels, converted.PixelWidth, converted.PixelHeight);
				return bgra.CloneAs<Rgba32>();
			}
			catch (Exception fallbackException)
			{
				throw new InvalidDataException("The selected file is not a supported image", new AggregateException(ex, fallbackException));
			}
		}
	}

	private static void ValidateDimensions(int width, int height)
	{
		if (width < 1 || height < 1)
			throw new InvalidDataException("The selected image dimensions are invalid");
		if (width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumPixels)
			throw new InvalidDataException("The selected image dimensions are too large");
	}

	private static bool TryReadDdsDimensions(Stream stream, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (!stream.CanSeek || stream.Length < 128)
			return false;
		Span<byte> header = stackalloc byte[128];
		int read = stream.Read(header);
		stream.Position = 0;
		if (read < header.Length || header[0] != (byte)'D' || header[1] != (byte)'D' || header[2] != (byte)'S' || header[3] != (byte)' ' || BitConverter.ToInt32(header[4..8]) != 124 || BitConverter.ToInt32(header[76..80]) != 32)
			return false;
		height = BitConverter.ToInt32(header[12..16]);
		width = BitConverter.ToInt32(header[16..20]);
		int caps = BitConverter.ToInt32(header[108..112]);
		bool dx10 = header[84] == (byte)'D' && header[85] == (byte)'X' && header[86] == (byte)'1' && header[87] == (byte)'0';
		return (caps & 4096) != 0 && stream.Length > (dx10 ? 148 : 128);
	}

	private static void NormalizeAttributes(string directory)
	{
		foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
			File.SetAttributes(file, FileAttributes.Normal);
	}

	private static void TryDeleteDirectory(string directory)
	{
		try
		{
			if (!Directory.Exists(directory))
				return;
			NormalizeAttributes(directory);
			Directory.Delete(directory, true);
		}
		catch
		{
		}
	}
}
