using System;
using System.Runtime.InteropServices;

namespace Voidstrap.Integrations
{
    internal static class ViGEmInterop
    {
        public const string LegacyPath = @"\\.\ViGEmBus";

        private static readonly Guid BusInterfaceGuid = new("96E42B22-F5E9-42F8-B043-ED0F932F014F");

        private const uint DIGCF_PRESENT = 0x2;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public UIntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        public static string? GetBusDevicePath()
        {
            var guid = BusInterfaceGuid;
            IntPtr info = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (info == INVALID_HANDLE_VALUE) return null;

            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, 0, ref ifData))
                    return null;

                SetupDiGetDeviceInterfaceDetail(info, ref ifData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) return null;

                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(info, ref ifData, detail, required, out _, IntPtr.Zero))
                        return null;
                    return Marshal.PtrToStringUni(detail + 4);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(info);
            }
        }

        public static bool IsServiceInstalled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus");
                if (key != null) return true;
            }
            catch { }

            try
            {
                foreach (var name in new[] { "ViGEmBus.sys", "ViGEmBus3.sys" })
                {
                    var p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", name);
                    if (System.IO.File.Exists(p)) return true;
                }
            }
            catch { }

            return false;
        }

        public static bool IsInstalled()
        {
            if (!Voidstrap.Utility.Platform.IsWindows) return false;
            if (IsServiceInstalled()) return true;
            return !string.IsNullOrEmpty(GetBusDevicePath());
        }

        public static string ResolveOpenPath()
        {
            var path = GetBusDevicePath();
            App.Logger.WriteLine("ViGEmInterop", path != null
                ? $"Resolved ViGEmBus device path: {path}"
                : $"Could not resolve ViGEmBus device interface (service installed: {IsServiceInstalled()}), falling back to legacy path");
            return path ?? LegacyPath;
        }
    }
}
