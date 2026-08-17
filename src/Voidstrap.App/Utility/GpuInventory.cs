using System;
using System.Collections.Generic;
using System.Linq;

namespace Voidstrap.Utility;

public sealed class GpuAdapterInfo
{
    public GpuAdapterInfo(string name, uint vendorId, string source)
    {
        Name = name ?? string.Empty;
        VendorId = vendorId;
        Source = source ?? string.Empty;
    }

    public string Name { get; }

    public uint VendorId { get; }

    public string Source { get; }

    public bool IsNvidia => VendorId == GpuInventory.VendorNvidia;

    public bool IsAmd => VendorId == GpuInventory.VendorAmd || VendorId == GpuInventory.VendorAti;

    public bool IsIntel => VendorId == GpuInventory.VendorIntel;

    public override string ToString()
    {
        return Name + " (vendor 0x" + VendorId.ToString("X4") + ", via " + Source + ")";
    }
}

public static class GpuInventory
{
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorAmd = 0x1002;
    public const uint VendorAti = 0x1022;
    public const uint VendorIntel = 0x8086;
    private const uint VendorMicrosoft = 0x1414;

    private static readonly object Gate = new object();
    private static List<GpuAdapterInfo>? _adapters;

    public static IReadOnlyList<GpuAdapterInfo> Adapters
    {
        get
        {
            lock (Gate)
            {
                return _adapters ??= Enumerate();
            }
        }
    }

    public static bool HasNvidia => Adapters.Any(adapter => adapter.IsNvidia);

    public static string Summary
    {
        get
        {
            IReadOnlyList<GpuAdapterInfo> adapters = Adapters;
            if (adapters.Count == 0)
                return "no graphics adapters were detected";
            return string.Join(", ", adapters.Select(adapter => adapter.Name));
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _adapters = null;
        }
    }

    private static List<GpuAdapterInfo> Enumerate()
    {
        List<GpuAdapterInfo> found = new List<GpuAdapterInfo>();
        if (!Platform.IsWindows)
        {
            AddLinuxAdapters(found);
            LogResult(found);
            return found;
        }

        AddDxgiAdapters(found);
        if (found.Count == 0)
            AddWmiAdapters(found);
        LogResult(found);
        return found;
    }

    private static void AddDxgiAdapters(List<GpuAdapterInfo> found)
    {
        try
        {
            if (Vortice.DXGI.DXGI.CreateDXGIFactory1(out Vortice.DXGI.IDXGIFactory1? factory).Failure || factory is null)
                return;
            try
            {
                for (uint index = 0; index < 32u; index++)
                {
                    if (factory.EnumAdapters1(index, out Vortice.DXGI.IDXGIAdapter1? adapter).Failure || adapter is null)
                        break;
                    try
                    {
                        Vortice.DXGI.AdapterDescription1 description = adapter.Description1;
                        Add(found, description.Description, (uint)description.VendorId, "DXGI");
                    }
                    finally
                    {
                        adapter.Dispose();
                    }
                }
            }
            finally
            {
                factory.Dispose();
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("GpuInventory", "DXGI enumeration failed: " + ex.Message);
        }
    }

    private static void AddWmiAdapters(List<GpuAdapterInfo> found)
    {
        try
        {
            using System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (System.Management.ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    string name = item["Name"] as string ?? string.Empty;
                    string pnp = item["PNPDeviceID"] as string ?? string.Empty;
                    Add(found, name, VendorFromPnpId(pnp, name), "WMI");
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("GpuInventory", "WMI enumeration failed: " + ex.Message);
        }
    }

    private static void AddLinuxAdapters(List<GpuAdapterInfo> found)
    {
        try
        {
            foreach (string path in System.IO.Directory.EnumerateDirectories("/sys/class/drm"))
            {
                string vendorFile = System.IO.Path.Combine(path, "device", "vendor");
                if (!System.IO.File.Exists(vendorFile))
                    continue;
                string raw = System.IO.File.ReadAllText(vendorFile).Trim();
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    raw = raw.Substring(2);
                if (!uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint vendor))
                    continue;
                Add(found, System.IO.Path.GetFileName(path), vendor, "sysfs");
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("GpuInventory", "sysfs enumeration failed: " + ex.Message);
        }
    }

    private static uint VendorFromPnpId(string pnpId, string name)
    {
        int marker = pnpId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0 && marker + 8 <= pnpId.Length
            && uint.TryParse(pnpId.AsSpan(marker + 4, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint vendor))
        {
            return vendor;
        }
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase) || name.Contains("RTX", StringComparison.OrdinalIgnoreCase))
        {
            return VendorNvidia;
        }
        if (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            return VendorAmd;
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return VendorIntel;
        return 0u;
    }

    private static void Add(List<GpuAdapterInfo> found, string name, uint vendorId, string source)
    {
        string clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
            return;
        if (vendorId == VendorMicrosoft
            || clean.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
            || clean.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        foreach (GpuAdapterInfo existing in found)
        {
            if (existing.VendorId == vendorId && string.Equals(existing.Name, clean, StringComparison.OrdinalIgnoreCase))
                return;
        }
        found.Add(new GpuAdapterInfo(clean, vendorId, source));
    }

    private static void LogResult(List<GpuAdapterInfo> found)
    {
        try
        {
            App.Logger?.WriteLine("GpuInventory", found.Count == 0
                ? "No graphics adapters detected"
                : "Detected " + found.Count + " adapter(s): " + string.Join(" | ", found.Select(adapter => adapter.ToString()))
                    + " -> NVIDIA present: " + found.Any(adapter => adapter.IsNvidia));
        }
        catch
        {
        }
    }
}
