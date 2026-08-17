using System.Globalization;
using System.Resources;

namespace Voidstrap.Extensions;

internal static class ResourceManagerEx
{
	public static string GetStringSafe(this ResourceManager manager, string name)
	{
		return manager.GetStringSafe(name, null);
	}

	public static string GetStringSafe(this ResourceManager manager, string name, CultureInfo? culture)
	{
		return manager.GetString(name, culture) ?? name;
	}
}
