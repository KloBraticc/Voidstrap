using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public sealed partial class GStreamerPlayback : IMusicPlayback
{
    private const string Gst = "libgstreamer-1.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string PbUtils = "libgstpbutils-1.0.so.0";

    private const int StateNull = 1;
    private const int StatePaused = 3;
    private const int StatePlaying = 4;
    private const int StateChangeFailure = 0;
    private const int FormatTime = 3;
    private const int SeekFlushAccurate = 3;
    private const int MessageEos = 1;
    private const int MessageError = 2;
    private const int MessageTypeOffset = 64;
    private const uint PlayFlagsAudioOnly = 0x12;
    private const int ErrorMessageOffset = 8;
    private const ulong PrerollTimeout = 10_000_000_000UL;
    private const ulong ProbeTimeout = 5_000_000_000UL;
    private const ulong ClockTimeNone = ulong.MaxValue;
    private const int BusPollMilliseconds = 250;
    private const int BandCount = 10;

    private static readonly nuint TypeDouble = 15 << 2;
    private static readonly nuint TypeString = 16 << 2;
    private static readonly Lazy<string?> InitError = new(Initialize, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _gate = new();
    private readonly float[] _gains = new float[BandCount];
    private nint _pipeline;
    private nint _bus;
    private nint _equalizer;
    private Timer? _busTimer;
    private double _volume = 1.0;
    private bool _eqEnabled;
    private bool _ended;
    private TimeSpan _duration;
    private bool _disposed;

    public GStreamerPlayback()
    {
        _ = Task.Run(() => InitError.Value);
    }

    public bool IsPlaying { get; private set; }

    public bool HasTrack => _pipeline != 0;

    public Exception? LastError { get; private set; }

    public event EventHandler? PlaybackEnded;

    public event EventHandler? PlayStateChanged;

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                if (_duration <= TimeSpan.Zero && _pipeline != 0 && gst_element_query_duration(_pipeline, FormatTime, out long nanoseconds) && nanoseconds > 0)
                    _duration = FromNanoseconds(nanoseconds);
                return _duration;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return _pipeline != 0 && gst_element_query_position(_pipeline, FormatTime, out long nanoseconds) && nanoseconds > 0
                    ? FromNanoseconds(nanoseconds)
                    : TimeSpan.Zero;
            }
        }
        set
        {
            lock (_gate)
            {
                if (_pipeline == 0)
                    return;
                TimeSpan target = value < TimeSpan.Zero ? TimeSpan.Zero : value;
                if (_duration > TimeSpan.Zero && target > _duration)
                    target = _duration;
                gst_element_seek_simple(_pipeline, FormatTime, SeekFlushAccurate, target.Ticks * 100);
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            lock (_gate)
            {
                if (_pipeline != 0)
                    SetDouble(_pipeline, "volume", _volume);
            }
        }
    }

    public bool EqualizerEnabled
    {
        get => _eqEnabled;
        set
        {
            _eqEnabled = value;
            ApplyEqualizer();
        }
    }

    public void SetBandGain(int band, float gainDb)
    {
        if (band < 0 || band >= _gains.Length)
            return;
        _gains[band] = gainDb;
        ApplyEqualizer();
    }

    public bool Load(string path)
    {
        Stop();
        LastError = null;
        string? initError = InitError.Value;
        if (initError != null)
        {
            LastError = new InvalidOperationException(initError);
            return false;
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LastError = new FileNotFoundException("The file was not found", path);
            return false;
        }

        nint pipeline = 0;
        nint bus = 0;
        try
        {
            pipeline = gst_element_factory_make("playbin", null);
            if (pipeline == 0)
            {
                LastError = new InvalidOperationException("The GStreamer playbin plugin is missing. Install the GStreamer base plugins to play music.");
                return false;
            }
            gst_object_ref_sink(pipeline);
            SetString(pipeline, "uri", FileUri(path));
            SetFlags(pipeline, "flags", PlayFlagsAudioOnly);
            nint equalizer = gst_element_factory_make("equalizer-10bands", null);
            if (equalizer != 0)
                SetObject(pipeline, "audio-filter", equalizer);
            SetDouble(pipeline, "volume", _volume);
            bus = gst_element_get_bus(pipeline);

            int result = gst_element_set_state(pipeline, StatePaused);
            if (result != StateChangeFailure)
                result = gst_element_get_state(pipeline, out _, out _, PrerollTimeout);
            if (result == StateChangeFailure)
            {
                string reason = (bus != 0 ? PopError(bus) : null) ?? "GStreamer could not open this file";
                LastError = new InvalidOperationException(reason);
                Release(pipeline, bus);
                return false;
            }

            TimeSpan duration = gst_element_query_duration(pipeline, FormatTime, out long nanoseconds) && nanoseconds > 0
                ? FromNanoseconds(nanoseconds)
                : TimeSpan.Zero;
            lock (_gate)
            {
                _pipeline = pipeline;
                _bus = bus;
                _equalizer = equalizer;
                _duration = duration;
                _ended = false;
            }
            ApplyEqualizer();
            _busTimer = new Timer(PollBus, null, BusPollMilliseconds, BusPollMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LastError = new InvalidOperationException("GStreamer could not be used: " + ex.Message, ex);
            if (pipeline != 0)
                Release(pipeline, bus);
            return false;
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_pipeline == 0)
                return;
            if (_ended)
            {
                _ended = false;
                gst_element_seek_simple(_pipeline, FormatTime, SeekFlushAccurate, 0);
                _busTimer?.Change(BusPollMilliseconds, BusPollMilliseconds);
            }
            gst_element_set_state(_pipeline, StatePlaying);
        }
        IsPlaying = true;
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_pipeline == 0)
                return;
            gst_element_set_state(_pipeline, StatePaused);
        }
        IsPlaying = false;
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        bool hadTrack = IsPlaying || _pipeline != 0;
        Timer? timer = _busTimer;
        _busTimer = null;
        timer?.Dispose();
        nint pipeline;
        nint bus;
        lock (_gate)
        {
            pipeline = _pipeline;
            bus = _bus;
            _pipeline = 0;
            _bus = 0;
            _equalizer = 0;
            _duration = TimeSpan.Zero;
            _ended = false;
        }
        if (pipeline != 0)
            Release(pipeline, bus);
        IsPlaying = false;
        if (hadTrack)
            PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    public static TimeSpan ProbeDuration(string path)
    {
        if (InitError.Value != null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return TimeSpan.Zero;
        nint discoverer = 0;
        try
        {
            discoverer = gst_discoverer_new(ProbeTimeout, out nint createError);
            FreeError(createError);
            if (discoverer == 0)
                return TimeSpan.Zero;
            nint info = gst_discoverer_discover_uri(discoverer, FileUri(path), out nint discoverError);
            FreeError(discoverError);
            if (info == 0)
                return TimeSpan.Zero;
            ulong duration = gst_discoverer_info_get_duration(info);
            g_object_unref(info);
            return duration == ClockTimeNone || duration == 0 ? TimeSpan.Zero : FromNanoseconds((long)duration);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return TimeSpan.Zero;
        }
        finally
        {
            if (discoverer != 0)
                g_object_unref(discoverer);
        }
    }

    private void PollBus(object? state)
    {
        try
        {
            string? error = null;
            bool finished = false;
            lock (_gate)
            {
                if (_bus == 0 || _ended)
                    return;
                nint message = gst_bus_pop_filtered(_bus, MessageEos | MessageError);
                if (message == 0)
                    return;
                if (Marshal.ReadInt32(message, MessageTypeOffset) == MessageError)
                    error = ReadError(message) ?? "GStreamer stopped with an error";
                gst_mini_object_unref(message);
                finished = true;
                _ended = true;
                _busTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (!finished)
                return;
            LastError = error == null ? null : new InvalidOperationException(error);
            IsPlaying = false;
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
    }

    private void ApplyEqualizer()
    {
        lock (_gate)
        {
            if (_equalizer == 0)
                return;
            for (int band = 0; band < _gains.Length; band++)
                SetDouble(_equalizer, "band" + band, _eqEnabled ? Math.Clamp(_gains[band], -24f, 12f) : 0.0);
        }
    }

    private static string? Initialize()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
            return "GStreamer playback is only used on Linux";
        try
        {
            if (gst_init_check(0, 0, out nint error))
                return null;
            string reason = ReadGError(error) ?? "GStreamer could not start";
            FreeError(error);
            return reason;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "GStreamer is not installed. Install gstreamer with its base and good plugins to play music.";
        }
    }

    private static void Release(nint pipeline, nint bus)
    {
        gst_element_set_state(pipeline, StateNull);
        if (bus != 0)
            gst_object_unref(bus);
        gst_object_unref(pipeline);
    }

    private static string FileUri(string path)
    {
        nint uri = gst_filename_to_uri(Path.GetFullPath(path), out nint error);
        FreeError(error);
        if (uri == 0)
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        try
        {
            return Marshal.PtrToStringUTF8(uri) ?? new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }
        finally
        {
            g_free(uri);
        }
    }

    private static string? PopError(nint bus)
    {
        nint message = gst_bus_pop_filtered(bus, MessageError);
        if (message == 0)
            return null;
        try
        {
            return ReadError(message);
        }
        finally
        {
            gst_mini_object_unref(message);
        }
    }

    private static string? ReadError(nint message)
    {
        gst_message_parse_error(message, out nint error, out nint debug);
        string? reason = ReadGError(error);
        FreeError(error);
        if (debug != 0)
            g_free(debug);
        return reason;
    }

    private static string? ReadGError(nint error)
    {
        if (error == 0)
            return null;
        nint text = Marshal.ReadIntPtr(error, ErrorMessageOffset);
        return text == 0 ? null : Marshal.PtrToStringUTF8(text);
    }

    private static void FreeError(nint error)
    {
        if (error != 0)
            g_error_free(error);
    }

    private static TimeSpan FromNanoseconds(long nanoseconds) => TimeSpan.FromTicks(nanoseconds / 100);

    private static void SetDouble(nint target, string name, double value)
    {
        GValue gvalue = default;
        g_value_init(ref gvalue, TypeDouble);
        g_value_set_double(ref gvalue, value);
        g_object_set_property(target, name, ref gvalue);
        g_value_unset(ref gvalue);
    }

    private static void SetString(nint target, string name, string value)
    {
        GValue gvalue = default;
        g_value_init(ref gvalue, TypeString);
        g_value_set_string(ref gvalue, value);
        g_object_set_property(target, name, ref gvalue);
        g_value_unset(ref gvalue);
    }

    private static void SetFlags(nint target, string name, uint flags)
    {
        nuint type = g_type_from_name("GstPlayFlags");
        if (type == 0)
            return;
        GValue gvalue = default;
        g_value_init(ref gvalue, type);
        g_value_set_flags(ref gvalue, flags);
        g_object_set_property(target, name, ref gvalue);
        g_value_unset(ref gvalue);
    }

    private static void SetObject(nint target, string name, nint value)
    {
        nuint type = g_type_from_name("GstElement");
        if (type == 0)
            return;
        GValue gvalue = default;
        g_value_init(ref gvalue, type);
        g_value_set_object(ref gvalue, value);
        g_object_set_property(target, name, ref gvalue);
        g_value_unset(ref gvalue);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GValue
    {
        public nuint Type;
        public long Data0;
        public long Data1;
    }

    [LibraryImport(Gst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_init_check(nint argc, nint argv, out nint error);

    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint gst_element_factory_make(string factoryName, string? name);

    [LibraryImport(Gst)]
    private static partial nint gst_object_ref_sink(nint target);

    [LibraryImport(Gst)]
    private static partial void gst_object_unref(nint target);

    [LibraryImport(Gst)]
    private static partial void gst_mini_object_unref(nint target);

    [LibraryImport(Gst)]
    private static partial int gst_element_set_state(nint element, int state);

    [LibraryImport(Gst)]
    private static partial int gst_element_get_state(nint element, out int state, out int pending, ulong timeout);

    [LibraryImport(Gst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_element_query_position(nint element, int format, out long position);

    [LibraryImport(Gst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_element_query_duration(nint element, int format, out long duration);

    [LibraryImport(Gst)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool gst_element_seek_simple(nint element, int format, int flags, long position);

    [LibraryImport(Gst)]
    private static partial nint gst_element_get_bus(nint element);

    [LibraryImport(Gst)]
    private static partial nint gst_bus_pop_filtered(nint bus, int types);

    [LibraryImport(Gst)]
    private static partial void gst_message_parse_error(nint message, out nint error, out nint debug);

    [LibraryImport(Gst, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint gst_filename_to_uri(string filename, out nint error);

    [LibraryImport(PbUtils)]
    private static partial nint gst_discoverer_new(ulong timeout, out nint error);

    [LibraryImport(PbUtils, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint gst_discoverer_discover_uri(nint discoverer, string uri, out nint error);

    [LibraryImport(PbUtils)]
    private static partial ulong gst_discoverer_info_get_duration(nint info);

    [LibraryImport(GObject)]
    private static partial nint g_value_init(ref GValue value, nuint type);

    [LibraryImport(GObject)]
    private static partial void g_value_set_double(ref GValue value, double number);

    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void g_value_set_string(ref GValue value, string text);

    [LibraryImport(GObject)]
    private static partial void g_value_set_flags(ref GValue value, uint flags);

    [LibraryImport(GObject)]
    private static partial void g_value_set_object(ref GValue value, nint target);

    [LibraryImport(GObject)]
    private static partial void g_value_unset(ref GValue value);

    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void g_object_set_property(nint target, string name, ref GValue value);

    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint g_type_from_name(string name);

    [LibraryImport(GObject)]
    private static partial void g_object_unref(nint target);

    [LibraryImport(GLib)]
    private static partial void g_error_free(nint error);

    [LibraryImport(GLib)]
    private static partial void g_free(nint memory);
}
