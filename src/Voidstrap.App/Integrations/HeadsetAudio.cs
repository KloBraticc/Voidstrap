using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Voidstrap.Integrations;

public static partial class HeadsetAudio
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
	private const int FailurePollMs = 30000;
	private const int FailuresBeforeBackoff = 5;

	private static readonly object _gate = new object();
	private static CancellationTokenSource? _cts;
	private static Thread? _thread;
	private static float? _baseVolume;
	private static int _consecutiveFailures;
	private static string? _lastFailureReason;

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
		if (Voidstrap.Utility.Platform.IsLinux)
			return;
		if (App.Settings.Prop.EnableHeadsetLoudness)
			Start();
		else
			Stop();
	}

	public static bool Start()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
			return false;
		lock (_gate)
		{
			if (IsRunning)
				return true;
			_consecutiveFailures = 0;
			_lastFailureReason = null;
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

	private static void WaitAfterFailure(string reason, CancellationToken token)
	{
		if (_lastFailureReason != reason)
		{
			_lastFailureReason = reason;
			_consecutiveFailures = 0;
			App.Logger?.WriteLine(LOG_IDENT, reason);
		}
		if (_consecutiveFailures < int.MaxValue)
			_consecutiveFailures++;
		if (_consecutiveFailures == FailuresBeforeBackoff)
			App.Logger?.WriteLine(LOG_IDENT, "Retrying every " + FailurePollMs / 1000 + " seconds until this changes");
		token.WaitHandle.WaitOne(_consecutiveFailures >= FailuresBeforeBackoff ? FailurePollMs : IdlePollMs);
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
				WaitAfterFailure("Roblox audio session not available yet", token);
				return;
			}

			client = Native.ActivateProcessLoopback(pid, out int activateHr);
			if (client == null)
			{
				WaitAfterFailure($"Process loopback unavailable, code 0x{activateHr:X8}", token);
				return;
			}

			if (!Native.TryInitialize(client, device, out bool isFloat, out int channels, out int sampleRate))
			{
				WaitAfterFailure("No supported capture format", token);
				return;
			}

			pump = new EventWaitHandle(false, EventResetMode.AutoReset);
			client.SetEventHandle(pump.SafeWaitHandle.DangerousGetHandle());
			Guid captureIid = Native.IID_AudioCaptureClient;
			if (client.GetService(ref captureIid, out IntPtr capturePointer) != 0)
			{
				WaitAfterFailure("Capture service unavailable", token);
				return;
			}
			capture = Native.Wrap<Native.IAudioCaptureClient>(capturePointer);
			if (capture == null)
			{
				WaitAfterFailure("Capture client could not be created", token);
				return;
			}

			float baseVolume = session.SimpleAudioVolume.Volume;
			if (baseVolume <= 0.01f)
				baseVolume = 1f;
			restoreVolume = baseVolume;
			lock (_gate)
				_baseVolume = baseVolume;

			client.Start();
			started = true;
			_consecutiveFailures = 0;
			_lastFailureReason = null;

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

				float packetMs = sampleCount / (float)channels / (sampleRate / 1000f);
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
			((object?)capture as ComObject)?.FinalRelease();
			((object?)client as ComObject)?.FinalRelease();
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

	internal static partial class Native
	{
		public const uint BufferFlagSilent = 0x2;

		private static readonly StrategyBasedComWrappers Wrappers = new StrategyBasedComWrappers();

		public static T? Wrap<T>(IntPtr pointer) where T : class
		{
			if (pointer == IntPtr.Zero)
				return null;
			try
			{
				return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
			}
			finally
			{
				Marshal.Release(pointer);
			}
		}

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

		[GeneratedComInterface]
		[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
		public partial interface IAudioClient
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
			int GetService(ref Guid riid, out IntPtr service);
		}

		[GeneratedComInterface]
		[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
		public partial interface IAudioCaptureClient
		{
			[PreserveSig]
			int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long devicePosition, out long qpcPosition);

			[PreserveSig]
			int ReleaseBuffer(uint frames);

			[PreserveSig]
			int GetNextPacketSize(out uint frames);
		}

		[GeneratedComInterface]
		[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
		public partial interface IActivateAudioInterfaceAsyncOperation
		{
			[PreserveSig]
			int GetActivateResult(out int result, out IntPtr activated);
		}

		[GeneratedComInterface]
		[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
		public partial interface IActivateAudioInterfaceCompletionHandler
		{
			void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
		}

		[GeneratedComInterface]
		[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
		public partial interface IAgileObject
		{
		}

		[GeneratedComClass]
		internal sealed partial class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
		{
			private readonly object _sync = new object();

			private bool _abandoned;

			public readonly ManualResetEventSlim Completed = new ManualResetEventSlim(false);

			public IntPtr Activated;

			public int Result_HResult;

			public void Abandon()
			{
				lock (_sync)
				{
					_abandoned = true;
					if (Activated != IntPtr.Zero)
					{
						Marshal.Release(Activated);
						Activated = IntPtr.Zero;
					}
				}
			}

			public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
			{
				try
				{
					Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int result, out IntPtr activated));
					lock (_sync)
					{
						Result_HResult = result;
						if (_abandoned)
						{
							if (activated != IntPtr.Zero)
								Marshal.Release(activated);
						}
						else
						{
							Activated = activated;
						}
					}
				}
				catch (Exception ex)
				{
					Result_HResult = ex.HResult;
				}
				Completed.Set();
			}
		}

		[LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
		private static partial int ActivateAudioInterfaceAsync(string devicePath, in Guid riid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

		public static IAudioClient? ActivateProcessLoopback(uint processId, out int hr)
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
				Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(VirtualLoopbackDevice, in IID_AudioClient, variantPtr, handler, out _));
				if (!handler.Completed.Wait(5000))
				{
					handler.Abandon();
					hr = -1;
					return null;
				}
				hr = handler.Result_HResult;
				return hr == 0 ? Wrap<IAudioClient>(handler.Activated) : null;
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

		public static bool TryInitialize(IAudioClient client, MMDevice? device, out bool isFloat, out int channels, out int sampleRate)
		{
			isFloat = false;
			channels = 2;
			sampleRate = 48000;
			foreach (WaveFormat format in CandidateFormats(device))
			{
				if (Initialize(client, format) != 0)
					continue;
				isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
				channels = format.Channels;
				sampleRate = format.SampleRate;
				return true;
			}
			return false;
		}

		private static IEnumerable<WaveFormat> CandidateFormats(MMDevice? device)
		{
			int mixRate = 0;
			int mixChannels = 0;
			try
			{
				if (device != null)
				{
					using AudioClient audioClient = device.CreateAudioClient();
					WaveFormat? mix = audioClient.MixFormat;
					if (mix != null)
					{
						mixRate = mix.SampleRate;
						mixChannels = mix.Channels;
					}
				}
			}
			catch
			{
			}
			if (mixRate > 0 && mixChannels > 0)
			{
				yield return WaveFormat.CreateIeeeFloatWaveFormat(mixRate, mixChannels);
				yield return new WaveFormat(mixRate, 16, mixChannels);
				if (mixChannels != 2)
				{
					yield return WaveFormat.CreateIeeeFloatWaveFormat(mixRate, 2);
					yield return new WaveFormat(mixRate, 16, 2);
				}
			}
			foreach (int rate in new[] { 48000, 44100 })
			{
				if (rate == mixRate)
					continue;
				yield return WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
				yield return new WaveFormat(rate, 16, 2);
			}
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
