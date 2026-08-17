using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Voidstrap;

internal static class Resource
{
	private static readonly Assembly assembly = Assembly.GetExecutingAssembly();

	private static readonly string[] resourceNames = assembly.GetManifestResourceNames();

	public static Stream GetStream(string name)
	{
		string name2 = resourceNames.Single((string str) => str.EndsWith(name));
		return assembly.GetManifestResourceStream(name2) ?? throw new InvalidDataException("Embedded resource could not be opened: " + name);
	}

	public static byte[] Get(string name)
	{
		using Stream stream = GetStream(name);
		using MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}

	public static string GetString(string name)
	{
		return Encoding.UTF8.GetString(Get(name));
	}
}
