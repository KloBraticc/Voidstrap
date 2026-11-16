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
    private const int MinimumFrameDelay = 20;
    private const int IdleWaitMilliseconds = 100;
    private const int MinimumFrameWaitMilliseconds = 4;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);
    private static readonly object Gate = new();
    private static readonly IntPtr ClassName = Marshal.StringToHGlobalUni("VoidstrapCursorOverlay");
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
            CursorFrames? frames = PrepareFrames();
            if (frames == null)
            {
                App.Logger.WriteLine(LOG_IDENT, "No cursor image could be loaded, the overlay stays off");
                return;
            }
            lock (Gate)
            {
                if (_active != null)
                    return;
                _active = new OverlayThread(frames);
                _activeSignature = signature;
            }
            App.Logger.WriteLine(LOG_IDENT, "Started the Roblox cursor override overlay with " + frames.Pixels.Length + " frame(s) at " + frames.Width + "x" + frames.Height);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
        }
    }

    private static string BuildSignature()
    {
        string? path = CursorManager.OverrideImagePath;
        long stamp = 0;
        try
        {
            if (path != null)
                stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        AppSettings settings = App.Settings.Prop;
        return string.Join('|', path ?? "", stamp, settings.RobloxCursorOverrideSize, settings.RobloxCursorOverrideCentered, settings.CursorType);
    }

    private static CursorFrames? PrepareFrames()
    {
        (IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames, double hotspotX, double hotspotY, int width) = CursorManager.LoadOverrideCursor();
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

    private sealed class OverlayThread
    {
        private readonly CursorFrames _frames;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly IDisposable _trackerLease;
        private readonly Thread _thread;

        public OverlayThread(CursorFrames frames)
        {
            _frames = frames;
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
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr originalBitmap = IntPtr.Zero;
            IntPtr[] bitmaps = new IntPtr[_frames.Pixels.Length];
            try
            {
                SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
                IntPtr instance = GetModuleHandleW(IntPtr.Zero);
                EnsureWindowClass(instance);
                window = CreateWindowExW(
                    WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
                    ClassName,
                    "Voidstrap Cursor",
                    WsPopup,
                    0,
                    0,
                    _frames.Width,
                    _frames.Height,
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
                screenDc = GetDC(IntPtr.Zero);
                memoryDc = CreateCompatibleDC(screenDc);
                for (int index = 0; index < bitmaps.Length; index++)
                    bitmaps[index] = CreateFrameBitmap(memoryDc, _frames.Pixels[index]);
                originalBitmap = SelectObject(memoryDc, bitmaps[0]);
                NativeSize size = new NativeSize { Width = _frames.Width, Height = _frames.Height };
                NativePoint origin = default;
                BlendFunction blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
                UpdateLayeredWindowSized(window, screenDc, IntPtr.Zero, ref size, memoryDc, ref origin, 0, ref blend, UlwAlpha);
                Loop(window, memoryDc, bitmaps, blend, token);
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
                foreach (IntPtr bitmap in bitmaps)
                {
                    if (bitmap != IntPtr.Zero)
                        DeleteObject(bitmap);
                }
                if (screenDc != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void Loop(IntPtr window, IntPtr memoryDc, IntPtr[] bitmaps, BlendFunction blend, CancellationToken token)
        {
            bool shown = false;
            int lastLeft = int.MinValue;
            int lastTop = int.MinValue;
            int frame = 0;
            long nextFrameAt = Environment.TickCount64 + _frames.Delays[0];
            while (!token.IsCancellationRequested)
            {
                PumpMessages();
                PointerState state = ReadPointer(out int x, out int y);
                if (state == PointerState.Visible)
                {
                    int left = x - _frames.HotspotX;
                    int top = y - _frames.HotspotY;
                    if (!shown)
                    {
                        SetWindowPos(window, HwndTopmost, left, top, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
                        shown = true;
                    }
                    else if (left != lastLeft || top != lastTop)
                    {
                        SetWindowPos(window, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging | SwpNoRedraw);
                    }
                    lastLeft = left;
                    lastTop = top;
                    if (bitmaps.Length > 1 && Environment.TickCount64 >= nextFrameAt)
                    {
                        frame = (frame + 1) % bitmaps.Length;
                        SelectObject(memoryDc, bitmaps[frame]);
                        NativePoint origin = default;
                        UpdateLayeredWindowContent(window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, memoryDc, ref origin, 0, ref blend, UlwAlpha);
                        nextFrameAt = Environment.TickCount64 + _frames.Delays[frame];
                    }
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

        private static void WaitForNextFrame(CancellationToken token)
        {
            long started = Stopwatch.GetTimestamp();
            int result = DwmFlush();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result != 0 || elapsed < 1)
                token.WaitHandle.WaitOne(MinimumFrameWaitMilliseconds);
        }

        private IntPtr CreateFrameBitmap(IntPtr memoryDc, byte[] pixels)
        {
            BitmapInfoHeader header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = _frames.Width,
                Height = -_frames.Height,
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

    private static PointerState ReadPointer(out int x, out int y)
    {
        x = y = 0;
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

    [LibraryImport("user32.dll", EntryPoint = "UpdateLayeredWindow")]
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

    [LibraryImport("user32.dll", EntryPoint = "UpdateLayeredWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindowContent(
        IntPtr hwnd,
        IntPtr destination,
        IntPtr position,
        IntPtr size,
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

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static partial int DwmFlush();
}
