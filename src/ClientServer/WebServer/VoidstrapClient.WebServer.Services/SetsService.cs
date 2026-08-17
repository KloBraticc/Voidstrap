using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoidstrapClient.Common;
using VoidstrapClient.Common.Models;

namespace VoidstrapClient.WebServer.Services;

internal class SetsService
{
	private const int MaxEntries = 10000;
	private const long MaxXmlBytes = 8L * 1024 * 1024;
	private const string EmptyList = "<List></List>";

	private readonly Dictionary<int, string> _sets = new Dictionary<int, string>();

	private readonly Dictionary<int, string> _users = new Dictionary<int, string>();

	public static SetsService Instance { get; }

	public SetsService()
	{
		string path = Path.Combine(PathHelper.Data, "sets");
		ParseData(_sets, Path.Combine(path, "set"));
		ParseData(_users, Path.Combine(path, "user"));
	}

	public string GetSet(int setId)
	{
		return _sets.TryGetValue(setId, out string? path) ? ReadXml(path) : EmptyList;
	}

	public string GetUserSets(int userId)
	{
		return _users.TryGetValue(userId, out string? path) ? ReadXml(path) : EmptyList;
	}

	public string GetBaseSet()
	{
		return ReadXml(Path.Combine(PathHelper.Data, "sets", "base.xml"));
	}

	private void ParseData(Dictionary<int, string> map, string directory)
	{
		if (!Directory.Exists(directory))
		{
			return;
		}
		Dictionary<int, (string, ClientYear)> dictionary = new Dictionary<int, (string, ClientYear)>();
		foreach (string text in Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly).Take(MaxEntries))
		{
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text);
			int result2;
			if (fileNameWithoutExtension.Contains('_'))
			{
				string[] array = fileNameWithoutExtension.Split('_');
				if (array.Length >= 2 && int.TryParse(array[0], out var result) && result > 0)
				{
					string year = array[1];
					ClientYear clientYear = new ClientYear(year);
					if (clientYear <= Config.Instance.Client.ClientYear &&
						(!dictionary.TryGetValue(result, out (string, ClientYear) existing) || existing.Item2 < clientYear))
					{
						dictionary[result] = (text, clientYear);
					}
				}
			}
			else if (int.TryParse(fileNameWithoutExtension, out result2) && result2 > 0)
			{
				dictionary.TryAdd(result2, (text, ClientYear.Blank));
			}
		}
		foreach (KeyValuePair<int, (string, ClientYear)> item in dictionary)
		{
			map[item.Key] = item.Value.Item1;
		}
	}

	private static string ReadXml(string path)
	{
		try
		{
			return Config.ReadTextFile(path, MaxXmlBytes);
		}
		catch
		{
			return EmptyList;
		}
	}

	public void Test()
	{
	}

	static SetsService()
	{
		Instance = new SetsService();
	}
}
