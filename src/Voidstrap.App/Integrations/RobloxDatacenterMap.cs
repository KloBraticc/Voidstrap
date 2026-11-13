using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Voidstrap.Integrations;

public static class RobloxDatacenterMap
{
    private const int MaxCidrEntries = 8192;

    private sealed class CidrEntry
    {
        public uint Network;

        public uint Mask;

        public int PrefixLength;

        public string Cidr = "";

        public RobloxDatacenter Datacenter = null!;
    }

    private static readonly List<SeedCidrEntry> _seedEntries = BuildSeedEntries();

    private static readonly List<CidrEntry> _entries = BuildCidrEntries();

    public static string ResolveCountry(string? city, string? country)
    {
        return Voidstrap.Utility.CountryFlag.Canonical(country);
    }

    public static IReadOnlyList<SeedCidrEntry> AllSeedEntries()
    {
        lock (_entries)
        {
            return _seedEntries.ToArray();
        }
    }

    public static void ReplaceCidrEntries(IEnumerable<SeedCidrEntry> entries)
    {
        if (entries == null)
            return;
        lock (_entries)
        {
            _entries.Clear();
            _seedEntries.Clear();
        }
        AddCidrEntries(entries);
    }

    public static void AddCidrEntries(IEnumerable<SeedCidrEntry> entries)
    {
        if (entries == null)
            return;
        lock (_entries)
        {
            Dictionary<string, CidrEntry> byCidr = _entries.ToDictionary(e => e.Cidr, StringComparer.OrdinalIgnoreCase);
            foreach (SeedCidrEntry entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Cidr))
                    continue;
                try
                {
                    if (
                        string.IsNullOrWhiteSpace(entry.City)
                        || !double.IsFinite(entry.Lat)
                        || !double.IsFinite(entry.Lon)
                        || entry.Lat is < -90.0 or > 90.0
                        || entry.Lon is < -180.0 or > 180.0
                        || entry.Lat == 0.0 && entry.Lon == 0.0
                    )
                        continue;
                    string[] array = entry.Cidr.Split('/');
                    if (
                        array.Length != 2
                        || !IPAddress.TryParse(array[0], out IPAddress? address)
                        || address.AddressFamily != AddressFamily.InterNetwork
                        || !int.TryParse(array[1], out int prefix)
                        || prefix is < 0 or > 32
                    )
                        continue;
                    byte[] bytes = address.GetAddressBytes();
                    uint network = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
                    uint mask = prefix == 0 ? 0u : (uint)(-1 << 32 - prefix);
                    network &= mask;
                    RobloxDatacenter datacenter = new RobloxDatacenter
                    {
                        City = entry.City,
                        Region = entry.Region,
                        Country = ResolveCountry(entry.City, entry.Country),
                        Lat = entry.Lat,
                        Lon = entry.Lon,
                    };
                    if (byCidr.TryGetValue(entry.Cidr, out CidrEntry? existing))
                    {
                        existing.Network = network;
                        existing.Mask = mask;
                        existing.PrefixLength = prefix;
                        existing.Datacenter = datacenter;
                        int seedIndex = _seedEntries.FindIndex(seed =>
                            string.Equals(seed.Cidr, entry.Cidr, StringComparison.OrdinalIgnoreCase)
                        );
                        if (seedIndex >= 0)
                            _seedEntries[seedIndex] = entry;
                        continue;
                    }
                    if (_entries.Count >= MaxCidrEntries)
                        break;
                    CidrEntry added = new CidrEntry
                    {
                        Network = network,
                        Mask = mask,
                        PrefixLength = prefix,
                        Cidr = entry.Cidr,
                        Datacenter = datacenter,
                    };
                    _entries.Add(added);
                    _seedEntries.Add(entry);
                    byCidr.Add(entry.Cidr, added);
                }
                catch { }
            }
            _entries.Sort((left, right) => right.PrefixLength.CompareTo(left.PrefixLength));
        }
    }

    public static RobloxDatacenter? Map(string? ipOrHost)
    {
        if (string.IsNullOrWhiteSpace(ipOrHost))
        {
            return null;
        }
        if (!IPAddress.TryParse(ipOrHost, out IPAddress? address))
        {
            try
            {
                IPAddress[] hostAddresses = Dns.GetHostAddresses(ipOrHost);
                foreach (IPAddress iPAddress in hostAddresses)
                {
                    if (iPAddress.AddressFamily == AddressFamily.InterNetwork)
                    {
                        address = iPAddress;
                        break;
                    }
                }
            }
            catch
            {
                return null;
            }
        }
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }
        byte[] addressBytes = address.GetAddressBytes();
        uint num = (uint)((addressBytes[0] << 24) | (addressBytes[1] << 16) | (addressBytes[2] << 8) | addressBytes[3]);
        lock (_entries)
        {
            foreach (CidrEntry entry in _entries)
            {
                if ((num & entry.Mask) == entry.Network)
                {
                    return entry.Datacenter;
                }
            }
        }
        return null;
    }

    public static IReadOnlyCollection<RobloxDatacenter> AllDatacenters()
    {
        HashSet<string> hashSet = new HashSet<string>();
        List<RobloxDatacenter> list = new List<RobloxDatacenter>();
        lock (_entries)
        {
            foreach (CidrEntry entry in _entries)
            {
                string item = entry.Datacenter.City + "|" + entry.Datacenter.Country;
                if (hashSet.Add(item))
                {
                    list.Add(entry.Datacenter);
                }
            }
        }
        return list;
    }

    private static List<SeedCidrEntry> BuildSeedEntries()
    {
        List<SeedCidrEntry> entries = new List<SeedCidrEntry>();

        AddCitySeeds(entries, "Ashburn", "Virginia", "United States", 39.0438, -77.4874,
            "128.116.102.0/24", "128.116.53.0/24", "107.180.197.0/24", "128.116.56.0/24");
        AddCitySeeds(entries, "Atlanta", "Georgia", "United States", 33.7490, -84.3880,
            "128.116.2.0/24");
        AddCitySeeds(entries, "Chicago", "Illinois", "United States", 41.8781, -87.6298,
            "128.116.48.0/24", "128.116.84.0/24");
        AddCitySeeds(entries, "Dallas", "Texas", "United States", 32.7767, -96.7970,
            "128.116.14.0/24", "128.116.95.0/24");
        AddCitySeeds(entries, "Los Angeles", "California", "United States", 34.0522, -118.2437,
            "128.116.116.0/24", "128.116.1.0/24", "128.116.63.0/24");
        AddCitySeeds(entries, "Miami", "Florida", "United States", 25.7617, -80.1918,
            "128.116.45.0/24", "128.116.127.0/24");
        AddCitySeeds(entries, "New York City", "New York", "United States", 40.7128, -74.0060,
            "128.116.32.0/24", "128.116.30.3/32");
        AddCitySeeds(entries, "San Jose", "California", "United States", 37.3382, -121.8863,
            "128.116.117.0/24", "128.116.67.0/24", "209.206.42.0/24", "209.206.43.0/24");
        AddCitySeeds(entries, "Seattle", "Washington", "United States", 47.6062, -122.3321,
            "128.116.115.0/24");

        AddCitySeeds(entries, "Sydney", "New South Wales", "Australia", -33.8688, 151.2093,
            "128.116.51.0/24");
        AddCitySeeds(entries, "Sao Paulo", "Sao Paulo", "Brazil", -23.5505, -46.6333,
            "128.116.86.0/24");
        AddCitySeeds(entries, "Paris", "Ile-de-France", "France", 48.8566, 2.3522,
            "128.116.13.0/24");
        AddCitySeeds(entries, "Frankfurt", "Hesse", "Germany", 50.1109, 8.6821,
            "128.116.5.0/24", "128.116.44.0/24");
        AddCitySeeds(entries, "Mumbai", "Maharashtra", "India", 19.0760, 72.8777,
            "128.116.47.0/24");
        AddCitySeeds(entries, "Tokyo", "Tokyo", "Japan", 35.6762, 139.6503,
            "128.116.55.0/24", "128.116.120.0/24");
        AddCitySeeds(entries, "Amsterdam", "North Holland", "Netherlands", 52.3676, 4.9041,
            "128.116.21.0/24");
        AddCitySeeds(entries, "Singapore", "Singapore", "Singapore", 1.3521, 103.8198,
            "128.116.46.0/24", "128.116.50.0/24", "128.116.54.0/24", "128.116.97.0/24");
        AddCitySeeds(entries, "London", "England", "United Kingdom", 51.5072, -0.1276,
            "128.116.31.0/24");

        return entries;
    }

    private static void AddCitySeeds(
        List<SeedCidrEntry> entries,
        string city,
        string region,
        string country,
        double lat,
        double lon,
        params string[] cidrs
    )
    {
        foreach (string cidr in cidrs)
        {
            entries.Add(new SeedCidrEntry
            {
                Cidr = cidr,
                City = city,
                Region = region,
                Country = country,
                PingIp = GetSeedPingIp(cidr),
                Lat = lat,
                Lon = lon,
            });
        }
    }

    private static string GetSeedPingIp(string cidr)
    {
        string[] parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out int prefix))
            return "";

        byte[] bytes = address.GetAddressBytes();
        if (prefix == 24 && bytes[3] == 0)
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.3";
        if (prefix == 32 && bytes[3] == 3)
            return address.ToString();
        return "";
    }

    private static List<CidrEntry> BuildCidrEntries()
    {
        List<CidrEntry> list = new List<CidrEntry>();
        foreach (SeedCidrEntry seedEntry in _seedEntries)
        {
            string[] array = seedEntry.Cidr.Split('/');
            IPAddress iPAddress = IPAddress.Parse(array[0]);
            int num = int.Parse(array[1]);
            byte[] addressBytes = iPAddress.GetAddressBytes();
            uint num2 = (uint)((addressBytes[0] << 24) | (addressBytes[1] << 16) | (addressBytes[2] << 8) | addressBytes[3]);
            uint num3 = ((num != 0) ? ((uint)(-1 << 32 - num)) : 0u);
            num2 &= num3;
            list.Add(
                new CidrEntry
                {
                    Network = num2,
                    Mask = num3,
                    PrefixLength = num,
                    Cidr = seedEntry.Cidr,
                    Datacenter = new RobloxDatacenter
                    {
                        City = seedEntry.City,
                        Region = seedEntry.Region,
                        Country = seedEntry.Country,
                        Lat = seedEntry.Lat,
                        Lon = seedEntry.Lon,
                    },
                }
            );
        }
        return list;
    }
}
