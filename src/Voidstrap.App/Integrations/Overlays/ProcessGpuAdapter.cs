using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Voidstrap.Integrations.Overlays
{
    internal static partial class ProcessGpuAdapter
    {
        private const uint PdhMoreData = 0x800007D2;
        private const uint DetailWizard = 400;

        public static long? FindRenderAdapterLuid(int processId)
        {
            if (processId <= 0)
                return null;
            try
            {
                uint ignored = 0;
                _ = PdhEnumObjects(null, null, null, ref ignored, DetailWizard, 1);
                uint counterLength = 0;
                uint instanceLength = 0;
                uint status = PdhEnumObjectItems(null, null, "GPU Engine", null, ref counterLength, null, ref instanceLength, DetailWizard, 0);
                if (status != PdhMoreData || instanceLength == 0)
                    return null;
                char[] counters = new char[Math.Max(counterLength, 1)];
                char[] instances = new char[instanceLength];
                status = PdhEnumObjectItems(null, null, "GPU Engine", counters, ref counterLength, instances, ref instanceLength, DetailWizard, 0);
                if (status != 0)
                    return null;
                string prefix = "pid_" + processId.ToString(CultureInfo.InvariantCulture) + "_luid_";
                Dictionary<long, int> engines = new();
                foreach (string name in new string(instances, 0, (int)Math.Min(instanceLength, (uint)instances.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string[] parts = name.Substring(prefix.Length).Split('_');
                    if (parts.Length < 2
                        || !uint.TryParse(parts[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high)
                        || !uint.TryParse(parts[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low))
                        continue;
                    long luid = ((long)high << 32) | low;
                    engines[luid] = engines.GetValueOrDefault(luid) + 1;
                }
                long? best = null;
                int bestCount = 0;
                foreach (KeyValuePair<long, int> pair in engines)
                {
                    if (pair.Value > bestCount)
                    {
                        best = pair.Key;
                        bestCount = pair.Value;
                    }
                }
                return best;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        [LibraryImport("pdh.dll", EntryPoint = "PdhEnumObjectsW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhEnumObjects(string? dataSource, string? machineName, char[]? objectList, ref uint bufferLength, uint detailLevel, int refresh);

        [LibraryImport("pdh.dll", EntryPoint = "PdhEnumObjectItemsW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhEnumObjectItems(string? dataSource, string? machineName, string objectName, [Out] char[]? counterList, ref uint counterListLength, [Out] char[]? instanceList, ref uint instanceListLength, uint detailLevel, uint flags);
    }
}
