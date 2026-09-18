using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Voidstrap.Utility
{
    public sealed record VirtualMachineDetection(
        bool IsVirtualMachine,
        string Hypervisor,
        bool HasHardwareGpu,
        string GpuSummary);

    public static class VirtualMachineProfile
    {
        private const uint VendorVmware = 0x15AD;
        private const uint VendorBochs = 0x1234;
        private const uint VendorVirtio = 0x1AF4;
        private const uint VendorQemu = 0x1B36;
        private const uint VendorHyperV = 0x1414;

        private static readonly Lazy<VirtualMachineDetection> DetectionValue =
            new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

        private static int _reported;

        public static VirtualMachineDetection Current => DetectionValue.Value;

        public static bool ShouldForceSafeGraphics => Current.IsVirtualMachine && !Current.HasHardwareGpu;

        public static IReadOnlyDictionary<string, object> SafeFastFlags { get; } = new Dictionary<string, object>
        {
            ["FFlagDebugGraphicsPreferOpenGL"] = true,
            ["FFlagDebugGraphicsPreferVulkan"] = false,
            ["DFIntDebugFRMQualityLevelOverride"] = 1,
            ["FIntDebugForceMSAASamples"] = 0,
            ["DFFlagDebugPauseVoxelizer"] = true,
            ["FIntFRMMaxGrassDistance"] = 0,
            ["FIntFRMMinGrassDistance"] = 0,
            ["DFFlagTextureQualityOverrideEnabled"] = true,
            ["DFIntTextureQualityOverride"] = 0
        };

        public static void ReportOnce()
        {
            if (Interlocked.Exchange(ref _reported, 1) != 0)
            {
                return;
            }

            VirtualMachineDetection detection = Current;
            if (!detection.IsVirtualMachine)
            {
                App.Logger?.WriteLine("VirtualMachineProfile", "Running on physical hardware, no virtual machine profile applied");
                return;
            }

            App.Logger?.WriteLine(
                "VirtualMachineProfile",
                "Detected " + detection.Hypervisor + ", hardware GPU present: " + detection.HasHardwareGpu + ", adapters: " + detection.GpuSummary);

            if (detection.HasHardwareGpu)
            {
                return;
            }

            App.Logger?.WriteLine(
                "VirtualMachineProfile",
                "This virtual machine exposes no hardware GPU, so Roblox has no Vulkan device. Voidstrap is forcing the OpenGL renderer and the lowest cost graphics settings. Expect very low frame rates. Running Voidstrap on the host operating system, or on a machine with GPU passthrough, is the only way to get real performance.");
        }

        private static VirtualMachineDetection Detect()
        {
            string hypervisor = DetectHypervisor();
            bool hasHardwareGpu = HasHardwareGpu();
            bool isVirtual = !string.IsNullOrEmpty(hypervisor) || HasVirtualAdapter();

            if (isVirtual && string.IsNullOrEmpty(hypervisor))
            {
                hypervisor = "an unidentified hypervisor";
            }

            return new VirtualMachineDetection(
                isVirtual,
                isVirtual ? hypervisor : "none",
                hasHardwareGpu,
                GpuInventory.Summary);
        }

        private static string DetectHypervisor()
        {
            string vendor = ReadDmi("sys_vendor");
            string product = ReadDmi("product_name");
            string combined = (vendor + " " + product).ToLowerInvariant();

            if (combined.Contains("virtualbox") || combined.Contains("innotek"))
            {
                return "VirtualBox";
            }

            if (combined.Contains("vmware"))
            {
                return "VMware";
            }

            if (combined.Contains("qemu") || combined.Contains("kvm"))
            {
                return "QEMU or KVM";
            }

            if (combined.Contains("xen"))
            {
                return "Xen";
            }

            if (combined.Contains("parallels"))
            {
                return "Parallels";
            }

            if (combined.Contains("microsoft") && combined.Contains("virtual"))
            {
                return "Hyper V";
            }

            if (combined.Contains("bochs") || combined.Contains("bhyve"))
            {
                return "Bochs or bhyve";
            }

            return HasHypervisorCpuFlag() ? "an unidentified hypervisor" : "";
        }

        private static string ReadDmi(string name)
        {
            try
            {
                string path = "/sys/class/dmi/id/" + name;
                return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("VirtualMachineProfile", "Could not read " + name + ": " + ex.Message);
                return "";
            }
        }

        private static bool HasHypervisorCpuFlag()
        {
            try
            {
                if (!File.Exists("/proc/cpuinfo"))
                {
                    return false;
                }

                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("flags", StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Contains(" hypervisor", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("VirtualMachineProfile", "Could not read the processor flags: " + ex.Message);
            }

            return false;
        }

        private static bool HasHardwareGpu()
        {
            try
            {
                return GpuInventory.Adapters.Any(adapter => adapter.IsNvidia || adapter.IsAmd || adapter.IsIntel);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("VirtualMachineProfile", "Could not inspect the graphics adapters: " + ex.Message);
                return true;
            }
        }

        private static bool HasVirtualAdapter()
        {
            try
            {
                return GpuInventory.Adapters.Any(adapter =>
                    adapter.VendorId == VendorVmware
                    || adapter.VendorId == VendorBochs
                    || adapter.VendorId == VendorVirtio
                    || adapter.VendorId == VendorQemu
                    || adapter.VendorId == VendorHyperV);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("VirtualMachineProfile", "Could not inspect the graphics adapters: " + ex.Message);
                return false;
            }
        }
    }
}
