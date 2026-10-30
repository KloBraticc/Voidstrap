using System;
using System.Runtime.InteropServices;
using System.Threading;
using DiscordRPC.IO;
using DiscordRPC.Logging;

namespace Voidstrap.Integrations;

internal sealed partial class NativeDiscordPipe : INamedPipeClient
{
    private const string Library = "voidrpc";

    private const int StateUnknown = 0;
    private const int StateReady = 1;
    private const int StateMissing = 2;

    private const int MaxFrame = 16 * 1024;

    private static int _state;

    private readonly object _gate = new();
    private readonly byte[] _buffer = new byte[MaxFrame];
    private IntPtr _connection;
    private int _pipe = -1;
    private bool _disposed;

    [LibraryImport(Library, EntryPoint = "voidrpc_version")]
    private static partial uint Version();

    [LibraryImport(Library, EntryPoint = "voidrpc_connect")]
    private static partial IntPtr ConnectNative(int pipe, out int connectedPipe);

    [LibraryImport(Library, EntryPoint = "voidrpc_is_connected")]
    private static partial int IsConnectedNative(IntPtr connection);

    [LibraryImport(Library, EntryPoint = "voidrpc_write")]
    private static unsafe partial int WriteNative(IntPtr connection, uint opcode, byte* data, uint length);

    [LibraryImport(Library, EntryPoint = "voidrpc_read")]
    private static unsafe partial int ReadNative(IntPtr connection, out uint opcode, byte* buffer, uint capacity, out uint length);

    [LibraryImport(Library, EntryPoint = "voidrpc_close")]
    private static partial void CloseNative(IntPtr connection);

    [LibraryImport(Library, EntryPoint = "voidrpc_find_pipe")]
    private static partial int FindPipeNative();

    public static bool IsAvailable
    {
        get
        {
            int state = Volatile.Read(ref _state);
            if (state != StateUnknown)
                return state == StateReady;
            try
            {
                bool ok = Version() == 1;
                Volatile.Write(ref _state, ok ? StateReady : StateMissing);
                return ok;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                App.Logger?.WriteLine("NativeDiscordPipe", $"Native voidrpc library is unavailable ({ex.GetType().Name}), using the managed Discord connection");
                Volatile.Write(ref _state, StateMissing);
                return false;
            }
        }
    }

    public static bool TryFindPipe(out int pipe)
    {
        pipe = IsAvailable ? FindPipeNative() : -1;
        return pipe >= 0;
    }

    public ILogger Logger { get; set; } = new NullLogger();

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _connection != IntPtr.Zero && IsConnectedNative(_connection) == 1;
        }
    }

    [Obsolete("The connected pipe is not necessary information.")]
    public int ConnectedPipe => _pipe;

    public bool Connect(int pipe)
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            CloseLocked();
            IntPtr connection = ConnectNative(pipe, out int connected);
            if (connection == IntPtr.Zero)
                return false;
            _connection = connection;
            _pipe = connected;
            Logger.Info("Connected to Discord through voidrpc on pipe {0}", connected);
            return true;
        }
    }

    public unsafe bool ReadFrame(out PipeFrame frame)
    {
        frame = default;
        lock (_gate)
        {
            if (_connection == IntPtr.Zero)
                return false;
            int result;
            uint opcode;
            uint length;
            fixed (byte* buffer = _buffer)
                result = ReadNative(_connection, out opcode, buffer, MaxFrame, out length);
            if (result != 1)
                return false;
            frame = new PipeFrame { Opcode = (Opcode)opcode, Data = _buffer.AsSpan(0, (int)length).ToArray() };
            return true;
        }
    }

    public unsafe bool WriteFrame(PipeFrame frame)
    {
        byte[] data = frame.Data ?? Array.Empty<byte>();
        lock (_gate)
        {
            if (_connection == IntPtr.Zero)
                return false;
            fixed (byte* bytes = data)
            {
                if (WriteNative(_connection, (uint)frame.Opcode, bytes, (uint)data.Length) == 1)
                    return true;
            }
            Logger.Warning("The Discord activity could not be written through voidrpc");
            return false;
        }
    }

    public void Close()
    {
        lock (_gate)
            CloseLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseLocked();
        }
        GC.SuppressFinalize(this);
    }

    private void CloseLocked()
    {
        if (_connection == IntPtr.Zero)
            return;
        CloseNative(_connection);
        _connection = IntPtr.Zero;
        _pipe = -1;
    }
}
