using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed record SettingsCatalogEntry(
	string Id,
	string SourcePage,
	string Title,
	string Description,
	IReadOnlyCollection<string> Aliases,
	string TargetName,
	IReadOnlyCollection<string> Containers,
	string VisibilityExpression = "");

public static class SettingsCatalogImporter
{
	private const long MaximumCatalogBytes = 4L * 1024 * 1024;
	private const int MaximumPageFiles = 256;
	private const int MaximumCatalogEntries = 25_000;
	private static readonly Regex PageExpression = new("new\\s+SearchCatalogOption\\(typeof\\((?<page>[A-Za-z0-9_]+)\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex StringExpression = new("\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static string DefaultCatalogPath => Path.Combine(AppContext.BaseDirectory, "Catalog", "SearchCatalog.cs");

	public static async Task<OperationResult<IReadOnlyCollection<SettingsCatalogEntry>>> LoadAsync(string? catalogPath = null, CancellationToken cancellationToken = default)
	{
		string path = string.IsNullOrWhiteSpace(catalogPath) ? DefaultCatalogPath : catalogPath;
		try
		{
			List<SettingsCatalogEntry> entries = new();
			if (File.Exists(path))
			{
				try
				{
					if (new FileInfo(path).Length <= MaximumCatalogBytes)
					{
						string source = await ReadTextAsync(path, cancellationToken);
						entries.AddRange(Parse(source));
					}
				}
				catch (IOException)
				{
				}
				catch (InvalidDataException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
			if (string.IsNullOrWhiteSpace(catalogPath))
			{
				entries.AddRange(await LoadPageEntriesAsync(Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "Pages"), cancellationToken));
			}
			if (entries.Count == 0)
			{
				return OperationResult<IReadOnlyCollection<SettingsCatalogEntry>>.Fail("SettingsCatalogMissing", "The settings catalog is unavailable");
			}

			IReadOnlyCollection<SettingsCatalogEntry> uniqueEntries = entries
				.Take(MaximumCatalogEntries)
				.GroupBy(static entry => $"{entry.SourcePage}\u001F{entry.TargetName}\u001F{entry.Title}\u001F{string.Join("\u001E", entry.Containers)}", StringComparer.Ordinal)
				.Select(static group =>
				{
					SettingsCatalogEntry first = group.First();
					return new SettingsCatalogEntry(
						first.Id,
						first.SourcePage,
						first.Title,
						first.Description,
						group.SelectMany(static entry => entry.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
						first.TargetName,
						first.Containers);
				})
				.ToArray();
			return OperationResult<IReadOnlyCollection<SettingsCatalogEntry>>.Success(uniqueEntries);
		}
		catch (OperationCanceledException)
		{
			return OperationResult<IReadOnlyCollection<SettingsCatalogEntry>>.Fail("OperationCanceled", "Settings catalog loading was canceled");
		}
		catch (IOException exception)
		{
			return OperationResult<IReadOnlyCollection<SettingsCatalogEntry>>.Fail("SettingsCatalogReadFailed", exception.Message);
		}
		catch (InvalidDataException exception)
		{
			return OperationResult<IReadOnlyCollection<SettingsCatalogEntry>>.Fail("SettingsCatalogReadFailed", exception.Message);
		}
		catch (UnauthorizedAccessException exception)
		{
			return OperationResult<IReadOnlyCollection<SettingsCatalogEntry>>.Fail("SettingsCatalogAccessDenied", exception.Message, CapabilityState.RequiresPermission);
		}
	}

	private static async Task<IReadOnlyCollection<SettingsCatalogEntry>> LoadPageEntriesAsync(string directoryPath, CancellationToken cancellationToken)
	{
		if (!Directory.Exists(directoryPath))
		{
			return Array.Empty<SettingsCatalogEntry>();
		}

		List<SettingsCatalogEntry> entries = new();
		foreach (string path in Directory.EnumerateFiles(directoryPath, "*.xaml", SearchOption.TopDirectoryOnly).Take(MaximumPageFiles))
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				if (new FileInfo(path).Length > MaximumCatalogBytes)
					continue;
				string source = await ReadTextAsync(path, cancellationToken);
				entries.AddRange(ParsePage(source, Path.GetFileNameWithoutExtension(path)));
				if (entries.Count >= MaximumCatalogEntries)
					break;
			}
			catch (XmlException)
			{
			}
			catch (IOException)
			{
			}
			catch (InvalidDataException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		return entries;
	}

	public static IReadOnlyCollection<SettingsCatalogEntry> Parse(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return Array.Empty<SettingsCatalogEntry>();
		}

		List<SettingsCatalogEntry> entries = new();
		Dictionary<string, int> pageIndices = new(StringComparer.Ordinal);
		using StringReader reader = new(source);
		while (reader.ReadLine() is string line)
		{
			if (entries.Count >= MaximumCatalogEntries)
				break;
			Match pageMatch = PageExpression.Match(line);
			if (!pageMatch.Success)
			{
				continue;
			}

			MatchCollection stringMatches = StringExpression.Matches(line);
			if (stringMatches.Count < 2)
			{
				continue;
			}

			string page = pageMatch.Groups["page"].Value;
			int index = pageIndices.TryGetValue(page, out int currentIndex) ? currentIndex + 1 : 1;
			pageIndices[page] = index;
			string title = DecodeString(stringMatches[0].Groups["value"].Value);
			string description = DecodeString(stringMatches[1].Groups["value"].Value);
			string[] aliases = stringMatches
				.Skip(2)
				.Select(static match => DecodeString(match.Groups["value"].Value))
				.Where(static alias => !string.IsNullOrWhiteSpace(alias))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

			entries.Add(new SettingsCatalogEntry($"Settings.{page}.{index}", page, title, description, aliases, string.Empty, Array.Empty<string>()));
		}

		return entries;
	}

	public static IReadOnlyCollection<SettingsCatalogEntry> ParsePage(string source, string sourcePage)
	{
		if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourcePage))
		{
			return Array.Empty<SettingsCatalogEntry>();
		}

		try
		{
			XmlReaderSettings settings = new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = MaximumCatalogBytes,
				MaxCharactersFromEntities = 0
			};
			using StringReader sourceReader = new StringReader(source);
			using XmlReader xmlReader = XmlReader.Create(sourceReader, settings);
			XDocument document = XDocument.Load(xmlReader, LoadOptions.None);
			string page = GetPageName(document.Root, sourcePage);
			List<SettingsCatalogEntry> entries = new();
			int index = 0;
			foreach (XElement element in document.Descendants().Where(static element => string.Equals(element.Name.LocalName, "OptionControl", StringComparison.Ordinal)))
			{
				if (entries.Count >= MaximumCatalogEntries)
					break;
				if (element.Ancestors().Any(static ancestor => string.Equals(ancestor.Name.LocalName, "DataTemplate", StringComparison.Ordinal)))
				{
					continue;
				}

				index++;
				string title = GetAttributeValue(element, "Header");
				string description = GetAttributeValue(element, "Description");
				string targetName = GetAttributeValue(element, "Name");
				string visibility = GetAttributeValue(element, "Visibility");
				if (string.IsNullOrEmpty(visibility))
				{
					visibility = element.Ancestors()
						.Select(static ancestor => GetAttributeValue(ancestor, "Visibility"))
						.FirstOrDefault(static value => value.Contains("PlatformFeatureVisibility", StringComparison.Ordinal)) ?? string.Empty;
				}
				string[] containers = element.Ancestors()
					.Where(static ancestor => string.Equals(ancestor.Name.LocalName, "TabItem", StringComparison.Ordinal) || string.Equals(ancestor.Name.LocalName, "Expander", StringComparison.Ordinal))
					.Reverse()
					.Select(GetContainerLabel)
					.Where(static value => !string.IsNullOrWhiteSpace(value))
					.ToArray();
				List<string> aliases = new();
				foreach (XAttribute attribute in element.DescendantsAndSelf().Attributes())
				{
					if (string.Equals(attribute.Name.LocalName, "Name", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attribute.Value))
					{
						aliases.Add(attribute.Value);
					}

					aliases.AddRange(ExtractBindingProperties(attribute.Value));
				}

				entries.Add(new SettingsCatalogEntry(
					$"SettingsXaml.{page}.{index}",
					page,
					title,
					description,
					aliases.Where(static alias => !string.IsNullOrWhiteSpace(alias)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
					targetName,
					containers,
					visibility));
			}

			return entries;
		}
		catch (XmlException)
		{
			return Array.Empty<SettingsCatalogEntry>();
		}
	}

	private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaximumCatalogBytes)
			throw new InvalidDataException("Settings catalog file size is invalid");
		byte[] data = new byte[checked((int)stream.Length)];
		int offset = 0;
		while (offset < data.Length)
		{
			int read = await stream.ReadAsync(data.AsMemory(offset), cancellationToken);
			if (read == 0)
				throw new EndOfStreamException();
			offset += read;
		}
		if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
			throw new InvalidDataException("Settings catalog file changed while it was being read");
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, System.Text.Encoding.UTF8, true);
		return await reader.ReadToEndAsync(cancellationToken);
	}

	public static string GetDisplayText(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}

		if (!value.Contains("Strings.", StringComparison.Ordinal))
		{
			return value;
		}

		int marker = value.LastIndexOf("Strings.", StringComparison.Ordinal);
		int start = marker + "Strings.".Length;
		int end = value.IndexOfAny(['}', ',', ' '], start);
		string key = end < 0 ? value[start..] : value[start..end];
		string[] parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			return value;
		}

		return string.Join(" ", parts.Where(static part => !string.Equals(part, "Menu", StringComparison.OrdinalIgnoreCase) && !string.Equals(part, "Title", StringComparison.OrdinalIgnoreCase) && !string.Equals(part, "Description", StringComparison.OrdinalIgnoreCase)));
	}

	private static string DecodeString(string value)
	{
		try
		{
			return Regex.Unescape(value);
		}
		catch (ArgumentException)
		{
			return value;
		}
	}

	private static string GetAttributeValue(XElement element, string localName)
	{
		return element.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))?.Value ?? string.Empty;
	}

	private static string GetContainerLabel(XElement element)
	{
		string header = GetAttributeValue(element, "Header");
		if (!string.IsNullOrWhiteSpace(header))
		{
			return header;
		}
		return element.Elements()
			.FirstOrDefault(static child => string.Equals(child.Name.LocalName, "TabItem.Header", StringComparison.Ordinal) || string.Equals(child.Name.LocalName, "Header", StringComparison.Ordinal))?
			.DescendantsAndSelf()
			.SelectMany(static child => child.Attributes())
			.Where(static attribute => string.Equals(attribute.Name.LocalName, "Text", StringComparison.Ordinal) || string.Equals(attribute.Name.LocalName, "Content", StringComparison.Ordinal))
			.Select(static attribute => attribute.Value)
			.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
	}

	private static string GetPageName(XElement? root, string fallback)
	{
		string typeName = root is null ? string.Empty : GetAttributeValue(root, "Class");
		if (string.IsNullOrWhiteSpace(typeName))
		{
			return fallback;
		}

		int separator = typeName.LastIndexOf(".", StringComparison.Ordinal);
		return separator >= 0 && separator < typeName.Length - 1 ? typeName[(separator + 1)..] : typeName;
	}

	private static IReadOnlyCollection<string> ExtractBindingProperties(string value)
	{
		if (!value.Contains("Binding", StringComparison.Ordinal))
		{
			return Array.Empty<string>();
		}

		List<string> values = new();
		Match pathMatch = Regex.Match(value, "Path=(?<path>[A-Za-z_][A-Za-z0-9_.]+)", RegexOptions.CultureInvariant);
		if (!pathMatch.Success)
		{
			pathMatch = Regex.Match(value, "\\{Binding\\s+(?<path>[A-Za-z_][A-Za-z0-9_.]+)", RegexOptions.CultureInvariant);
		}
		if (pathMatch.Success)
		{
			string path = pathMatch.Groups["path"].Value;
			values.Add(path);
		}

		return values;
	}
}
