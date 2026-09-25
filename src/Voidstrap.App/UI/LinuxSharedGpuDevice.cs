using System;
using System.Collections.Generic;
using System.Reflection;

namespace Voidstrap.UI;

public static class LinuxSharedGpuDevice
{
#if CROSSPLAT
	private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	private static readonly string[] DeviceFields =
	[
		"<Wgpu>k__BackingField",
		"<Api>k__BackingField",
		"<Instance>k__BackingField",
		"<Adapter>k__BackingField",
		"<Device>k__BackingField",
		"<Queue>k__BackingField",
		"<MaxSampledTexturesPerShaderStage>k__BackingField",
		"<MaxSamplersPerShaderStage>k__BackingField",
		"<MaxBindGroups>k__BackingField",
		"<SupportsReadOnlyAndReadWriteStorageTextures>k__BackingField",
		"<SupportsTextureFormatsTier1>k__BackingField",
		"<AdapterBackendType>k__BackingField",
		"<AdapterName>k__BackingField",
		"RenderLock",
		"_deviceResourceDomain"
	];

	private static readonly PropertyInfo? SharedContextProperty = typeof(System.Windows.Media.ProGPU.ProGpuWpfWindowOptions)
		.GetProperty("SharedRenderDeviceContext", InstanceMembers);

	private static readonly PropertyInfo? CompositorOptionsProperty = typeof(System.Windows.Media.ProGPU.ProGpuWpfWindowOptions)
		.GetProperty("CompositorOptions", InstanceMembers);

	private static readonly string[] SoftwareAdapters = ["llvmpipe", "lavapipe", "softpipe", "swiftshader"];

	private static bool? _softwareRenderer;

	private static readonly FieldInfo? LifetimeField = typeof(ProGPU.Backend.WgpuContext).GetField("_sharedDeviceLifetime", InstanceMembers);

	private static ProGPU.Backend.WgpuContext? _keeper;

	private static bool _keeperUnavailable;

	private static bool _shareDevice;
#endif

	public static void Install()
	{
#if CROSSPLAT
		if (!OperatingSystem.IsLinux())
			return;

		_shareDevice = Environment.GetEnvironmentVariable("VOIDSTRAP_SHARED_GPU_DEVICE") != "0" && SharedContextProperty is not null && !Voidstrap.Utility.LinuxStartup.SafeMode;
		if (!_shareDevice)
			App.Logger.WriteLine("LinuxSharedGpuDevice", "Every window gets its own GPU device");

		bool registered = System.Windows.Media.ProGPU.WpfPortableWindowActivation.TryRegisterPresentationFrameworkActivation(CreateHost);
		App.Logger.WriteLine("LinuxSharedGpuDevice", registered
			? (_shareDevice ? "New windows reuse one GPU device" : "New windows use the Voidstrap host setup")
			: "The window activation service was unavailable, windows use the default host setup");
#endif
	}

#if CROSSPLAT
	private static System.Windows.Media.ProGPU.ProGpuWpfWindowHost CreateHost(object window)
	{
		try
		{
			return CreateTunedHost(window);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxSharedGpuDevice", "The tuned window host failed, falling back to the default host: " + ex.Message);
			_shareDevice = false;
			return new System.Windows.Media.ProGPU.ProGpuWpfWindowHost(System.Windows.Media.ProGPU.WpfPortableWindowActivation.CreateHostOptions(window));
		}
	}

	private static System.Windows.Media.ProGPU.ProGpuWpfWindowHost CreateTunedHost(object window)
	{
		System.Windows.Media.ProGPU.ProGpuWpfWindowOptions options = System.Windows.Media.ProGPU.WpfPortableWindowActivation.CreateHostOptions(window);
		ApplySoftwareRendererOptions(options);
		if (_shareDevice)
		{
			try
			{
				ProGPU.Backend.WgpuContext? owner = ResolveOwner();
				if (owner is not null)
					SharedContextProperty!.SetValue(options, owner);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("LinuxSharedGpuDevice", "Could not share the GPU device: " + ex.Message);
			}
		}

		System.Windows.Media.ProGPU.ProGpuWpfWindowHost host = new(options);
		LinuxWindowReveal.Prepare(window, host);
		return host;
	}

	private static void ApplySoftwareRendererOptions(System.Windows.Media.ProGPU.ProGpuWpfWindowOptions options)
	{
		if (CompositorOptionsProperty is null || !IsSoftwareRenderer())
			return;

		try
		{
			CompositorOptionsProperty.SetValue(options, ProGPU.Scene.CompositorOptions.Default with { PrimarySampleCount = 1 });
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxSharedGpuDevice", "Could not lighten software rendering: " + ex.Message);
		}
	}

	private static bool IsSoftwareRenderer()
	{
		if (ProGPU.Backend.WgpuContext.TryGetFirstActiveContext(out ProGPU.Backend.WgpuContext? context))
		{
			string adapter = context.AdapterName ?? string.Empty;
			bool software = Array.Exists(SoftwareAdapters, name => adapter.Contains(name, StringComparison.OrdinalIgnoreCase));
			LinuxUiPerformance.Renderer(adapter, software);
			if (_softwareRenderer != software)
			{
				_softwareRenderer = software;
				if (software)
					App.Logger.WriteLine("LinuxSharedGpuDevice", "Rendering on the CPU through " + adapter + ", windows skip multisampling to stay responsive");
			}
			return software;
		}

		if (_softwareRenderer is bool known)
			return known;

		bool guess = Voidstrap.Utility.LinuxStartup.ActiveStage == "software" || !HasRenderNode();
		if (guess && Voidstrap.Utility.LinuxStartup.ActiveStage == "software")
			LinuxUiPerformance.Renderer("software fallback", true);
		if (guess)
			App.Logger.WriteLine("LinuxSharedGpuDevice", "No GPU render node was found, windows skip multisampling to stay responsive");
		return guess;
	}

	private static bool HasRenderNode()
	{
		try
		{
			return System.IO.Directory.Exists("/dev/dri") && System.IO.Directory.GetFiles("/dev/dri", "renderD*").Length > 0;
		}
		catch (Exception)
		{
			return true;
		}
	}

	private static ProGPU.Backend.WgpuContext? ResolveOwner()
	{
		if (_keeper is { IsDeviceLost: false })
			return _keeper;

		if (_keeper is not null)
		{
			ReleaseKeeper(_keeper);
			_keeper = null;
			App.Logger.WriteLine("LinuxSharedGpuDevice", "The shared GPU device was lost, the next window starts a new one");
		}

		if (!ProGPU.Backend.WgpuContext.TryGetFirstActiveContext(out ProGPU.Backend.WgpuContext? owner) || owner.IsDeviceLost)
			return null;

		if (!_keeperUnavailable)
		{
			_keeper = TryCreateKeeper(owner);
			if (_keeper is not null)
				App.Logger.WriteLine("LinuxSharedGpuDevice", "Keeping the " + owner.AdapterName + " device for every window");
			else
				_keeperUnavailable = true;
		}

		return _keeper ?? owner;
	}

	private static ProGPU.Backend.WgpuContext? TryCreateKeeper(ProGPU.Backend.WgpuContext owner)
	{
		try
		{
			object? lifetime = LifetimeField?.GetValue(owner);
			MethodInfo? acquire = lifetime?.GetType().GetMethod("Acquire", InstanceMembers);
			if (lifetime is null || acquire is null)
				return null;

			List<FieldInfo> fields = new(DeviceFields.Length);
			foreach (string name in DeviceFields)
			{
				FieldInfo? field = typeof(ProGPU.Backend.WgpuContext).GetField(name, InstanceMembers);
				if (field is null)
				{
					App.Logger.WriteLine("LinuxSharedGpuDevice", "The renderer changed, no long lived device keeper");
					return null;
				}
				fields.Add(field);
			}

			ProGPU.Backend.WgpuContext keeper = new();
			foreach (FieldInfo field in fields)
				field.SetValue(keeper, field.GetValue(owner));
			LifetimeField!.SetValue(keeper, acquire.Invoke(lifetime, null));
			return keeper;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxSharedGpuDevice", "Could not keep the GPU device alive: " + ex.Message);
			return null;
		}
	}

	private static void ReleaseKeeper(ProGPU.Backend.WgpuContext keeper)
	{
		try
		{
			object? lifetime = LifetimeField?.GetValue(keeper);
			lifetime?.GetType().GetMethod("Release", InstanceMembers)?.Invoke(lifetime, null);
			LifetimeField?.SetValue(keeper, null);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxSharedGpuDevice", "Could not release the old GPU device: " + ex.Message);
		}
	}
#endif
}
