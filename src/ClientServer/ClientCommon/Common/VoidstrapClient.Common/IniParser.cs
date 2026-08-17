using System;
using System.Collections.Generic;
using System.Reflection;

namespace VoidstrapClient.Common;

public static class IniParser
{
	private static string[] _newLineDelimiters = new string[3] { "\r\n", "\r", "\n" };

	private static Dictionary<string, PropertyInfo> GetPropertyInfos(Type type)
	{
		Dictionary<string, PropertyInfo> dictionary = new Dictionary<string, PropertyInfo>();
		PropertyInfo[] properties = type.GetProperties();
		foreach (PropertyInfo propertyInfo in properties)
		{
			if (propertyInfo.PropertyType != typeof(string))
			{
				throw new Exception("Non-string properties are not supported");
			}
			string name = propertyInfo.Name;
			if (dictionary.ContainsKey(name))
			{
				throw new Exception("Duplicate property name in " + type.FullName);
			}
			dictionary[name] = propertyInfo;
		}
		return dictionary;
	}

	public static T Parse<T>(string ini) where T : new()
	{
		T val = new T();
		Dictionary<string, PropertyInfo> propertyInfos = GetPropertyInfos(typeof(T));
		string[] array = ini.Split(_newLineDelimiters, StringSplitOptions.None);
		foreach (string text in array)
		{
			int num = text.IndexOf('=');
			if (num != -1)
			{
				string text2 = text.Substring(0, num);
				string text3 = text;
				int num2 = num + 1;
				string value = text3.Substring(num2, text3.Length - num2);
				if (!string.IsNullOrEmpty(text2) && propertyInfos.ContainsKey(text2))
				{
					propertyInfos[text2].SetValue(val, value);
				}
			}
		}
		return val;
	}

	public static bool TryParse<T>(string ini, out T output) where T : new()
	{
		try
		{
			output = Parse<T>(ini);
			return true;
		}
		catch
		{
			output = new T();
			return false;
		}
	}
}
