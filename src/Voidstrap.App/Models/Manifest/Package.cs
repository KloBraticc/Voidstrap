using System.IO;

namespace Voidstrap.Models.Manifest;

public class Package
{
	public string Name { get; set; } = "";

	public string Signature { get; set; } = "";

	public int PackedSize { get; set; }

	public int Size { get; set; }

	public string DownloadPath => Path.Combine(Paths.Downloads, Signature);

	public override string ToString()
	{
		return "[" + Signature + "] " + Name;
	}
}
