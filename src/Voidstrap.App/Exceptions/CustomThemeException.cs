using System;
using System.Globalization;
using System.Text;
using Voidstrap.Extensions;
using Voidstrap.Resources;

namespace Voidstrap.Exceptions;

internal class CustomThemeException : Exception
{
	public string EnglishMessage { get; }

	public CustomThemeException(string translationString)
		: base(Strings.ResourceManager.GetStringSafe(translationString))
	{
		EnglishMessage = Strings.ResourceManager.GetStringSafe(translationString, new CultureInfo("en-GB"));
	}

	public CustomThemeException(Exception innerException, string translationString)
		: base(Strings.ResourceManager.GetStringSafe(translationString), innerException)
	{
		EnglishMessage = Strings.ResourceManager.GetStringSafe(translationString, new CultureInfo("en-GB"));
	}

	public CustomThemeException(string translationString, params object?[] args)
		: base(string.Format(Strings.ResourceManager.GetStringSafe(translationString), args))
	{
		EnglishMessage = string.Format(Strings.ResourceManager.GetStringSafe(translationString, new CultureInfo("en-GB")), args);
	}

	public CustomThemeException(Exception innerException, string translationString, params object?[] args)
		: base(string.Format(Strings.ResourceManager.GetStringSafe(translationString), args), innerException)
	{
		EnglishMessage = string.Format(Strings.ResourceManager.GetStringSafe(translationString, new CultureInfo("en-GB")), args);
	}

	public override string ToString()
	{
		StringBuilder stringBuilder = new(GetType().ToString());
		if (!string.IsNullOrEmpty(Message))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new(2, 1, stringBuilder2);
			handler.AppendLiteral(": ");
			handler.AppendFormatted(Message);
			stringBuilder3.Append(ref handler);
		}
		if (!string.IsNullOrEmpty(EnglishMessage) && Message != EnglishMessage)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new(3, 1, stringBuilder2);
			handler.AppendLiteral(" (");
			handler.AppendFormatted(EnglishMessage);
			handler.AppendLiteral(")");
			stringBuilder4.Append(ref handler);
		}
		if (base.InnerException != null)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new(13, 1, stringBuilder2);
			handler.AppendLiteral("\r\n ---> ");
			handler.AppendFormatted(base.InnerException);
			handler.AppendLiteral("\r\n   ");
			stringBuilder5.Append(ref handler);
		}
		if (StackTrace != null)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new(2, 1, stringBuilder2);
			handler.AppendLiteral("\r\n");
			handler.AppendFormatted(StackTrace);
			stringBuilder6.Append(ref handler);
		}
		return stringBuilder.ToString();
	}
}
