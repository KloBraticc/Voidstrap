using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Integrations.Overlays;

internal static partial class RobloxCursorOverlay
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExTransparent = 0x20;
    private const uint WsExTopmost = 0x8;
    private const uint WsExToolWindow = 0x80;
    private const uint WsExLayered = 0x80000;
    private const uint WsExNoActivate = 0x8000000;
    private const int CursorShowing = 0x1;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoRedraw = 0x8;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpShowWindow = 0x40;
    private const uint SwpNoOwnerZOrder = 0x200;
    private const uint SwpNoSendChanging = 0x400;
    private const int SwHide = 0;
    private const uint PmRemove = 0x1;
    private const uint UlwAlpha = 0x2;
    private const byte AcSrcAlpha = 0x1;
    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const int SmSwapButton = 23;
    private const int MinimumFrameDelay = 20;
    private const int IdleWaitMilliseconds = 100;
    private const int MinimumFrameWaitMilliseconds = 4;
    private const int HoverMarker = 1;
    private const int MaximumCachedCursors = 32;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);
    private static readonly object Gate = new();
    private static readonly IntPtr ClassName = Marshal.StringToHGlobalUni("VoidstrapCursorOverlay");
    private static readonly CursorOverrideKind[] Kinds = [CursorOverrideKind.Normal, CursorOverrideKind.Hover, CursorOverrideKind.Click];
    private static bool _classRegistered;
    private static OverlayThread? _active;
    private static string? _activeSignature;

    internal static void Reconcile()
    {
        const string LOG_IDENT = "RobloxCursorOverlay::Reconcile";
        bool wanted = CursorManager.OverrideActive && OverlayHub.RobloxProcessRunning && !OverlayHub.ShuttingDown;
        string? signature = wanted ? BuildSignature() : null;
        lock (Gate)
        {
            if (_active != null && (!wanted || signature != _activeSignature))
            {
                _active.Stop();
                _active = null;
                _activeSignature = null;
                App.Logger.WriteLine(LOG_IDENT, "Stopped the Roblox cursor override overlay");
            }
            if (!wanted || _active != null)
                return;
        }
        try
        {
            CursorFrames?[] sets = Kinds.Select(PrepareFrames).ToArray();
            if (sets[0] == null)
            {
                App.Logger.WriteLine(LOG_IDENT, "No cursor image could be loaded, the overlay stays off");
                return;
            }
            lock (Gate)
            {
                if (_active != null)
                    return;
                _active = new OverlayThread(sets);
                _activeSignature = signature;
            }
            App.Logger.WriteLine(
                LOG_IDENT,
                "Started the Roblox cursor override overlay, cursor "
                    + sets[0]!.Pixels.Length
                    + " frame(s) at "
                    + sets[0]!.Width
                    + "x"
                    + sets[0]!.Height
                    + ", hover image "
                    + (sets[1] != null)
                    + ", click image "
                    + (sets[2] != null)
            );
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
        }
    }

    private static string BuildSignature()
    {
        StringBuilder signature = new StringBuilder();
        foreach (CursorOverrideKind kind in Kinds)
        {
            string? path = CursorManager.OverrideImagePath(kind);
            long stamp = 0;
            try
            {
                if (path != null)
                    stamp = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            signature.Append(path).Append('|').Append(stamp).Append('|');
        }
        AppSettings settings = App.Settings.Prop;
        return signature.Append(settings.RobloxCursorOverrideSize).Append('|').Append(settings.RobloxCursorOverrideCentered).Append('|').Append(settings.CursorType).ToString();
    }

    private static CursorFrames? PrepareFrames(CursorOverrideKind kind)
    {
        (IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames, double hotspotX, double hotspotY, int width) = CursorManager.LoadOverrideCursor(kind);
        if (frames.Count == 0)
            return null;
        BitmapSource first = frames[0].Frame;
        int targetWidth = Math.Max(1, width);
        int targetHeight = Math.Max(1, (int)Math.Round(targetWidth * first.PixelHeight / (double)Math.Max(1, first.PixelWidth)));
        byte[][] pixels = new byte[frames.Count][];
        int[] delays = new int[frames.Count];
        for (int index = 0; index < frames.Count; index++)
        {
            pixels[index] = ToPremultiplied(frames[index].Frame, targetWidth, targetHeight);
            delays[index] = Math.Max(MinimumFrameDelay, frames[index].DelayMilliseconds);
        }
        return new CursorFrames(
            targetWidth,
            targetHeight,
            (int)Math.Round(hotspotX * (targetWidth - 1)),
            (int)Math.Round(hotspotY * (targetHeight - 1)),
            pixels,
            delays
        );
    }

    private static byte[] ToPremultiplied(BitmapSource source, int width, int height)
    {
        BitmapSource scaled = source;
        if (source.PixelWidth != width || source.PixelHeight != height)
            scaled = new TransformedBitmap(source, new ScaleTransform(width / (double)source.PixelWidth, height / (double)source.PixelHeight));
        BitmapSource converted = scaled.Format == PixelFormats.Pbgra32 ? scaled : new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(new System.Windows.Int32Rect(0, 0, Math.Min(width, converted.PixelWidth), Math.Min(height, converted.PixelHeight)), pixels, width * 4, 0);
        return pixels;
    }

    private sealed record CursorFrames(int Width, int Height, int HotspotX, int HotspotY, byte[][] Pixels, int[] Delays);

    private sealed class FrameSet
    {
        public required CursorFrames Frames { get; init; }

        public required IntPtr[] Bitmaps { get; init; }

        public int FrameAt(long elapsed)
        {
            int[] delays = Frames.Delays;
            if (delays.Length < 2)
                return 0;
            long total = 0;
            foreach (int delay in delays)
                total += delay;
            long position = elapsed % total;
            for (int index = 0; index < delays.Length; index++)
            {
                if (position < delays[index])
                    return index;
                position -= delays[index];
            }
            return 0;
        }
    }

    private sealed class OverlayThread
    {
        private readonly CursorFrames?[] _sets;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly IDisposable _trackerLease;
        private readonly Thread _thread;
        private readonly Dictionary<IntPtr, int> _markers = new();
        private IntPtr _screenDc;
        private bool _presentFailureLogged;
        private readonly HashSet<CursorOverrideKind> _loggedKinds = [];

        public OverlayThread(CursorFrames?[] sets)
        {
            _sets = sets;
            _trackerLease = RobloxWindowTracker.Acquire();
            _thread = new Thread(Run) { IsBackground = true, Name = "Voidstrap cursor overlay", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _cancellation.Cancel();
            _thread.Join(1000);
            _trackerLease.Dispose();
            _cancellation.Dispose();
        }

        private void Run()
        {
            const string LOG_IDENT = "RobloxCursorOverlay::Run";
            CancellationToken token = _cancellation.Token;
            IntPtr window = IntPtr.Zero;
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr originalBitmap = IntPtr.Zero;
            List<IntPtr> created = [];
            try
            {
                SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
                IntPtr instance = GetModuleHandleW(IntPtr.Zero);
                EnsureWindowClass(instance);
                CursorFrames normal = _sets[0]!;
                window = CreateWindowExW(
                    WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
                    ClassName,
                    "Voidstrap Cursor",
                    WsPopup,
                    0,
                    0,
                    normal.Width,
                    normal.Height,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    instance,
                    IntPtr.Zero
                );
                if (window == IntPtr.Zero)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not create the cursor window, error " + Marshal.GetLastPInvokeError());
                    return;
                }
                _screenDc = GetDC(IntPtr.Zero);
                memoryDc = CreateCompatibleDC(_screenDc);
                FrameSet?[] sets = new FrameSet?[_sets.Length];
                for (int index = 0; index < _sets.Length; index++)
                {
                    if (_sets[index] is not CursorFrames frames)
                        continue;
                    IntPtr[] bitmaps = new IntPtr[frames.Pixels.Length];
                    for (int frame = 0; frame < bitmaps.Length; frame++)
                    {
                        bitmaps[frame] = CreateFrameBitmap(memoryDc, frames, frames.Pixels[frame]);
                        created.Add(bitmaps[frame]);
                    }
                    sets[index] = new FrameSet { Frames = frames, Bitmaps = bitmaps };
                }
                originalBitmap = SelectObject(memoryDc, sets[0]!.Bitmaps[0]);
                Loop(window, memoryDc, sets, token);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
            finally
            {
                if (window != IntPtr.Zero)
                    DestroyWindow(window);
                if (memoryDc != IntPtr.Zero)
                {
                    if (originalBitmap != IntPtr.Zero)
                        SelectObject(memoryDc, originalBitmap);
                    DeleteDC(memoryDc);
                }
                foreach (IntPtr bitmap in created)
                    DeleteObject(bitmap);
                if (_screenDc != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, _screenDc);
            }
        }

        private void Loop(IntPtr window, IntPtr memoryDc, FrameSet?[] sets, CancellationToken token)
        {
            bool shown = false;
            int lastLeft = int.MinValue;
            int lastTop = int.MinValue;
            FrameSet? current = null;
            int frame = -1;
            long started = Environment.TickCount64;
            BlendFunction blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            while (!token.IsCancellationRequested)
            {
                PumpMessages();
                PointerState state = ReadPointer(out int x, out int y, out IntPtr cursor);
                if (state == PointerState.Visible)
                {
                    CursorOverrideKind kind = ResolveKind(cursor, sets);
                    if (_loggedKinds.Add(kind))
                        App.Logger.WriteLine("RobloxCursorOverlay::Loop", "Showing the " + kind + " cursor for the first time this session");
                    FrameSet wanted = sets[(int)kind] ?? sets[0]!;
                    bool resized = !ReferenceEquals(wanted, current);
                    int wantedFrame = wanted.FrameAt(Environment.TickCount64 - started);
                    if (resized || wantedFrame != frame)
                    {
                        current = wanted;
                        frame = wantedFrame;
                        Present(window, memoryDc, current, frame, blend);
                    }
                    int left = x - wanted.Frames.HotspotX;
                    int top = y - wanted.Frames.HotspotY;
                    if (!shown)
                    {
                        SetWindowPos(window, HwndTopmost, left, top, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
                        shown = true;
                    }
                    else if (left != lastLeft || top != lastTop || resized)
                    {
                        SetWindowPos(window, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging | SwpNoRedraw);
                    }
                    lastLeft = left;
                    lastTop = top;
                }
                else if (shown)
                {
                    ShowWindow(window, SwHide);
                    shown = false;
                }
                if (state == PointerState.RobloxInactive)
                    token.WaitHandle.WaitOne(IdleWaitMilliseconds);
                else
                    WaitForNextFrame(token);
            }
        }

        private void Present(IntPtr window, IntPtr memoryDc, FrameSet set, int frame, BlendFunction blend)
        {
            SelectObject(memoryDc, set.Bitmaps[frame]);
            NativePoint origin = default;
            NativeSize size = new NativeSize { Width = set.Frames.Width, Height = set.Frames.Height };
            if (!UpdateLayeredWindowSized(window, _screenDc, IntPtr.Zero, ref size, memoryDc, ref origin, 0, ref blend, UlwAlpha) && !_presentFailureLogged)
            {
                _presentFailureLogged = true;
                App.Logger.WriteLine("RobloxCursorOverlay::Present", "Could not draw the cursor frame, error " + Marshal.GetLastPInvokeError());
            }
        }

        private CursorOverrideKind ResolveKind(IntPtr cursor, FrameSet?[] sets)
        {
            if (sets[2] != null && IsPrimaryButtonDown())
                return CursorOverrideKind.Click;
            if (sets[1] == null)
                return CursorOverrideKind.Normal;
            return ReadMarker(cursor) == HoverMarker ? CursorOverrideKind.Hover : CursorOverrideKind.Normal;
        }

        private static bool IsPrimaryButtonDown()
        {
            int button = GetSystemMetrics(SmSwapButton) != 0 ? VkRightButton : VkLeftButton;
            return (GetAsyncKeyState(button) & 0x8000) != 0;
        }

        private int ReadMarker(IntPtr cursor)
        {
            if (cursor == IntPtr.Zero)
                return 0;
            if (_markers.TryGetValue(cursor, out int cached))
                return cached;
            int marker = ClassifyCursor(cursor);
            if (_markers.Count >= MaximumCachedCursors)
                _markers.Clear();
            _markers[cursor] = marker;
            return marker;
        }

        private int ClassifyCursor(IntPtr cursor)
        {
            if (!GetIconInfo(cursor, out IconInfo info))
                return 0;
            try
            {
                if (info.Color == IntPtr.Zero || GetObjectW(info.Color, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap bitmap) == 0)
                    return 0;
                int width = bitmap.Width;
                int height = Math.Abs(bitmap.Height);
                if (width < 4 || height < 4 || width > 512 || height > 512)
                    return 0;
                BitmapInfoHeader header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                };
                byte[] pixels = new byte[width * height * 4];
                if (GetDIBits(_screenDc, info.Color, 0, (uint)height, pixels, ref header, 0) == 0)
                    return 0;
                bool topLeft = AlphaAt(pixels, width, width / 4, height / 4) > 0;
                bool bottomLeft = AlphaAt(pixels, width, width / 4, height * 3 / 4) > 0;
                bool bottomRight = AlphaAt(pixels, width, width * 3 / 4, height * 3 / 4) > 0;
                bool topRight = AlphaAt(pixels, width, width * 3 / 4, height / 4) > 0;
                if (topRight)
                    return 0;
                if (topLeft && !bottomLeft && !bottomRight)
                    return 1;
                if (bottomLeft && !topLeft && !bottomRight)
                    return 2;
                if (bottomRight && !topLeft && !bottomLeft)
                    return 3;
                return 0;
            }
            finally
            {
                if (info.Color != IntPtr.Zero)
                    DeleteObject(info.Color);
                if (info.Mask != IntPtr.Zero)
                    DeleteObject(info.Mask);
            }
        }

        private static int AlphaAt(byte[] pixels, int width, int x, int y)
        {
            int alpha = pixels[(y * width + x) * 4 + 3];
            return alpha <= CursorManager.OverrideMarkerAlpha * 4 ? alpha : 0;
        }

        private static void WaitForNextFrame(CancellationToken token)
        {
            long started = Stopwatch.GetTimestamp();
            int result = DwmFlush();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result != 0 || elapsed < 1)
                token.WaitHandle.WaitOne(MinimumFrameWaitMilliseconds);
        }

        private static IntPtr CreateFrameBitmap(IntPtr memoryDc, CursorFrames frames, byte[] pixels)
        {
            BitmapInfoHeader header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = frames.Width,
                Height = -frames.Height,
                Planes = 1,
                BitCount = 32,
            };
            IntPtr bitmap = CreateDIBSection(memoryDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the cursor frame bitmap");
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            return bitmap;
        }
    }

    private enum PointerState
    {
        RobloxInactive,
        Hidden,
        Visible,
    }

    private static PointerState ReadPointer(out int x, out int y, out IntPtr cursor)
    {
        x = y = 0;
        cursor = IntPtr.Zero;
        RobloxWindowRect rect = RobloxWindowTracker.Current;
        if (!rect.Valid || rect.Hwnd == IntPtr.Zero || GetForegroundWindow() != rect.Hwnd)
            return PointerState.RobloxInactive;
        CursorInfo info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0)
            return PointerState.Hidden;
        if (!GetClientRect(rect.Hwnd, out NativeRect client))
            return PointerState.Hidden;
        NativePoint origin = default;
        if (!ClientToScreen(rect.Hwnd, ref origin))
            return PointerState.Hidden;
        x = info.X;
        y = info.Y;
        cursor = info.Cursor;
        bool inside = x >= origin.X && y >= origin.Y && x < origin.X + client.Right && y < origin.Y + client.Bottom;
        return inside ? PointerState.Visible : PointerState.Hidden;
    }

    private static void PumpMessages()
    {
        while (PeekMessageW(out NativeMessage message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    private static void EnsureWindowClass(IntPtr instance)
    {
        lock (Gate)
        {
            if (_classRegistered)
                return;
            WindowClass windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = NativeLibrary.GetExport(NativeLibrary.Load("user32.dll"), "DefWindowProcW"),
                Instance = instance,
                ClassName = ClassName,
            };
            _classRegistered = RegisterClassExW(ref windowClass) != 0 || Marshal.GetLastPInvokeError() == 1410;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public IntPtr MenuName;
        public IntPtr ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetCursorInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WindowClass windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(
        uint exStyle,
        IntPtr className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter
    );

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessageW(ref NativeMessage message);

    [LibraryImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindowSized(
        IntPtr hwnd,
        IntPtr destination,
        IntPtr position,
        ref NativeSize size,
        IntPtr source,
        ref NativePoint sourcePoint,
        uint colorKey,
        ref BlendFunction blend,
        uint flags
    );

    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [LibraryImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial IntPtr GetModuleHandleW(IntPtr moduleName);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")]
    private static partial IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("user32.dll", EntryPoint = "GetIconInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(IntPtr icon, out IconInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObjectW(IntPtr gdiObject, int size, out NativeBitmap bitmap);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits")]
    private static partial int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader header, uint usage);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static partial int DwmFlush();
}
