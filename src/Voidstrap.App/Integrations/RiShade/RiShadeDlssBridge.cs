using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Voidstrap.Integrations.RiShade
{
    internal sealed partial class RiShadeDlssBridge : IDisposable
    {
        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);

        private const string LOG_IDENT = "RiShadeDlss";
        private const int HeaderSize = 24;
        private const int GenerationOffset = 0;
        private const int WidthOffset = 4;
        private const int HeightOffset = 8;
        private const int LuidLowOffset = 12;
        private const int LuidHighOffset = 16;
        private const int StateOffset = 20;
        private const int StateFailed = 2;
        private const uint ResultWaitMs = 12;
        private const Vortice.DXGI.SharedResourceFlags SharedAccess = Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write;

        private readonly ID3D11Device _device;
        private readonly string _mapName;
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _header;
        private Process? _host;
        private uint _generation;
        private uint _width;
        private uint _height;
        private ID3D11Texture2D? _input;
        private ID3D11Texture2D? _output;
        private IDXGIKeyedMutex? _inputLock;
        private IDXGIKeyedMutex? _outputLock;
        private IntPtr _inputHandle;
        private IntPtr _outputHandle;
        private bool _failed;
        private bool _disposed;

        private RiShadeDlssBridge(ID3D11Device device, string mapName, MemoryMappedFile map)
        {
            _device = device;
            _mapName = mapName;
            _map = map;
            _header = map.CreateViewAccessor(0, HeaderSize);
        }

        public static RiShadeDlssBridge? TryStart(ID3D11Device device)
        {
            string? hostPath = ExtractHost();
            if (hostPath == null)
                return null;

            string mapName = $"Local\\RiShadeDlss_{Environment.ProcessId}_{Environment.TickCount64}";
            RiShadeDlssBridge? bridge = null;
            try
            {
                bridge = new RiShadeDlssBridge(device, mapName, MemoryMappedFile.CreateNew(mapName, HeaderSize));
                using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
                using (var adapter = dxgiDevice.GetAdapter())
                {
                    var luid = adapter.Description.Luid;
                    bridge._header.Write(LuidLowOffset, luid.LowPart);
                    bridge._header.Write(LuidHighOffset, luid.HighPart);
                }

                var startInfo = new ProcessStartInfo(hostPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = RiShadeManager.RiShadeDir,
                };
                startInfo.ArgumentList.Add("--root");
                startInfo.ArgumentList.Add(RiShadeManager.RiShadeDir);
                startInfo.ArgumentList.Add("--map");
                startInfo.ArgumentList.Add(mapName);
                startInfo.ArgumentList.Add("--parent");
                startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
                startInfo.Environment.Remove("RESHADE_DISABLE_GRAPHICS_HOOK");
                startInfo.Environment.Remove("RESHADE_DISABLE_LOGGING");
                bridge._host = Process.Start(startInfo);
                if (bridge._host == null)
                {
                    bridge.Dispose();
                    return null;
                }
                App.Logger.WriteLine(LOG_IDENT, "DLSS host started, ReShade writes its log to the dlss folder inside RiShade");
                return bridge;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeDlssBridge::TryStart", ex);
                bridge?.Dispose();
                return null;
            }
        }

        private static string? ExtractHost()
        {
            string hostPath = Path.Combine(RiShadeManager.RiShadeDir, "rishade_dlss.exe");
            try
            {
                using Stream? embedded = typeof(RiShadeDlssBridge).Assembly.GetManifestResourceStream("RiShade.rishade_dlss.exe");
                if (embedded == null)
                {
                    App.Logger.WriteLine(LOG_IDENT, "This build does not include the DLSS host, DLSS stays off");
                    return null;
                }
                byte[] bytes = new byte[embedded.Length];
                embedded.ReadExactly(bytes);
                if (!File.Exists(hostPath) || !File.ReadAllBytes(hostPath).AsSpan().SequenceEqual(bytes))
                {
                    string staged = hostPath + ".tmp";
                    File.WriteAllBytes(staged, bytes);
                    File.Move(staged, hostPath, true);
                }
                return hostPath;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The DLSS host could not be written to the RiShade folder: " + ex.Message);
                return File.Exists(hostPath) ? hostPath : null;
            }
        }

        public bool Failed => _failed;

        public void Apply(ID3D11DeviceContext context, ID3D11Texture2D frame)
        {
            if (_disposed || _failed)
                return;
            if (_host == null || _host.HasExited || _header.ReadInt32(StateOffset) == StateFailed)
            {
                _failed = true;
                App.Logger.WriteLine(LOG_IDENT, "The DLSS host stopped, frames continue without DLSS");
                return;
            }

            var desc = frame.Description;
            if (_input == null || desc.Width != _width || desc.Height != _height)
            {
                if (!CreateTextures(desc.Width, desc.Height))
                    return;
            }

            if (Acquire(_inputLock!, 0, 0))
            {
                context.CopyResource(_input!, frame);
                _inputLock!.ReleaseSync(1);
            }

            if (Acquire(_outputLock!, 1, ResultWaitMs))
            {
                context.CopyResource(frame, _output!);
                _outputLock!.ReleaseSync(0);
            }
        }

        private static unsafe bool Acquire(IDXGIKeyedMutex mutex, ulong key, uint timeoutMs)
        {
            void** vtable = *(void***)mutex.NativePointer;
            var acquireSync = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)vtable[8];
            return acquireSync(mutex.NativePointer, key, timeoutMs) == 0;
        }

        private bool CreateTextures(uint width, uint height)
        {
            ReleaseTextures();
            try
            {
                uint generation = _generation + 1;
                var desc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.SharedKeyedMutex | ResourceOptionFlags.SharedNTHandle,
                };
                _input = _device.CreateTexture2D(desc);
                _output = _device.CreateTexture2D(desc);
                using (var resource = _input.QueryInterface<IDXGIResource1>())
                    _inputHandle = resource.CreateSharedHandle(null, SharedAccess, $"{_mapName}_in_{generation}");
                using (var resource = _output.QueryInterface<IDXGIResource1>())
                    _outputHandle = resource.CreateSharedHandle(null, SharedAccess, $"{_mapName}_out_{generation}");
                _inputLock = _input.QueryInterface<IDXGIKeyedMutex>();
                _outputLock = _output.QueryInterface<IDXGIKeyedMutex>();

                _width = width;
                _height = height;
                _generation = generation;
                _header.Write(WidthOffset, width);
                _header.Write(HeightOffset, height);
                _header.Write(GenerationOffset, generation);
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeDlssBridge::CreateTextures", ex);
                ReleaseTextures();
                _failed = true;
                return false;
            }
        }

        private void ReleaseTextures()
        {
            _inputLock?.Dispose();
            _outputLock?.Dispose();
            _input?.Dispose();
            _output?.Dispose();
            _inputLock = null;
            _outputLock = null;
            _input = null;
            _output = null;
            if (_inputHandle != IntPtr.Zero)
                CloseHandle(_inputHandle);
            if (_outputHandle != IntPtr.Zero)
                CloseHandle(_outputHandle);
            _inputHandle = IntPtr.Zero;
            _outputHandle = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_host != null && !_host.HasExited)
                    _host.Kill();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The DLSS host could not be stopped: " + ex.Message);
            }
            _host?.Dispose();
            _host = null;
            ReleaseTextures();
            _header.Dispose();
            _map.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
