using System;
using VoidstrapClient.Common.Enums;

namespace VoidstrapClient.Common.Models;

public class ClientYear : IComparable<ClientYear>
{
	public static ClientYear Blank { get; } = new ClientYear(0, YearQuarter.Early);

	public int Year { get; set; }

	public YearQuarter Era { get; set; }

	public bool IsBlank
	{
		get
		{
			if (Year == 0)
			{
				return Era == YearQuarter.Early;
			}
			return false;
		}
	}

	public ClientYear(int year, YearQuarter era)
	{
		Year = year;
		Era = era;
	}

	public ClientYear(string year)
	{
		ParseYearString(year);
	}

	private static YearQuarter GetYearQuarter(char end)
	{
		switch (char.ToLowerInvariant(end))
		{
		case 'e':
			return YearQuarter.Early;
		case 'm':
			return YearQuarter.Mid;
		case 'l':
			return YearQuarter.Late;
		default:
			Logger.Instance.Warn($"Unknown short year quarter: {end}");
			return YearQuarter.Early;
		}
	}

	private void ParseYearString(string year)
	{
		int num = year.IndexOf('-');
		if (num != -1)
		{
			year = year.Substring(0, num);
		}
		if (year.Length == 5 && int.TryParse(year.Substring(0, 4), out var result))
		{
			Year = result;
			char end = year[4];
			Era = GetYearQuarter(end);
		}
		else
		{
			Logger.Instance.Warn("Failed to parse year: " + year);
			Year = 0;
			Era = YearQuarter.Early;
		}
	}

	public char GetShortYearQuarter()
	{
		return Era switch
		{
			YearQuarter.Early => 'E', 
			YearQuarter.Mid => 'M', 
			YearQuarter.Late => 'L', 
			_ => 'E', 
		};
	}

	public override string ToString()
	{
		return $"{Year}{GetShortYearQuarter()}";
	}

	public override bool Equals(object? obj)
	{
		if (!(obj is ClientYear clientYear))
		{
			return false;
		}
		if (Year == clientYear.Year)
		{
			return Era == clientYear.Era;
		}
		return false;
	}

	public override int GetHashCode()
	{
		return HashCode.Combine(Year, Era);
	}

	public int CompareTo(ClientYear? other)
	{
		if ((object)other == null)
		{
			return 1;
		}
		if (Year > other.Year)
		{
			return 1;
		}
		if (Year < other.Year)
		{
			return -1;
		}
		if (Era > other.Era)
		{
			return 1;
		}
		if (Era < other.Era)
		{
			return -1;
		}
		return 0;
	}

	public static bool operator <(ClientYear a, ClientYear b)
	{
		return a.CompareTo(b) == -1;
	}

	public static bool operator >(ClientYear a, ClientYear b)
	{
		return a.CompareTo(b) == 1;
	}

	public static bool operator <=(ClientYear a, ClientYear b)
	{
		if (!a.Equals(b))
		{
			return a.CompareTo(b) == -1;
		}
		return true;
	}

	public static bool operator >=(ClientYear a, ClientYear b)
	{
		if (!a.Equals(b))
		{
			return a.CompareTo(b) == 1;
		}
		return true;
	}

	public static bool operator ==(ClientYear a, ClientYear b)
	{
		return a.Equals(b);
	}

	public static bool operator !=(ClientYear a, ClientYear b)
	{
		return !a.Equals(b);
	}
}
