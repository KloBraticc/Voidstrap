using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Voidstrap.Utility;

internal static class MediaContentFilter
{
	private const string ModelHash = "e9350e576608afe4b57a089ffeb0ebafa1389cdcea4882dd61df28c45f1c24d2";
	private const string ModelRoot = "https://huggingface.co/KanariKanaru/nsfw-image-detection-384-onnx/resolve/8edc47eedf74b30fd379673bc202fe3b754b1538/";
	private static readonly HttpClient Http = VpnHttpClient.Create(TimeSpan.FromSeconds(60));
	private static readonly SemaphoreSlim Gate = new(1, 1);
	private static InferenceSession? _session;
	private static DateTime _retryAfter;

	internal static async Task<BitmapSource?> LoadCheckedIconAsync(string path)
	{
		try
		{
			if (new FileInfo(path).Length > 256 * 1024)
				return null;
			return await LoadCheckedIconAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("MediaContentFilter", "The mod icon was hidden: " + ex.GetType().Name);
			return null;
		}
	}

	internal static Task<BitmapSource?> LoadCheckedIconAsync(byte[] bytes) => Task.Run(async () =>
	{
		try
		{
			if (bytes.Length > 256 * 1024)
				return null;
			using MemoryStream stream = new(bytes, false);
			BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
			BitmapFrame[] frames = decoder.Frames.ToArray();
			if (frames.Length == 0 || frames.Length > 16 || frames.Any(frame => frame.PixelWidth > 256 || frame.PixelHeight > 256))
				return null;
			foreach (BitmapFrame frame in frames)
				frame.Freeze();
			foreach (BitmapFrame frame in frames)
				if (!await IsAllowedAsync(frame).ConfigureAwait(false))
					return null;
			return (BitmapSource?)frames.OrderByDescending(frame => frame.PixelWidth).First();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("MediaContentFilter", "The mod icon was hidden: " + ex.GetType().Name);
			return null;
		}
	});

	internal static Task<bool> IsAllowedAsync(BitmapSource source)
	{
		BitmapSource converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
		byte[] pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
		converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
		return IsAllowedAsync(new Raster(converted.PixelWidth, converted.PixelHeight, pixels));
	}

	internal static async Task<bool> IsAllowedAsync(Raster raster)
	{
		await Gate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_session == null)
			{
				if (DateTime.UtcNow < _retryAfter)
					return false;
				await InitializeAsync().ConfigureAwait(false);
			}
			if (!CheckRaster(raster))
				return false;
			if (raster.Width > 384 || raster.Height > 384)
				for (int y = 0; y < raster.Height; y += 384)
					for (int x = 0; x < raster.Width; x += 384)
						if (!CheckRaster(raster.Crop(x, y, Math.Min(384, raster.Width - x), Math.Min(384, raster.Height - y))))
							return false;
			return true;
		}
		catch (Exception ex)
		{
			_retryAfter = DateTime.UtcNow.AddMinutes(1);
			App.Logger?.WriteLine("MediaContentFilter", "Media was hidden because local content checking failed: " + ex.GetType().Name);
			return false;
		}
		finally
		{
			Gate.Release();
		}
	}

	private static bool Check(Raster raster, byte background)
	{
		DenseTensor<float> input = new([1, 3, 384, 384]);
		for (int y = 0; y < 384; y++)
			for (int x = 0; x < 384; x++)
			{
				int offset = (y * 384 + x) * 4;
				float alpha = raster.Pixels[offset + 3] / 255f;
				for (int channel = 0; channel < 3; channel++)
					input[0, channel, y, x] = (raster.Pixels[offset + 2 - channel] * alpha + background * (1 - alpha)) / 127.5f - 1;
			}
		using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output = _session!.Run([NamedOnnxValue.CreateFromTensor("input", input)]);
		float[] logits = output.Single(value => value.Name == "logits").AsEnumerable<float>().ToArray();
		return logits.Length == 2 && float.IsFinite(logits[0]) && float.IsFinite(logits[1]) &&
			1 / (1 + Math.Exp((double)logits[1] - logits[0])) < 0.25;
	}

	private static bool CheckRaster(Raster raster)
	{
		Raster resized = raster.Resize(384, 384);
		bool transparent = false;
		for (int i = 3; i < resized.Pixels.Length; i += 4)
			transparent |= resized.Pixels[i] != 255;
		return Check(resized, 255) && (!transparent || Check(resized, 0));
	}

	private static async Task InitializeAsync()
	{
		string folder = Path.Combine(Paths.Cache, "MediaFilter");
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, "content.onnx");
		if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024 || !MatchesHash(await File.ReadAllBytesAsync(path).ConfigureAwait(false), ModelHash))
			await DownloadAsync("model.onnx", path, ModelHash).ConfigureAwait(false);
		await EnsureNoticeAsync(folder, "LICENSE-APACHE-2.0.txt", "b4123048cb9e96f48ec9ab9e0ec5fe34b6c5a2eb648e88550cc7a630763134c3").ConfigureAwait(false);
		await EnsureNoticeAsync(folder, "MODEL_NOTICES.md", "265c7254d699ce034c215fb1c10d0e45a2b7fd5572b4f0786cff8af3bc1fc908").ConfigureAwait(false);
		using SessionOptions options = new() { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
		_session = new InferenceSession(path, options);
	}

	private static async Task EnsureNoticeAsync(string folder, string name, string hash)
	{
		string path = Path.Combine(folder, name);
		if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024 || !MatchesHash(await File.ReadAllBytesAsync(path).ConfigureAwait(false), hash))
			await DownloadAsync(name, path, hash).ConfigureAwait(false);
	}

	internal static bool MatchesHash(byte[] bytes, string expected) => Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase);

	private static async Task DownloadAsync(string name, string path, string hash)
	{
		using HttpResponseMessage response = await Http.GetAsync(ModelRoot + name, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		using MemoryStream bytes = new();
		byte[] buffer = new byte[81920];
		int count;
		using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
		while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
		{
			if (bytes.Length + count > 32 * 1024 * 1024)
				throw new InvalidDataException("The content checker download exceeds its size limit");
			bytes.Write(buffer, 0, count);
		}
		byte[] data = bytes.ToArray();
		if (!MatchesHash(data, hash))
			throw new InvalidDataException("The content checker checksum does not match");
		string temporary = path + ".tmp";
		try
		{
			await File.WriteAllBytesAsync(temporary, data).ConfigureAwait(false);
			File.Move(temporary, path, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}
}
