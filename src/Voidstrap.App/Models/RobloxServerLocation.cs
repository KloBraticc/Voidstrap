using System;
using System.Collections.Generic;
using System.Text;

namespace Voidstrap.Models;

public class RobloxServerLocation
{
	public string Key { get; set; } = "";

	public string City { get; set; } = "";

	public string Region { get; set; } = "";

	public ServerRegion Group { get; set; }

	public ServerStatusTier Status { get; set; }

	public string[] MatchTokens { get; set; } = Array.Empty<string>();

	public double Lat { get; set; }

	public double Lon { get; set; }

	public string DisplayGroup => Group switch
	{
		ServerRegion.UnitedStates => "United States", 
		ServerRegion.Canada => "Canada", 
		ServerRegion.Europe => "Europe", 
		ServerRegion.Asia => "Asia", 
		ServerRegion.Oceania => "Oceania", 
		ServerRegion.SouthAmerica => "South America", 
		ServerRegion.MiddleEast => "Middle East", 
		ServerRegion.Africa => "Africa", 
		ServerRegion.Experimental => "Experimental", 
		_ => "Other", 
	};

	public string DisplayStatus => Status switch
	{
		ServerStatusTier.Current => "Current", 
		ServerStatusTier.Rare => "Rare", 
		ServerStatusTier.Experimental => "Experimental", 
		_ => "", 
	};

	public double DistanceKmTo(double lat, double lon)
	{
		double num = ToRad(lat - Lat);
		double num2 = ToRad(lon - Lon);
		double num3 = Math.Sin(num / 2.0) * Math.Sin(num / 2.0) + Math.Cos(ToRad(Lat)) * Math.Cos(ToRad(lat)) * Math.Sin(num2 / 2.0) * Math.Sin(num2 / 2.0);
		double num4 = 2.0 * Math.Atan2(Math.Sqrt(num3), Math.Sqrt(1.0 - num3));
		return 6371.0 * num4;
	}

	private static double ToRad(double d)
	{
		return d * Math.PI / 180.0;
	}

	public bool Matches(string serverLocation, bool smart = false)
	{
		if (string.IsNullOrWhiteSpace(serverLocation) || MatchTokens.Length == 0)
		{
			return false;
		}
		string text = serverLocation.ToLowerInvariant();
		string[] matchTokens = MatchTokens;
		foreach (string text2 in matchTokens)
		{
			if (!string.IsNullOrEmpty(text2) && text.Contains(text2.ToLowerInvariant()))
			{
				return true;
			}
		}
		if (!smart)
		{
			return false;
		}
		string text3 = Normalize(serverLocation);
		if (string.IsNullOrEmpty(text3))
		{
			return false;
		}
		matchTokens = MatchTokens;
		for (int i = 0; i < matchTokens.Length; i++)
		{
			string text4 = Normalize(matchTokens[i]);
			if (string.IsNullOrEmpty(text4) || text4.Length < 3)
			{
				continue;
			}
			if (text3.Contains(text4))
			{
				return true;
			}
			foreach (string item in SplitWords(text3))
			{
				if (item.Length >= 3)
				{
					if (item.Contains(text4) || text4.Contains(item))
					{
						return true;
					}
					int num = LevenshteinDistance(item, text4);
					int num2 = Math.Max(1, text4.Length / 4);
					if (num <= num2)
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	private static string Normalize(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(s.Length);
		string text = s.ToLowerInvariant();
		foreach (char c in text)
		{
			char c2 = c switch
			{
				'0' => 'o', 
				'1' => 'i', 
				'3' => 'e', 
				'4' => 'a', 
				'5' => 's', 
				'7' => 't', 
				'8' => 'b', 
				'@' => 'a', 
				'$' => 's', 
				_ => c, 
			};
			if (c2 >= 'a' && c2 <= 'z')
			{
				stringBuilder.Append(c2);
			}
			else
			{
				stringBuilder.Append(' ');
			}
		}
		string text2 = stringBuilder.ToString();
		while (text2.Contains("  "))
		{
			text2 = text2.Replace("  ", " ");
		}
		return text2.Trim();
	}

	private static IEnumerable<string> SplitWords(string s)
	{
		return s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
	}

	private static int LevenshteinDistance(string a, string b)
	{
		if (a == b)
		{
			return 0;
		}
		if (a.Length == 0)
		{
			return b.Length;
		}
		if (b.Length == 0)
		{
			return a.Length;
		}
		int[] array = new int[b.Length + 1];
		int[] array2 = new int[b.Length + 1];
		for (int i = 0; i <= b.Length; i++)
		{
			array[i] = i;
		}
		for (int j = 1; j <= a.Length; j++)
		{
			array2[0] = j;
			for (int k = 1; k <= b.Length; k++)
			{
				int num = ((a[j - 1] != b[k - 1]) ? 1 : 0);
				array2[k] = Math.Min(Math.Min(array2[k - 1] + 1, array[k] + 1), array[k - 1] + num);
			}
			Array.Copy(array2, array, b.Length + 1);
		}
		return array[b.Length];
	}
}
