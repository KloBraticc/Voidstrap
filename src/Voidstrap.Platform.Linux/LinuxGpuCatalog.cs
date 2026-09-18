using System.Globalization;
using System.Text.RegularExpressions;

namespace Voidstrap.Platform.Linux;

public sealed record LinuxGpuCard(
	string Node,
	string Address,
	string VendorId,
	string ProductId,
	string VendorName,
	string ProductName,
	bool IsPrimary)
{
	public string DisplayName
	{
		get
		{
			string label = string.IsNullOrWhiteSpace(VendorName)
				? ProductName
				: (string.IsNullOrWhiteSpace(ProductName) ? VendorName : VendorName + " " + ProductName);

			if (string.IsNullOrWhiteSpace(label))
				label = Node;

			return label + " (" + Address + ")";
		}
	}
}

public static partial class LinuxGpuCatalog
{
	private const string DrmRoot = "/sys/class/drm";

	private static readonly string[] PciIdsPaths =
	[
		"/usr/share/hwdata/pci.ids",
		"/usr/share/misc/pci.ids",
		"/usr/share/pci.ids"
	];


	private static readonly object Gate = new();
	private static List<LinuxGpuCard>? _cards;

	public static IReadOnlyList<LinuxGpuCard> Cards
	{
		get
		{
			lock (Gate)
			{
				return _cards ??= Enumerate();
			}
		}
	}

	public static void Invalidate()
	{
		lock (Gate)
		{
			_cards = null;
		}
	}

	private static List<LinuxGpuCard> Enumerate()
	{
		List<LinuxGpuCard> cards = [];
		if (!OperatingSystem.IsLinux() || !Directory.Exists(DrmRoot))
			return cards;

		List<(string Node, string Address, string VendorId, string ProductId, bool IsPrimary)> raw = [];
		try
		{
			foreach (string path in Directory.EnumerateDirectories(DrmRoot).OrderBy(static entry => entry, StringComparer.Ordinal))
			{
				string node = Path.GetFileName(path);
				if (!CardExpression.IsMatch(node))
					continue;

				string device = Path.Combine(path, "device");
				string? address = ResolveAddress(device);
				if (address is null)
					continue;

				string vendor = ReadHexId(Path.Combine(device, "vendor"));
				string product = ReadHexId(Path.Combine(device, "device"));
				if (vendor.Length == 0)
					continue;

				if (raw.Any(entry => string.Equals(entry.Address, address, StringComparison.OrdinalIgnoreCase)))
					continue;

				bool primary = string.Equals(ReadText(Path.Combine(device, "boot_vga")), "1", StringComparison.Ordinal);
				raw.Add((node, address, vendor, product, primary));
			}
		}
		catch (Exception)
		{
			return cards;
		}

		if (raw.Count == 0)
			return cards;

		Dictionary<string, (string Vendor, string Product)> names = ResolveNames(raw.Select(static entry => (entry.VendorId, entry.ProductId)));

		foreach ((string node, string address, string vendorId, string productId, bool primary) in raw)
		{
			names.TryGetValue(vendorId + ":" + productId, out (string Vendor, string Product) resolved);
			cards.Add(new LinuxGpuCard(
				node,
				address,
				vendorId,
				productId,
				string.IsNullOrWhiteSpace(resolved.Vendor) ? FallbackVendorName(vendorId) : resolved.Vendor,
				string.IsNullOrWhiteSpace(resolved.Product) ? "0x" + productId : resolved.Product,
				primary));
		}

		return cards;
	}

	private static string? ResolveAddress(string devicePath)
	{
		try
		{
			if (!Directory.Exists(devicePath) && !File.Exists(devicePath))
				return null;

			string resolved = Path.GetFullPath(new DirectoryInfo(devicePath).LinkTarget is string link
				? (Path.IsPathRooted(link) ? link : Path.Combine(Path.GetDirectoryName(devicePath) ?? string.Empty, link))
				: devicePath);

			string name = Path.GetFileName(resolved);
			return string.IsNullOrWhiteSpace(name) ? null : name;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static string ReadHexId(string file)
	{
		string value = ReadText(file);
		if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			value = value[2..];
		return value.Length == 0 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
			? string.Empty
			: value.ToLowerInvariant();
	}

	private static string ReadText(string file)
	{
		try
		{
			return File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static string FallbackVendorName(string vendorId)
	{
		return vendorId switch
		{
			"10de" => "NVIDIA",
			"1002" or "1022" => "AMD",
			"8086" => "Intel",
			"1af4" or "1b36" => "VirtIO",
			"15ad" => "VMware",
			"1414" => "Microsoft",
			_ => "0x" + vendorId
		};
	}

	private static Dictionary<string, (string Vendor, string Product)> ResolveNames(IEnumerable<(string VendorId, string ProductId)> wanted)
	{
		Dictionary<string, (string, string)> results = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> vendors = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> pairs = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string vendorId, string productId) in wanted)
		{
			vendors.Add(vendorId);
			pairs.Add(vendorId + ":" + productId);
		}

		string? database = PciIdsPaths.FirstOrDefault(File.Exists);
		if (database is null)
			return results;

		try
		{
			string currentVendor = string.Empty;
			string currentVendorName = string.Empty;
			foreach (string line in File.ReadLines(database))
			{
				if (line.Length == 0 || line[0] == '#')
					continue;

				if (line[0] != '\t')
				{
					int split = line.IndexOf("  ", StringComparison.Ordinal);
					if (split != 4)
					{
						currentVendor = string.Empty;
						continue;
					}

					string id = line[..4];
					currentVendor = vendors.Contains(id) ? id : string.Empty;
					currentVendorName = currentVendor.Length == 0 ? string.Empty : ShortenVendor(line[(split + 2)..].Trim());
					continue;
				}

				if (currentVendor.Length == 0 || line.Length < 2 || line[1] == '\t')
					continue;

				string body = line[1..];
				int deviceSplit = body.IndexOf("  ", StringComparison.Ordinal);
				if (deviceSplit != 4)
					continue;

				string key = currentVendor + ":" + body[..4];
				if (!pairs.Contains(key) || results.ContainsKey(key))
					continue;

				results[key] = (currentVendorName, ShortenProduct(body[(deviceSplit + 2)..].Trim()));
				if (results.Count == pairs.Count)
					break;
			}
		}
		catch (Exception)
		{
		}

		return results;
	}

	private static string ShortenVendor(string vendor)
	{
		foreach (string suffix in new[] { " Corporation", " Corp.", " Corp", " Inc.", " Inc", ", Inc.", " Technologies, Inc.", " Technologies Inc" })
		{
			if (vendor.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return vendor[..^suffix.Length].Trim();
		}

		return vendor;
	}

	private static string ShortenProduct(string product)
	{
		int open = product.IndexOf('[', StringComparison.Ordinal);
		int close = product.LastIndexOf(']');
		if (open >= 0 && close > open + 1)
			return product[(open + 1)..close].Trim();
		return product;
	}

    [GeneratedRegex("^card[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CardExpression { get; }
}
