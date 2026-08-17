using System;
using System.Globalization;
using System.Windows.Data;
using Voidstrap.Extensions;
using Voidstrap.Models.Attributes;
using Voidstrap.Resources;

namespace Voidstrap.UI.Converters;

internal class EnumNameConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (!(value is Enum obj))
		{
			return value?.ToString() ?? string.Empty;
		}
		string text = obj.ToString();
		Type type = obj.GetType();
		string fullName = type.FullName;
		System.Reflection.MemberInfo[] members = type.GetMember(text);
		if (members.Length == 0)
		{
			return text;
		}
		object[] customAttributes = members[0].GetCustomAttributes(typeof(EnumNameAttribute), inherit: false);
		if (customAttributes.Length != 0)
		{
			EnumNameAttribute enumNameAttribute = (EnumNameAttribute)customAttributes[0];
			if (enumNameAttribute != null)
			{
				if (enumNameAttribute.StaticName != null)
				{
					return enumNameAttribute.StaticName;
				}
				if (enumNameAttribute.FromTranslation != null)
				{
					return Strings.ResourceManager.GetStringSafe(enumNameAttribute.FromTranslation);
				}
			}
		}
		return Strings.ResourceManager.GetStringSafe($"{fullName.Substring(fullName.IndexOf('.', StringComparison.Ordinal) + 1)}.{text}");
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return Binding.DoNothing;
	}
}
