using Voidstrap.Enums;

namespace Voidstrap.Extensions;

internal static class CustomThemeTemplateEx
{
	public static string GetFileName(this CustomThemeTemplate template)
	{
		return $"CustomBootstrapperTemplate_{template}.xml";
	}
}
