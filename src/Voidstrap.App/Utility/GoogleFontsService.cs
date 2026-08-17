using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public sealed class GoogleFontOption
{
	public string Family { get; set; } = string.Empty;

	public string Category { get; set; } = string.Empty;

	public string File { get; set; } = string.Empty;

	public string DisplayName => string.IsNullOrWhiteSpace(Category) ? Family : Family + ", " + Category;
}

internal static partial class GoogleFontsService
{
	private const int MaximumCatalogBytes = 8388608;

	internal const int MaximumFontBytes = 33554432;

	private const long MaximumFontCacheBytes = 268435456;

	private const string CatalogUrl = "https://voidstrapp.pages.dev/api/google-fonts";

	private static readonly HttpClient Http = CreateHttpClient();

	private static readonly string CatalogPath = Path.Combine(Paths.Cache, "GoogleFonts", "catalog.json");

	private static readonly string FontCacheDirectory = Path.Combine(Paths.Cache, "GoogleFonts", "Files");

	private static readonly SemaphoreSlim CacheMaintenanceGate = new(1, 1);

	private static readonly SemaphoreSlim FontDownloadGate = new(1, 1);

	private static readonly string[] StarterFamilies =
	[
		"Bebas Neue", "Dancing Script", "Fira Sans", "Inter", "JetBrains Mono", "Lato", "Merriweather", "Montserrat", "Noto Sans", "Nunito", "Open Sans", "Oswald", "Pacifico", "Playfair Display", "Poppins", "Raleway", "Roboto", "Rubik", "Source Sans 3", "Ubuntu"
	];

	private sealed class CatalogEnvelope
	{
		public List<GoogleFontOption> Fonts { get; set; } = [];
	}

	private static HttpClient CreateHttpClient()
	{
		HttpClient client = VpnHttpClient.Create(TimeSpan.FromSeconds(20));
		client.DefaultRequestHeaders.UserAgent.ParseAdd("VoidstrapApp");
		return client;
	}

	public static async Task<IReadOnlyList<GoogleFontOption>> LoadCatalogAsync(bool force, CancellationToken token)
	{
		if (!force && TryLoadCache(out IReadOnlyList<GoogleFontOption> fresh) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CatalogPath) < TimeSpan.FromHours(24))
			return fresh;
		try
		{
			using HttpResponseMessage response = await Http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, token);
			response.EnsureSuccessStatusCode();
			byte[] data = await ReadLimitedAsync(response.Content, MaximumCatalogBytes, token);
			CatalogEnvelope? envelope = JsonSerializer.Deserialize<CatalogEnvelope>(data, JsonOptions.Tolerant);
			List<GoogleFontOption> fonts = Normalize(envelope?.Fonts);
			if (fonts.Count == 0)
				throw new InvalidDataException("The font catalog was empty");
			await Task.Run(() => JsonFile.SerializeAtomic(CatalogPath, new CatalogEnvelope { Fonts = fonts }), token);
			return fonts;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("GoogleFontsService::LoadCatalog", "Font catalog unavailable: " + ex.Message);
			if (TryLoadCache(out IReadOnlyList<GoogleFontOption> cached))
				return cached;
			return StarterFamilies.Select(family => new GoogleFontOption { Family = family, Category = "starter" }).ToArray();
		}
	}

	public static async Task<string> DownloadAsync(GoogleFontOption font, CancellationToken token)
	{
		if (string.IsNullOrWhiteSpace(font.Family))
			throw new InvalidDataException("Select a font first");
		await FontDownloadGate.WaitAsync(token);
		try
		{
			return await DownloadCoreAsync(font, token);
		}
		finally
		{
			FontDownloadGate.Release();
		}
	}

	private static async Task<string> DownloadCoreAsync(GoogleFontOption font, CancellationToken token)
	{
		Directory.CreateDirectory(FontCacheDirectory);
		string fileUrl = ValidateFileUrl(font.File) ?? await ResolveFileUrlAsync(font.Family, token);
		string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(font.Family + "|" + fileUrl))).Substring(0, 20);
		string destination = Path.Combine(FontCacheDirectory, id + ".ttf");
		if (IsValidFont(destination))
			return destination;
		string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
		try
		{
			using HttpResponseMessage response = await Http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, token);
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength is long size && (size <= 0 || size > MaximumFontBytes))
				throw new InvalidDataException("The font file size is invalid");
			await using Stream source = await response.Content.ReadAsStreamAsync(token);
			await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				byte[] buffer = new byte[65536];
				long total = 0;
				while (true)
				{
					int read = await source.ReadAsync(buffer.AsMemory(), token);
					if (read == 0)
						break;
					total += read;
					if (total > MaximumFontBytes)
						throw new InvalidDataException("The font file is too large");
					await output.WriteAsync(buffer.AsMemory(0, read), token);
				}
				await output.FlushAsync(token);
			}
			if (!IsValidFont(temporary))
				throw new InvalidDataException("The downloaded file is not a supported font");
			File.Move(temporary, destination, true);
			await MaintainCacheAsync(destination, token).ConfigureAwait(false);
			return destination;
		}
		finally
		{
			try
			{
				File.Delete(temporary);
			}
			catch
			{
			}
		}
	}

	public static async Task<string> ImportLocalAsync(string sourcePath, CancellationToken token)
	{
		FileInfo sourceInfo = new(sourcePath);
		if (!sourceInfo.Exists || sourceInfo.Length < 12 || sourceInfo.Length > MaximumFontBytes)
			throw new InvalidDataException("The font file size is invalid");
		string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
		if (extension is not ".ttf" and not ".otf")
			throw new InvalidDataException("The font file type is not supported");
		string localDirectory = Path.Combine(FontCacheDirectory, "Local");
		Directory.CreateDirectory(localDirectory);
		string temporary = Path.Combine(localDirectory, Guid.NewGuid().ToString("N") + ".importing");
		try
		{
			using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			await using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
			await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				byte[] buffer = new byte[65536];
				long total = 0;
				while (true)
				{
					int read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
					if (read == 0)
						break;
					total += read;
					if (total > MaximumFontBytes)
						throw new InvalidDataException("The font file is too large");
					hash.AppendData(buffer, 0, read);
					await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
				}
				await output.FlushAsync(token).ConfigureAwait(false);
			}
			if (!IsValidFont(temporary))
				throw new InvalidDataException("The font file is not supported");
			string id = Convert.ToHexString(hash.GetHashAndReset());
			string destination = Path.Combine(localDirectory, id + extension);
			if (IsValidFont(destination))
				return destination;
			File.Move(temporary, destination, true);
			await MaintainCacheAsync(destination, token).ConfigureAwait(false);
			return destination;
		}
		finally
		{
			try
			{
				File.Delete(temporary);
			}
			catch
			{
			}
		}
	}

	private static async Task MaintainCacheAsync(string protectedPath, CancellationToken token)
	{
		await CacheMaintenanceGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			await Task.Run(() =>
			{
				try
				{
					if (!Directory.Exists(FontCacheDirectory))
						return;
					FileInfo[] files = Directory.EnumerateFiles(FontCacheDirectory, "*", SearchOption.AllDirectories)
						.Where(path => path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
						.Select(path => new FileInfo(path))
						.Where(file => file.Exists)
						.OrderBy(file => file.LastWriteTimeUtc)
						.ToArray();
					long total = files.Sum(file => file.Length);
					foreach (FileInfo file in files)
					{
						if (total <= MaximumFontCacheBytes)
							break;
						if (file.FullName.Equals(protectedPath, StringComparison.OrdinalIgnoreCase))
							continue;
						try
						{
							long length = file.Length;
							file.Delete();
							total -= length;
						}
						catch
						{
						}
					}
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("GoogleFontsService::MaintainCache", "Font cache maintenance failed: " + ex.Message);
				}
			}, token).ConfigureAwait(false);
		}
		finally
		{
			CacheMaintenanceGate.Release();
		}
	}

	private static bool TryLoadCache(out IReadOnlyList<GoogleFontOption> fonts)
	{
		fonts = [];
		try
		{
			CatalogEnvelope envelope = JsonFile.Deserialize<CatalogEnvelope>(CatalogPath, JsonOptions.Tolerant, MaximumCatalogBytes);
			List<GoogleFontOption> normalized = Normalize(envelope.Fonts);
			if (normalized.Count == 0)
				return false;
			fonts = normalized;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static List<GoogleFontOption> Normalize(IEnumerable<GoogleFontOption>? values)
	{
		return (values ?? [])
			.Where(value => !string.IsNullOrWhiteSpace(value.Family) && value.Family.Length <= 128)
			.GroupBy(value => value.Family.Trim(), StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.OrderBy(value => value.Family, StringComparer.CurrentCultureIgnoreCase)
			.Take(3000)
			.ToList();
	}

	private static async Task<string> ResolveFileUrlAsync(string family, CancellationToken token)
	{
		string url = "https://fonts.googleapis.com/css2?family=" + Uri.EscapeDataString(family).Replace("%20", "+", StringComparison.OrdinalIgnoreCase) + "&display=swap";
		using HttpRequestMessage request = new(HttpMethod.Get, url);
		request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
		using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
		response.EnsureSuccessStatusCode();
		byte[] data = await ReadLimitedAsync(response.Content, 262144, token);
		string css = Encoding.UTF8.GetString(data);
		foreach (Match match in FontUrlPattern().Matches(css))
		{
			string? validated = ValidateFileUrl(match.Value);
			if (validated != null)
				return validated;
		}
		throw new InvalidDataException("No compatible font file was found");
	}

	private static string? ValidateFileUrl(string value)
	{
		if (!Uri.TryCreate(value.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out Uri? uri))
			return null;
		if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("fonts.gstatic.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
			return null;
		return uri.AbsoluteUri;
	}

	private static bool IsValidFont(string path)
	{
		try
		{
			FileInfo file = new(path);
			if (!file.Exists || file.Length < 4 || file.Length > MaximumFontBytes)
				return false;
			Span<byte> header = stackalloc byte[4];
			using FileStream stream = File.OpenRead(path);
			if (stream.Read(header) != 4)
				return false;
			return (header.SequenceEqual(new byte[] { 0, 1, 0, 0 }) || header.SequenceEqual("OTTO"u8)) && TryReadFamilyName(path, out _);
		}
		catch
		{
			return false;
		}
	}

	internal static bool TryReadFamilyName(string path, out string familyName)
	{
		familyName = string.Empty;
		try
		{
			FileInfo file = new(path);
			if (!file.Exists || file.Length < 28 || file.Length > MaximumFontBytes)
				return false;

			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			Span<byte> header = stackalloc byte[12];
			stream.ReadExactly(header);
			bool supported = header[..4].SequenceEqual(new byte[] { 0, 1, 0, 0 }) || header[..4].SequenceEqual("OTTO"u8);
			if (!supported)
				return false;

			int tableCount = BinaryPrimitives.ReadUInt16BigEndian(header[4..6]);
			if (tableCount <= 0 || tableCount > 256)
				return false;

			uint nameOffset = 0;
			uint nameLength = 0;
			Span<byte> tableRecord = stackalloc byte[16];
			for (int index = 0; index < tableCount; index++)
			{
				stream.ReadExactly(tableRecord);
				if (!tableRecord[..4].SequenceEqual("name"u8))
					continue;
				nameOffset = BinaryPrimitives.ReadUInt32BigEndian(tableRecord[8..12]);
				nameLength = BinaryPrimitives.ReadUInt32BigEndian(tableRecord[12..16]);
				break;
			}

			if (nameLength < 6 || nameLength > 4194304 || (ulong)nameOffset + nameLength > (ulong)file.Length)
				return false;

			byte[] nameData = new byte[(int)nameLength];
			stream.Position = nameOffset;
			stream.ReadExactly(nameData);
			ReadOnlySpan<byte> names = nameData;
			int nameCount = BinaryPrimitives.ReadUInt16BigEndian(names[2..4]);
			int stringOffset = BinaryPrimitives.ReadUInt16BigEndian(names[4..6]);
			if (nameCount <= 0 || nameCount > 4096 || 6L + nameCount * 12L > names.Length || stringOffset < 6 || stringOffset > names.Length)
				return false;

			string? bestName = null;
			int bestScore = int.MinValue;
			for (int index = 0; index < nameCount; index++)
			{
				int recordOffset = 6 + index * 12;
				ReadOnlySpan<byte> record = names.Slice(recordOffset, 12);
				int platformId = BinaryPrimitives.ReadUInt16BigEndian(record[0..2]);
				int encodingId = BinaryPrimitives.ReadUInt16BigEndian(record[2..4]);
				int languageId = BinaryPrimitives.ReadUInt16BigEndian(record[4..6]);
				int nameId = BinaryPrimitives.ReadUInt16BigEndian(record[6..8]);
				int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..10]);
				int offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..12]);
				if (nameId is not 1 and not 16 || length <= 0 || length > 2048)
					continue;
				int start = stringOffset + offset;
				if (start < stringOffset || start > names.Length || length > names.Length - start)
					continue;

				string value = platformId is 0 or 3
					? Encoding.BigEndianUnicode.GetString(names.Slice(start, length))
					: Encoding.Latin1.GetString(names.Slice(start, length));
				value = string.Join(" ", value.Replace('\0', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
				if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Contains('#') || value.Any(char.IsControl))
					continue;

				int score = nameId == 16 ? 100 : 50;
				score += platformId == 3 ? 40 : platformId == 0 ? 30 : platformId == 1 ? 20 : 0;
				if (languageId == 1033)
					score += 10;
				if (encodingId is 1 or 10)
					score += 2;
				if (score > bestScore)
				{
					bestScore = score;
					bestName = value;
				}
			}

			if (string.IsNullOrWhiteSpace(bestName))
				return false;
			familyName = bestName;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximumBytes, CancellationToken token)
	{
		if (content.Headers.ContentLength is long size && (size <= 0 || size > maximumBytes))
			throw new InvalidDataException("The response size is invalid");
		await using Stream source = await content.ReadAsStreamAsync(token);
		using MemoryStream destination = new();
		byte[] buffer = new byte[65536];
		while (true)
		{
			int read = await source.ReadAsync(buffer.AsMemory(), token);
			if (read == 0)
				break;
			if (destination.Length + read > maximumBytes)
				throw new InvalidDataException("The response is too large");
			destination.Write(buffer, 0, read);
		}
		if (destination.Length == 0)
			throw new InvalidDataException("The response is empty");
		return destination.ToArray();
	}

	[GeneratedRegex(@"https://fonts\.gstatic\.com/[^)'\""\s]+\.ttf", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex FontUrlPattern();
}
