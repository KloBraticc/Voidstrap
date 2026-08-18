using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Voidstrap.Utility;

internal static class SafeImaging
{
	private const long MaxCompressedImageBytes = 64L * 1024 * 1024;
	private const long MaxDecodedImageBytes = 64L * 1024 * 1024;

	private static readonly Lazy<System.Net.Http.HttpClient> _http = new Lazy<System.Net.Http.HttpClient>(() => VpnHttpClient.Create(TimeSpan.FromSeconds(15)));

	public static BitmapSource? FromBytes(byte[]? bytes, int decodeWidth = 0)
	{
		if (bytes == null || bytes.Length == 0 || bytes.LongLength > MaxCompressedImageBytes || !HasSafeDimensions(bytes, decodeWidth))
		{
			return null;
		}
		try
		{
			if (!Platform.IsWindows)
			{
				return DecodePortable(bytes, decodeWidth);
			}
			BitmapImage image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
			if (decodeWidth > 0)
			{
				image.DecodePixelWidth = decodeWidth;
			}
			using (MemoryStream compressedStream = new MemoryStream(bytes, writable: false))
			{
				image.StreamSource = compressedStream;
				image.EndInit();
			}
			return Detach(image);
		}
		catch (Exception ex)
		{
			try
			{
				BitmapSource? portable = DecodePortable(bytes, decodeWidth);
				if (portable != null)
				{
					return portable;
				}
			}
			catch (Exception fallbackEx)
			{
				App.Logger?.WriteLine("SafeImaging::FromBytes", "Fallback decode failed: " + fallbackEx.Message.Split('\n')[0]);
			}
			App.Logger?.WriteLine("SafeImaging::FromBytes", "Decode failed: " + ex.Message.Split('\n')[0]);
			return null;
		}
	}

	public static BitmapSource? FromFile(string? path, int decodeWidth = 0)
	{
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			return null;
		}
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
			if (stream.Length <= 0 || stream.Length > MaxCompressedImageBytes)
			{
				return null;
			}
			byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
			stream.ReadExactly(bytes);
			return FromBytes(bytes, decodeWidth);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("SafeImaging::FromFile", "Could not read " + path + ": " + ex.Message.Split('\n')[0]);
			return null;
		}
	}

	private const int MaxCachedUriImages = 64;

	private static readonly ConcurrentDictionary<string, BitmapSource> _uriCache = new(StringComparer.OrdinalIgnoreCase);

	public static void ClearUriCache()
	{
		_uriCache.Clear();
	}

	public static BitmapSource? FromUri(Uri? uri, int decodeWidth = 0)
	{
		if (uri == null)
		{
			return null;
		}
		string key = uri.OriginalString + "|" + decodeWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
		if (_uriCache.TryGetValue(key, out BitmapSource? cached))
		{
			return cached;
		}
		BitmapSource? loaded = LoadUri(uri, decodeWidth);
		if (loaded == null)
		{
			loaded = FromManifest(uri, decodeWidth);
		}
		if (loaded == null)
		{
			App.Logger?.WriteLine("SafeImaging::FromUri", "Could not load " + uri);
			return null;
		}
		if (loaded.IsFrozen)
		{
			if (_uriCache.Count >= MaxCachedUriImages)
			{
				_uriCache.Clear();
			}
			_uriCache[key] = loaded;
		}
		return loaded;
	}

	public static async System.Threading.Tasks.Task<BitmapSource?> FromHttpAsync(string? value, int decodeWidth = 0, System.Threading.CancellationToken token = default)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
		{
			return null;
		}
		using System.Net.Http.HttpRequestMessage request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
		using System.Net.Http.HttpResponseMessage response = await _http.Value.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is long contentLength && contentLength > MaxCompressedImageBytes)
		{
			return null;
		}
		await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		byte[]? bytes = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
		if (bytes == null)
		{
			return null;
		}
		return await System.Threading.Tasks.Task.Run(() => FromBytes(bytes, decodeWidth), token).ConfigureAwait(false);
	}

	private static BitmapSource? FromManifest(Uri uri, int decodeWidth)
	{
		try
		{
			string path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
			path = path.TrimStart('/').Replace('/', '.').Replace('\\', '.');
			if (string.IsNullOrEmpty(path))
			{
				return null;
			}
			Assembly assembly = Assembly.GetExecutingAssembly();
			string target = assembly.GetName().Name + "." + path;
			using Stream? stream = assembly.GetManifestResourceStream(target);
			if (stream == null)
			{
				return null;
			}
			return FromBytes(ReadBounded(stream), decodeWidth);
		}
		catch
		{
			return null;
		}
	}

	private static BitmapSource? LoadUri(Uri uri, int decodeWidth)
	{
		try
		{
			if (uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
			{
				using System.Net.Http.HttpRequestMessage request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
				using System.Net.Http.HttpResponseMessage response = _http.Value.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
				if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is long contentLength && contentLength > MaxCompressedImageBytes)
				{
					return null;
				}
				using Stream responseStream = response.Content.ReadAsStream();
				return FromBytes(ReadBounded(responseStream), decodeWidth);
			}
			if (uri.IsAbsoluteUri && uri.IsFile)
			{
				return FromFile(uri.LocalPath, decodeWidth);
			}
			if (Platform.IsWindows)
			{
				BitmapImage image = new BitmapImage();
				image.BeginInit();
				image.CacheOption = BitmapCacheOption.OnLoad;
				if (decodeWidth > 0)
				{
					image.DecodePixelWidth = decodeWidth;
				}
				image.UriSource = uri;
				image.EndInit();
				return Detach(image);
			}
			StreamResourceInfo? info = Application.GetResourceStream(uri);
			if (info?.Stream == null)
			{
				return null;
			}
			using Stream stream = info.Stream;
			return FromBytes(ReadBounded(stream), decodeWidth);
		}
		catch
		{
			return null;
		}
	}

	public static BitmapSource? FromPack(string packUri, int decodeWidth = 0)
	{
		return Uri.TryCreate(packUri, UriKind.Absolute, out Uri? uri) ? FromUri(uri, decodeWidth) : null;
	}

	public static BitmapSource? FromStream(Stream? stream, int decodeWidth = 0)
	{
		if (stream == null)
		{
			return null;
		}
		try
		{
			return FromBytes(ReadBounded(stream), decodeWidth);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("SafeImaging::FromStream", "Decode failed: " + ex.Message.Split('\n')[0]);
			return null;
		}
	}

	private static byte[]? ReadBounded(Stream stream)
	{
		int capacity = stream.CanSeek ? (int)Math.Min(stream.Length - stream.Position, MaxCompressedImageBytes) : 81920;
		using MemoryStream copy = new MemoryStream(Math.Max(0, capacity));
		byte[] buffer = new byte[81920];
		while (true)
		{
			int read = stream.Read(buffer, 0, buffer.Length);
			if (read == 0)
			{
				return copy.Length == 0 ? null : copy.ToArray();
			}
			if (copy.Length + read > MaxCompressedImageBytes)
			{
				return null;
			}
			copy.Write(buffer, 0, read);
		}
	}

	private static async System.Threading.Tasks.Task<byte[]?> ReadBoundedAsync(Stream stream, System.Threading.CancellationToken token)
	{
		int capacity = stream.CanSeek ? (int)Math.Min(stream.Length - stream.Position, MaxCompressedImageBytes) : 81920;
		using MemoryStream copy = new MemoryStream(Math.Max(0, capacity));
		byte[] buffer = new byte[81920];
		while (true)
		{
			int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
			if (read == 0)
			{
				return copy.Length == 0 ? null : copy.ToArray();
			}
			if (copy.Length + read > MaxCompressedImageBytes)
			{
				return null;
			}
			await copy.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
		}
	}

	private static bool HasSafeDimensions(byte[] bytes, int decodeWidth)
	{
		try
		{
			SixLabors.ImageSharp.ImageInfo? info = SixLabors.ImageSharp.Image.Identify(bytes);
			if (info == null || info.Width <= 0 || info.Height <= 0)
			{
				return false;
			}
			long width = info.Width;
			long height = info.Height;
			if (decodeWidth > 0 && width > decodeWidth)
			{
				height = Math.Max(1, (height * decodeWidth + width - 1) / width);
				width = decodeWidth;
			}
			return width * height * 4 <= MaxDecodedImageBytes;
		}
		catch
		{
			return false;
		}
	}

	private static BitmapSource? DecodePortable(byte[] bytes, int decodeWidth)
	{
		SixLabors.ImageSharp.ImageInfo? info = SixLabors.ImageSharp.Image.Identify(bytes);
		if (info == null || info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height * 4 > MaxDecodedImageBytes)
		{
			return null;
		}
		using SixLabors.ImageSharp.Image<Bgra32> image = SixLabors.ImageSharp.Image.Load<Bgra32>(bytes);
		if (decodeWidth > 0 && image.Width > decodeWidth)
		{
			int height = Math.Max(1, (int)Math.Round(image.Height * (double)decodeWidth / image.Width));
			image.Mutate(context => context.Resize(decodeWidth, height));
		}
		int stride = checked(image.Width * 4);
		byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * image.Height));
		image.CopyPixelDataTo(pixels);
		BitmapSource source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
		return Detach(source);
	}

	public static bool SupportsVisualCapture => Platform.IsWindows;

	internal static BitmapSource Detach(BitmapSource source)
	{
		if (source.CanFreeze && !source.IsFrozen)
		{
			source.Freeze();
		}
		return source;
	}
}
