using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations;

public sealed record RobloxAudioOutput(string Id, string Name);

public static partial class RobloxAudioRouting
{
	private const string LOG_IDENT = "RobloxAudioRouting";
	private const int LinuxRefreshMs = 3000;
	private const int OwnershipRetryMs = 2000;

	private static readonly string[] RobloxProcessNames = ["RobloxPlayerBeta"];

	private static readonly LinuxEqualizerBands[] EqualizerBands =
	[
		new(0f, 0f, 0f),
		new(6f, 0f, 0f),
		new(-3f, 5f, 2f),
		new(-4f, 3f, 1f),
		new(0f, 0f, 5f)
	];

	private static readonly object Gate = new();
	private static CancellationTokenSource? _cts;
	private static Task? _loop;

	public static int EqualizerPresetCount => EqualizerBands.Length;

	public static bool SupportsOutputDevice
	{
		get
		{
			if (Utility.Platform.IsWindows)
				return Environment.OSVersion.Version.Build >= 17134;
			return Utility.Platform.IsLinux && LinuxAudioSessions.CanRoute;
		}
	}

	public static bool SupportsEqualizer => Utility.Platform.IsLinux && LinuxAudioSessions.CanFilter;

	private static string SelectedOutput => App.Settings.Prop.RobloxAudioOutputDevice ?? "";

	private static int SelectedEqualizer => Math.Clamp(App.Settings.Prop.RobloxAudioEqualizer, 0, EqualizerBands.Length - 1);

	public static async Task<IReadOnlyList<RobloxAudioOutput>> GetOutputsAsync(CancellationToken token = default)
	{
		if (Utility.Platform.IsWindows)
			return await Task.Run(GetWindowsOutputs, token).ConfigureAwait(false);
		if (Utility.Platform.IsLinux)
		{
			IReadOnlyList<LinuxAudioSink>? sinks = await LinuxAudioSessions.FindSinksAsync(token).ConfigureAwait(false);
			return sinks?.Select(sink => new RobloxAudioOutput(sink.Name, sink.Description)).ToList() ?? [];
		}
		return [];
	}

	public static void ApplyFromSettings()
	{
		if (Utility.Platform.IsWindows)
		{
			if (SupportsOutputDevice)
				foreach (Process process in RobloxProcessNames.SelectMany(Process.GetProcessesByName))
					using (process)
						ApplyWindows((uint)process.Id);
			return;
		}

		if (!Utility.Platform.IsLinux)
			return;

		if (SelectedOutput.Length > 0 || SelectedEqualizer > 0)
			StartLinux();
		else
			StopLinux();
	}

	public static void NotifyRobloxLaunched(int processId)
	{
		if (Utility.Platform.IsWindows && SupportsOutputDevice && processId > 0)
			ApplyWindows((uint)processId);
	}

	public static void Shutdown()
	{
		if (Utility.Platform.IsLinux)
			StopLinux();
	}

	public static MMDevice ResolveWindowsDevice(MMDeviceEnumerator enumerator)
	{
		string selected = SelectedOutput;
		if (selected.Length > 0)
		{
			try
			{
				MMDevice device = enumerator.GetDevice(selected);
				if (device.State == DeviceState.Active)
					return device;
				device.Dispose();
			}
			catch (COMException)
			{
			}
		}
		return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
	}

	private static List<RobloxAudioOutput> GetWindowsOutputs()
	{
		List<RobloxAudioOutput> outputs = [];
		try
		{
			using MMDeviceEnumerator enumerator = new();
			foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
			{
				using (device)
					outputs.Add(new RobloxAudioOutput(device.ID, device.FriendlyName));
			}
		}
		catch (COMException ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Output devices could not be listed: " + ex.Message);
		}
		return outputs;
	}

	private static void ApplyWindows(uint processId)
	{
		string selected = SelectedOutput;
		int hr = Native.SetPersistedDefaultEndpoint(processId, selected.Length > 0 ? Native.ToPolicyDeviceId(selected) : null);
		if (hr < 0)
			App.Logger?.WriteLine(LOG_IDENT, $"Roblox output device could not be set, code 0x{hr:X8}");
		else
			App.Logger?.WriteLine(LOG_IDENT, selected.Length > 0 ? "Roblox audio sent to the chosen output device" : "Roblox audio follows the system default device");
	}

	private static void StartLinux()
	{
		lock (Gate)
		{
			if (_cts != null)
				return;
			CancellationTokenSource source = new();
			_cts = source;
			_loop = Task.Run(() => LinuxLoopAsync(source.Token));
		}
		App.Logger?.WriteLine(LOG_IDENT, "Roblox audio routing started");
	}

	private static void StopLinux()
	{
		CancellationTokenSource? source;
		Task? loop;
		lock (Gate)
		{
			source = _cts;
			loop = _loop;
			_cts = null;
			_loop = null;
		}
		if (source == null)
			return;
		try
		{
			source.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		try
		{
			loop?.Wait(4000);
		}
		catch (AggregateException)
		{
		}
		source.Dispose();
		App.Logger?.WriteLine(LOG_IDENT, "Roblox audio routing stopped");
	}

	private static async Task LinuxLoopAsync(CancellationToken token)
	{
		Dictionary<int, string> applied = [];
		FileStream? ownership = null;
		try
		{
			while (!token.IsCancellationRequested)
			{
				ownership ??= LinuxAudioSessions.TryAcquireOwnership("voidstrap-audio-route");
				if (ownership == null)
				{
					await Task.Delay(OwnershipRetryMs, token).ConfigureAwait(false);
					continue;
				}

				string output = SelectedOutput;
				int preset = SelectedEqualizer;
				string target = output;
				if (preset > 0)
				{
					if (LinuxAudioEqualizer.Ensure(EqualizerBands[preset], output.Length > 0 ? output : null))
						target = LinuxAudioEqualizer.SinkName;
					else
						App.Logger?.WriteLine(LOG_IDENT, "The PipeWire equalizer could not start");
				}
				else
				{
					LinuxAudioEqualizer.Stop();
				}

				IReadOnlyList<LinuxAudioStream>? streams = await LinuxAudioSessions.FindRobloxStreamsAsync(token).ConfigureAwait(false);
				if (streams != null)
				{
					HashSet<int> live = streams.Select(stream => stream.NodeId).ToHashSet();
					foreach (int gone in applied.Keys.Where(id => !live.Contains(id)).ToList())
						applied.Remove(gone);
					foreach (LinuxAudioStream stream in streams)
					{
						if (applied.TryGetValue(stream.NodeId, out string? current) && current == target)
							continue;
						if (await LinuxAudioSessions.SetStreamTargetAsync(stream.NodeId, target, token).ConfigureAwait(false))
							applied[stream.NodeId] = target;
					}
				}

				await Task.Delay(LinuxRefreshMs, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("RobloxAudioRouting::LinuxLoop", ex);
		}
		finally
		{
			using CancellationTokenSource timeout = new(3000);
			foreach (int nodeId in applied.Keys)
			{
				try
				{
					await LinuxAudioSessions.SetStreamTargetAsync(nodeId, null, timeout.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
			LinuxAudioEqualizer.Stop();
			ownership?.Dispose();
		}
	}

	private static partial class Native
	{
		private const string PolicyClass = "Windows.Media.Internal.AudioPolicyConfig";
		private const int SetPersistedSlot = 25;
		private const int RoleConsole = 0;
		private const int RoleMultimedia = 1;
		private const int FlowRender = 0;

		private static readonly Guid PolicyIidModern = new("ab3d4648-e242-459f-b02f-541c70306324");
		private static readonly Guid PolicyIidLegacy = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

		public static string ToPolicyDeviceId(string deviceId)
		{
			return @"\\?\SWD#MMDEVAPI#" + deviceId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
		}

		public static unsafe int SetPersistedDefaultEndpoint(uint processId, string? policyDeviceId)
		{
			IntPtr classId = IntPtr.Zero;
			IntPtr device = IntPtr.Zero;
			IntPtr factory = IntPtr.Zero;
			try
			{
				int hr = WindowsCreateString(PolicyClass, (uint)PolicyClass.Length, out classId);
				if (hr < 0)
					return hr;
				Guid iid = Environment.OSVersion.Version.Build >= 21390 ? PolicyIidModern : PolicyIidLegacy;
				hr = RoGetActivationFactory(classId, in iid, out factory);
				if (hr < 0 || factory == IntPtr.Zero)
					return hr;
				if (policyDeviceId != null)
				{
					hr = WindowsCreateString(policyDeviceId, (uint)policyDeviceId.Length, out device);
					if (hr < 0)
						return hr;
				}
				IntPtr method = (*(IntPtr**)factory)[SetPersistedSlot];
				var setPersisted = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)method;
				hr = setPersisted(factory, processId, FlowRender, RoleMultimedia, device);
				if (hr < 0)
					return hr;
				return setPersisted(factory, processId, FlowRender, RoleConsole, device);
			}
			finally
			{
				if (factory != IntPtr.Zero)
					Marshal.Release(factory);
				if (device != IntPtr.Zero)
					WindowsDeleteString(device);
				if (classId != IntPtr.Zero)
					WindowsDeleteString(classId);
			}
		}

		[LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
		private static partial int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

		[LibraryImport("combase.dll")]
		private static partial int WindowsDeleteString(IntPtr hstring);

		[LibraryImport("combase.dll")]
		private static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);
	}
}
