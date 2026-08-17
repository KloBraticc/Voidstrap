using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Voidstrap.Integrations;

public static class HeadsetAudio
{
	private const string LOG_IDENT = "HeadsetAudio";
	private const string RobloxProcess = "RobloxPlayerBeta";
	private const float ThresholdDb = -18f;
	private const float Ratio = 4f;
	private const float AttackMs = 15f;
	private const float ReleaseMs = 250f;
	private const float MinGain = 0.25f;
	private const float WriteEpsilon = 0.004f;
	private const int GainDelayPackets = 3;
	private const int IdlePollMs = 1000;

	private static readonly object _gate = new object();
	private static CancellationTokenSource? _cts;
	private static Thread? _thread;
	private static float? _baseVolume;

	public static bool IsRunning { get; private set; }

	public static float? BaseVolume
	{
		get
		{
			lock (_gate)
				return _baseVolume;
		}
	}

	public static void ApplyFromSettings()
	{
		if (App.Settings.Prop.EnableHeadsetLoudness)
			Start();
		else
			Stop();
	}

	public static bool Start()
	{
		lock (_gate)
		{
			if (IsRunning)
				return true;
			var cts = new CancellationTokenSource();
			Thread thread = new Thread(() => Loop(cts.Token))
			{
				IsBackground = true,
				Name = "VoidstrapHeadsetAudio",
				Priority = ThreadPriority.AboveNormal
			};
			_cts = cts;
			_thread = thread;
			IsRunning = true;
			try
			{
				thread.Start();
			}
			catch (Exception ex)
			{
				_cts = null;
				_thread = null;
				IsRunning = false;
				cts.Dispose();
				App.Logger?.WriteException("HeadsetAudio::Start", ex);
				return false;
			}
		}
		App.Logger?.WriteLine(LOG_IDENT, "Headset audio started");
		return true;
	}

	public static void Stop()
	{
		CancellationTokenSource? cts;
		Thread? thread;
		lock (_gate)
		{
			cts = _cts;
			thread = _thread;
			_cts = null;
			_thread = null;
			IsRunning = false;
		}
		if (cts == null && thread == null)
			return;
		try
		{
			cts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		try
		{
			thread?.Join(2000);
		}
		catch
		{
		}
		try
		{
			cts?.Dispose();
		}
		catch
		{
		}
		RestoreBaseVolume();
		App.Logger?.WriteLine(LOG_IDENT, "Headset audio stopped");
	}

	public static void Shutdown()
	{
		Stop();
	}

	private static void Loop(CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				uint pid = FindRobloxPid();
				if (pid == 0)
				{
					ClearBaseVolume();
					token.WaitHandle.WaitOne(IdlePollMs);
					continue;
				}
				RunSession(pid, token);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Headset audio loop error: " + ex.Message);
		}
		finally
		{
			RestoreBaseVolume();
		}
	}

	private static void RunSession(uint pid, CancellationToken token)
	{
		MMDeviceEnumerator? enumerator = null;
		MMDevice? device = null;
		AudioSessionControl? session = null;
		Native.IAudioClient? client = null;
		Native.IAudioCaptureClient? capture = null;
		EventWaitHandle? pump = null;
		bool started = false;
		float restoreVolume = 0f;

		try
		{
			enumerator = new MMDeviceEnumerator();
			device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
			session = FindSession(device, pid);
			if (session == null)
			{
				token.WaitHandle.WaitOne(IdlePollMs);
				return;
			}

			object? activated = Native.ActivateProcessLoopback(pid, out int activateHr);
			if (activated == null)
			{
				App.Logger?.WriteLine(LOG_IDENT, $"Process loopback unavailable, code 0x{activateHr:X8}");
				token.WaitHandle.WaitOne(IdlePollMs);
				return;
			}

			client = (Native.IAudioClient)activated;
			if (!Native.TryInitialize(client, out bool isFloat, out int channels))
			{
				App.Logger?.WriteLine(LOG_IDENT, "No supported capture format");
				token.WaitHandle.WaitOne(IdlePollMs);
				return;
			}

			pump = new EventWaitHandle(false, EventResetMode.AutoReset);
			client.SetEventHandle(pump.SafeWaitHandle.DangerousGetHandle());
			Guid captureIid = Native.IID_AudioCaptureClient;
			if (client.GetService(ref captureIid, out object captureObj) != 0)
			{
				token.WaitHandle.WaitOne(IdlePollMs);
				return;
			}
			capture = (Native.IAudioCaptureClient)captureObj;

			float baseVolume = session.SimpleAudioVolume.Volume;
			if (baseVolume <= 0.01f)
				baseVolume = 1f;
			restoreVolume = baseVolume;
			lock (_gate)
				_baseVolume = baseVolume;

			client.Start();
			started = true;

			var history = new float[8];
			for (int i = 0; i < history.Length; i++)
				history[i] = 1f;
			int historyIndex = 0;
			float gain = 1f;
			float appliedGain = 1f;
			byte[] buffer = new byte[65536];
			long nextPidCheck = Environment.TickCount64 + 2000;

			while (!token.IsCancellationRequested)
			{
				pump.WaitOne(100);

				long now = Environment.TickCount64;
				if (now >= nextPidCheck)
				{
					nextPidCheck = now + 2000;
					if (FindRobloxPid() != pid)
						break;
					float live = session.SimpleAudioVolume.Volume;
					if (Math.Abs(live - baseVolume * appliedGain) > 0.02f)
					{
						baseVolume = live / Math.Max(appliedGain, MinGain);
						baseVolume = Math.Clamp(baseVolume, 0.02f, 1f);
						restoreVolume = baseVolume;
						lock (_gate)
							_baseVolume = baseVolume;
					}
				}

				if (IsDuckingUnfocused(pid))
				{
					if (Math.Abs(appliedGain - 1f) > WriteEpsilon)
					{
						appliedGain = 1f;
						gain = 1f;
						TrySetVolume(session, baseVolume);
					}
					continue;
				}

				double sumSquares = 0;
				long sampleCount = 0;
				while (capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) == 0 && frames != 0)
				{
					int bytes = (int)frames * channels * (isFloat ? 4 : 2);
					if ((flags & Native.BufferFlagSilent) == 0 && data != IntPtr.Zero && bytes <= buffer.Length)
					{
						Marshal.Copy(data, buffer, 0, bytes);
						Accumulate(buffer, bytes, isFloat, ref sumSquares, ref sampleCount);
					}
					else if ((flags & Native.BufferFlagSilent) != 0)
					{
						sampleCount += (long)frames * channels;
					}
					capture.ReleaseBuffer(frames);
				}

				if (sampleCount == 0)
					continue;

				float capturedGain = history[(historyIndex - GainDelayPackets + history.Length) % history.Length];
				double rms = Math.Sqrt(sumSquares / sampleCount) / Math.Max(capturedGain, MinGain);
				float levelDb = rms > 1e-7 ? (float)(20.0 * Math.Log10(rms)) : -140f;

				float targetGainDb = levelDb > ThresholdDb ? -(levelDb - ThresholdDb) * (1f - 1f / Ratio) : 0f;
				float targetGain = Math.Clamp((float)Math.Pow(10.0, targetGainDb / 20.0), MinGain, 1f);

				float packetMs = sampleCount / (float)channels / 48f;
				float timeConstant = targetGain < gain ? AttackMs : ReleaseMs;
				float alpha = 1f - (float)Math.Exp(-packetMs / Math.Max(timeConstant, 1f));
				gain += (targetGain - gain) * alpha;
				gain = Math.Clamp(gain, MinGain, 1f);

				historyIndex = (historyIndex + 1) % history.Length;
				history[historyIndex] = gain;

				if (Math.Abs(gain - appliedGain) > WriteEpsilon)
				{
					appliedGain = gain;
					TrySetVolume(session, Math.Clamp(baseVolume * gain, 0f, 1f));
				}
			}

		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Headset audio session error: " + ex.Message);
			token.WaitHandle.WaitOne(IdlePollMs);
		}
		finally
		{
			if (session != null && restoreVolume > 0f)
				TrySetVolume(session, restoreVolume);
			try
			{
				if (started)
					client?.Stop();
			}
			catch
			{
			}
			if (capture != null && Marshal.IsComObject(capture))
				Marshal.ReleaseComObject(capture);
			if (client != null && Marshal.IsComObject(client))
				Marshal.ReleaseComObject(client);
			pump?.Dispose();
			device?.Dispose();
			enumerator?.Dispose();
			ClearBaseVolume();
		}
	}

	private static void Accumulate(byte[] buffer, int bytes, bool isFloat, ref double sumSquares, ref long sampleCount)
	{
		if (isFloat)
		{
			int count = bytes / 4;
			for (int i = 0; i < count; i++)
			{
				float v = BitConverter.ToSingle(buffer, i * 4);
				sumSquares += (double)v * v;
			}
			sampleCount += count;
			return;
		}
		int shorts = bytes / 2;
		for (int i = 0; i < shorts; i++)
		{
			double v = BitConverter.ToInt16(buffer, i * 2) / 32768.0;
			sumSquares += v * v;
		}
		sampleCount += shorts;
	}

	private static bool IsDuckingUnfocused(uint pid)
	{
		if (!App.Settings.Prop.DuckRobloxAudioOnUnfocus)
			return false;
		try
		{
			HWND foreground = PInvoke.GetForegroundWindow();
			if (foreground == HWND.Null)
				return true;
			PInvoke.GetWindowThreadProcessId(foreground, out uint foregroundPid);
			return foregroundPid != pid;
		}
		catch
		{
			return false;
		}
	}

	private static void TrySetVolume(AudioSessionControl session, float volume)
	{
		try
		{
			session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0.01f, 1f);
		}
		catch
		{
		}
	}

	private static AudioSessionControl? FindSession(MMDevice device, uint pid)
	{
		try
		{
			SessionCollection sessions = device.AudioSessionManager.Sessions;
			for (int i = 0; i < sessions.Count; i++)
			{
				try
				{
					if (sessions[i].GetProcessID == pid)
						return sessions[i];
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static void RestoreBaseVolume()
	{
		float? target;
		lock (_gate)
		{
			target = _baseVolume;
			_baseVolume = null;
		}
		if (!target.HasValue)
			return;
		uint pid = FindRobloxPid();
		if (pid == 0)
			return;
		try
		{
			using var enumerator = new MMDeviceEnumerator();
			using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
			AudioSessionControl? session = FindSession(device, pid);
			if (session != null)
				TrySetVolume(session, target.Value);
		}
		catch
		{
		}
	}

	private static void ClearBaseVolume()
	{
		lock (_gate)
			_baseVolume = null;
	}

	private static uint FindRobloxPid()
	{
		try
		{
			Process[] processes = Process.GetProcessesByName(RobloxProcess);
			uint result = 0;
			foreach (Process process in processes)
			{
				try
				{
					if (result == 0)
						result = (uint)process.Id;
				}
				catch
				{
				}
				finally
				{
					process.Dispose();
				}
			}
			return result;
		}
		catch
		{
			return 0;
		}
	}

	private static class Native
	{
		public const uint BufferFlagSilent = 0x2;

		public static readonly Guid IID_AudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

		private static readonly Guid IID_AudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

		private const string VirtualLoopbackDevice = "VAD\\Process_Loopback";

		private const uint StreamFlagsLoopback = 0x00020000;

		private const uint StreamFlagsEventCallback = 0x00040000;

		private const int UnsupportedFormat = unchecked((int)0x88890008);

		[StructLayout(LayoutKind.Sequential)]
		private struct ActivationParams
		{
			public int ActivationType;

			public uint TargetProcessId;

			public int ProcessLoopbackMode;
		}

		[ComImport]
		[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		public interface IAudioClient
		{
			[PreserveSig]
			int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

			[PreserveSig]
			int GetBufferSize(out uint frames);

			[PreserveSig]
			int GetStreamLatency(out long latency);

			[PreserveSig]
			int GetCurrentPadding(out uint padding);

			[PreserveSig]
			int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);

			[PreserveSig]
			int GetMixFormat(out IntPtr format);

			[PreserveSig]
			int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

			[PreserveSig]
			int Start();

			[PreserveSig]
			int Stop();

			[PreserveSig]
			int Reset();

			[PreserveSig]
			int SetEventHandle(IntPtr handle);

			[PreserveSig]
			int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
		}

		[ComImport]
		[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		public interface IAudioCaptureClient
		{
			[PreserveSig]
			int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long devicePosition, out long qpcPosition);

			[PreserveSig]
			int ReleaseBuffer(uint frames);

			[PreserveSig]
			int GetNextPacketSize(out uint frames);
		}

		[ComImport]
		[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		public interface IActivateAudioInterfaceAsyncOperation
		{
			void GetActivateResult([MarshalAs(UnmanagedType.Error)] out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
		}

		[ComImport]
		[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		public interface IActivateAudioInterfaceCompletionHandler
		{
			void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
		}

		private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler
		{
			public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);

			public object? Result;

			public int Result_HResult;

			public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
			{
				try
				{
					operation.GetActivateResult(out Result_HResult, out object activated);
					Result = activated;
				}
				catch (Exception ex)
				{
					Result_HResult = ex.HResult;
				}
				Completed.Set();
			}
		}

		[DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
		private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string devicePath, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

		public static object? ActivateProcessLoopback(uint processId, out int hr)
		{
			hr = 0;
			IntPtr paramsPtr = IntPtr.Zero;
			IntPtr variantPtr = IntPtr.Zero;
			try
			{
				var activation = new ActivationParams
				{
					ActivationType = 1,
					TargetProcessId = processId,
					ProcessLoopbackMode = 0
				};
				paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
				Marshal.StructureToPtr(activation, paramsPtr, false);
				variantPtr = Marshal.AllocHGlobal(32);
				for (int i = 0; i < 32; i++)
					Marshal.WriteByte(variantPtr, i, 0);
				Marshal.WriteInt16(variantPtr, 0, 65);
				Marshal.WriteInt32(variantPtr, 8, Marshal.SizeOf<ActivationParams>());
				Marshal.WriteIntPtr(variantPtr, 16, paramsPtr);

				var handler = new CompletionHandler();
				ActivateAudioInterfaceAsync(VirtualLoopbackDevice, IID_AudioClient, variantPtr, handler, out _);
				if (!handler.Completed.Wait(5000))
				{
					hr = -1;
					return null;
				}
				hr = handler.Result_HResult;
				return hr == 0 ? handler.Result : null;
			}
			catch (Exception ex)
			{
				hr = ex.HResult;
				return null;
			}
			finally
			{
				if (variantPtr != IntPtr.Zero)
					Marshal.FreeHGlobal(variantPtr);
				if (paramsPtr != IntPtr.Zero)
					Marshal.FreeHGlobal(paramsPtr);
			}
		}

		public static bool TryInitialize(IAudioClient client, out bool isFloat, out int channels)
		{
			isFloat = false;
			channels = 2;
			if (Initialize(client, new WaveFormat(48000, 16, 2)) == 0)
				return true;
			isFloat = true;
			return Initialize(client, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) == 0;
		}

		private static int Initialize(IAudioClient client, WaveFormat format)
		{
			IntPtr formatPtr = Marshal.AllocHGlobal(128);
			try
			{
				Marshal.StructureToPtr(format, formatPtr, false);
				return client.Initialize(0, StreamFlagsLoopback | StreamFlagsEventCallback, 2_000_000, 0, formatPtr, IntPtr.Zero);
			}
			catch
			{
				return UnsupportedFormat;
			}
			finally
			{
				Marshal.FreeHGlobal(formatPtr);
			}
		}
	}
}
