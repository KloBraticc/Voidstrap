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
        return new List<SeedCidrEntry>();
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
