using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Persistable;

namespace Voidstrap.Integrations;

internal static partial class DualSenseFeedback
{
    internal readonly record struct Trigger(int Preset, int Strength)
    {
        public bool Enabled => Preset != 0 && Strength != 0;
    }

    internal readonly record struct Presets(
        Trigger Left,
        Trigger Right,
        int Vibration,
        int VibrationStrength,
        int PulseLength,
        bool VibrateWhileHeld,
        HoldTrigger HoldTriggers,
        string DeviceId)
    {
        public bool RumbleEnabled => Vibration != 0 && VibrationStrength != 0;
        public bool Enabled => Left.Enabled || Right.Enabled || RumbleEnabled;
    }

    internal readonly record struct Controller(string Id, string Name);
    internal enum HoldTrigger { Both, Left, Right }

    private readonly record struct Output(Trigger Left, Trigger Right, byte LowMotor = 0, byte HighMotor = 0);

    public static Presets ReadPresets(AppSettings? settings) => new(
        new(Validate(settings?.DualSenseLeftTrigger), Math.Clamp(settings?.DualSenseLeftStrength ?? 100, 0, 100)),
        new(Validate(settings?.DualSenseRightTrigger), Math.Clamp(settings?.DualSenseRightStrength ?? 100, 0, 100)),
        Validate(settings?.DualSenseVibration),
        Math.Clamp(settings?.DualSenseVibrationStrength ?? 100, 0, 100),
        Math.Clamp(settings?.DualSensePulseLength ?? 100, 50, 200),
        settings?.DualSenseVibrateWhileHeld ?? false,
        settings?.DualSenseHoldTriggers switch
        {
            1 => HoldTrigger.Left,
            2 => HoldTrigger.Right,
            _ => HoldTrigger.Both
        },
        settings?.DualSenseDeviceId ?? ""
    );

    private static int Validate(int? value) => value is >= 1 and <= 3 ? value.Value : 0;

    internal enum PreviewResult { NoPreset, NoController, Stopped, Failed }

    public static IReadOnlyList<Controller> FindControllers()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        List<Controller> controllers = [];
        foreach (string path in FindControllerPaths())
        {
            using Device? device = Device.OpenPath(path);
            if (device == null)
                continue;
            string model = path.Contains("pid_0df2", StringComparison.OrdinalIgnoreCase) ? "DualSense Edge" : "DualSense";
            controllers.Add(new(path, $"{model} {controllers.Count + 1}"));
        }
        return controllers;
    }

    public static async Task<PreviewResult> PreviewAsync(Func<Presets> readPresets, CancellationToken token)
    {
        Presets presets = readPresets();
        if (!presets.Enabled)
            return PreviewResult.NoPreset;
        if (!OperatingSystem.IsWindows())
            return PreviewResult.NoController;

        using Device? device = Device.Open(presets.DeviceId);
        if (device == null)
            return PreviewResult.NoController;

        try
        {
            await UpdateAsync(device, () =>
            {
                Presets current = readPresets();
                return string.Equals(current.DeviceId, presets.DeviceId, StringComparison.OrdinalIgnoreCase) ? current : null;
            }, token).ConfigureAwait(false);
            return PreviewResult.Stopped;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return PreviewResult.Stopped;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return PreviewResult.Failed;
        }
        finally
        {
            await device.ResetAsync().ConfigureAwait(false);
        }
    }

    public static async Task RunAsync(int processId, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!IsActive(processId, out Presets presets))
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    continue;
                }

                using Device? device = Device.Open(presets.DeviceId);
                if (device != null)
                {
                    try
                    {
                        await UpdateAsync(
                            device,
                            () => IsActive(processId, out Presets current)
                                && string.Equals(current.DeviceId, presets.DeviceId, StringComparison.OrdinalIgnoreCase)
                                ? current
                                : null,
                            token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException || ex is OperationCanceledException && !token.IsCancellationRequested) { }
                    finally
                    {
                        await device.ResetAsync().ConfigureAwait(false);
                    }
                }
                await Task.Delay(device == null ? 750 : 100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static bool IsActive(int processId, out Presets presets)
    {
        presets = ReadPresets(App.Settings.Prop);
        return presets.Enabled && RobloxWindowTracker.IsProcessForeground(processId);
    }

    private static async Task UpdateAsync(Device device, Func<Presets?> readCurrentPresets, CancellationToken token)
    {
        FeedbackState state = new();
        while (readCurrentPresets() is { } presets)
        {
            token.ThrowIfCancellationRequested();
            (byte Left, byte Right)? triggers = presets.RumbleEnabled
                ? await device.ReadTriggersAsync(token).ConfigureAwait(false)
                : null;
            Output output = state.Update(presets, triggers, Environment.TickCount64);
            await device.ApplyAsync(output, token).ConfigureAwait(false);
            await Task.Delay(presets.RumbleEnabled ? 8 : 60, token).ConfigureAwait(false);
        }
    }

    private sealed class FeedbackState
    {
        private bool _leftPressed;
        private bool _rightPressed;
        private bool _rightPulse;
        private bool _hold;
        private long _pulseUntil;

        public Output Update(Presets presets, (byte Left, byte Right)? triggers, long now)
        {
            if (_hold != presets.VibrateWhileHeld || !presets.RumbleEnabled)
                _pulseUntil = 0;
            _hold = presets.VibrateWhileHeld;
            if (presets.RumbleEnabled && triggers is { } input)
            {
                bool nextLeft = input.Left >= (_leftPressed ? 45 : 85);
                bool nextRight = input.Right >= (_rightPressed ? 45 : 85);
                if (!_hold && (nextLeft && !_leftPressed || nextRight && !_rightPressed))
                {
                    _rightPulse = nextRight && !_rightPressed;
                    int duration = Pulse(presets.Vibration, _rightPulse).Duration * presets.PulseLength / 100;
                    _pulseUntil = now + duration;
                }
                _leftPressed = nextLeft;
                _rightPressed = nextRight;
            }
            else
            {
                _leftPressed = _rightPressed = false;
            }

            bool leftHeld = _leftPressed && presets.HoldTriggers != HoldTrigger.Right;
            bool rightHeld = _rightPressed && presets.HoldTriggers != HoldTrigger.Left;
            bool rumble = presets.RumbleEnabled && (_hold ? leftHeld || rightHeld : now < _pulseUntil);
            var pulse = rumble ? Pulse(presets.Vibration, _hold ? rightHeld : _rightPulse) : default;
            return new(
                presets.Left.Enabled ? presets.Left : default,
                presets.Right.Enabled ? presets.Right : default,
                (byte)(pulse.Low * presets.VibrationStrength / 100),
                (byte)(pulse.High * presets.VibrationStrength / 100));
        }
    }

    private static (byte Low, byte High, int Duration) Pulse(int preset, bool right) => preset switch
    {
        1 => (64, 35, 90),
        2 => (140, 90, 150),
        3 => right ? ((byte)48, (byte)125, 55) : ((byte)65, (byte)28, 55),
        _ => (0, 0, 0)
    };

    private sealed class Device(FileStream stream, int inputLength, int outputLength) : IDisposable
    {
        private readonly byte[] _input = new byte[inputLength];
        private Output _applied;

        public static Device? Open(string selectedId)
        {
            foreach (string path in FindControllerPaths())
            {
                if (selectedId.Length != 0 && !string.Equals(path, selectedId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (OpenPath(path) is { } device)
                    return device;
            }
            return null;
        }

        public static Device? OpenPath(string path)
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
                if (TryGetCaps(handle, out HidCaps caps)
                    && caps.UsagePage == 1 && caps.Usage == 5 && caps.InputLength == 64
                    && caps.OutputLength is 48 or 63 or 64)
                {
                    HidD_SetNumInputBuffers(handle, 2);
                    FileStream deviceStream = new(handle, FileAccess.ReadWrite, 1, true);
                    handle = null;
                    return new Device(deviceStream, caps.InputLength, caps.OutputLength);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally
            {
                handle?.Dispose();
            }
            return null;
        }

        public async Task ApplyAsync(Output output, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (output == _applied)
                return;

            byte[] report = new byte[outputLength];
            report[0] = 2;
            if (output.Right != _applied.Right)
            {
                report[1] |= 0x04;
                WriteTrigger(report.AsSpan(11, 11), output.Right);
            }
            if (output.Left != _applied.Left)
            {
                report[1] |= 0x08;
                WriteTrigger(report.AsSpan(22, 11), output.Left);
            }
            if (output.LowMotor != _applied.LowMotor || output.HighMotor != _applied.HighMotor)
            {
                report[1] |= 0x03;
                report[3] = output.HighMotor;
                report[4] = output.LowMotor;
            }

            using CancellationTokenSource timeout = new(450);
            await stream.WriteAsync(report, timeout.Token).ConfigureAwait(false);
            _applied = output;
        }

        public async Task<(byte Left, byte Right)?> ReadTriggersAsync(CancellationToken token)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(60);
            try
            {
                int count = await stream.ReadAsync(_input, timeout.Token).ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException();
                return count == _input.Length && _input[0] == 1 ? (_input[5], _input[6]) : null;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return null;
            }
        }

        public async Task ResetAsync()
        {
            try
            {
                await ApplyAsync(default, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }

        public void Dispose() => stream.Dispose();
    }

    private static void WriteTrigger(Span<byte> effect, Trigger trigger)
    {
        if (!trigger.Enabled)
        {
            effect[0] = 0x05;
            return;
        }

        if (trigger.Preset == 3)
        {
            effect[0] = 0x25;
            BinaryPrimitives.WriteUInt16LittleEndian(effect[1..], (1 << 2) | (1 << 6));
            effect[3] = (byte)(ScaleForce(4, trigger.Strength) - 1);
            return;
        }

        int start = trigger.Preset == 1 ? 3 : 2;
        uint force = ScaleForce(trigger.Preset == 1 ? 2 : 5, trigger.Strength);
        ushort zones = 0;
        uint forces = 0;
        for (int zone = start; zone < 10; zone++)
        {
            zones |= (ushort)(1 << zone);
            forces |= (force - 1) << (zone * 3);
        }
        effect[0] = 0x21;
        BinaryPrimitives.WriteUInt16LittleEndian(effect[1..], zones);
        BinaryPrimitives.WriteUInt32LittleEndian(effect[3..], forces);
    }

    private static uint ScaleForce(int force, int strength) => (uint)Math.Max(1, (force * strength + 50) / 100);

    private static IEnumerable<string> FindControllerPaths() => FindHidPaths()
        .Where(path => path.Contains("vid_054c&pid_0ce6", StringComparison.OrdinalIgnoreCase)
            || path.Contains("vid_054c&pid_0df2", StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.OrdinalIgnoreCase);

    private static unsafe string[] FindHidPaths()
    {
        HidD_GetHidGuid(out Guid hidClass);
        if (CM_Get_Device_Interface_List_SizeW(out int length, in hidClass, 0, 0) != 0 || length <= 1)
            return [];
        char[] paths = new char[length];
        fixed (char* buffer = paths)
        {
            if (CM_Get_Device_Interface_ListW(in hidClass, 0, buffer, length, 0) != 0)
                return [];
        }
        return new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool TryGetCaps(SafeFileHandle handle, out HidCaps caps)
    {
        caps = default;
        if (HidD_GetPreparsedData(handle, out nint data) == 0)
            return false;
        try
        {
            return HidP_GetCaps(data, out caps) == 0x00110000;
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputLength;
        public ushort OutputLength;
    }

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid guid);

    [LibraryImport("hid.dll")]
    private static partial byte HidD_GetPreparsedData(SafeFileHandle handle, out nint data);

    [LibraryImport("hid.dll")]
    private static partial byte HidD_FreePreparsedData(nint data);

    [LibraryImport("hid.dll")]
    private static partial byte HidD_SetNumInputBuffers(SafeFileHandle handle, uint count);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(nint data, out HidCaps caps);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Device_Interface_List_SizeW(out int length, in Guid guid, nint device, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static unsafe partial int CM_Get_Device_Interface_ListW(in Guid guid, nint device, char* buffer, int length, uint flags);
}
