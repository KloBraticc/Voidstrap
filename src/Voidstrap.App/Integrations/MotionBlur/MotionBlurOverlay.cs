using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Voidstrap.Integrations.FrameGeneration;

namespace Voidstrap.Integrations.MotionBlur
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MotionBlurParams
    {
        public Vector4 Dims;
        public Vector4 Mb;
    }

    internal sealed class MotionBlurOverlay
    {
        private const string LOG_IDENT = "MotionBlur";
        private const float DeadZonePixels = 1.5f;
        private const double ResyncGapMs = 250.0;

        private static int _prepareStarted;
        private static int _prepareFailureLogged;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psPass;
        private ID3D11PixelShader? _psBlur;
        private ID3D11SamplerState? _sampler;
        private ID3D11Buffer? _cbuffer;
        private FrameGenPipeline? _flow;

        private static readonly ID3D11ShaderResourceView[] _nullSrvs = new ID3D11ShaderResourceView[3];

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _width;
        private int _height;
        private int _set;
        private bool _hasPrevious;
        private double _lastFrameMs;
        private double _intervalEmaMs;

        public static bool EnsurePrepared()
        {
            if (FrameGenPipeline.IsPrepared)
                return true;
            if (FrameGenPipeline.PrepareFailed)
            {
                if (Interlocked.Exchange(ref _prepareFailureLogged, 1) == 0)
                    App.Logger.WriteLine(LOG_IDENT, "Motion blur needs the motion estimation shaders, which failed to compile, so it stays off");
                return false;
            }
            if (Interlocked.Exchange(ref _prepareStarted, 1) == 0)
                System.Threading.Tasks.Task.Run(FrameGenPipeline.Prepare);
            return false;
        }

        public void AttachExternal(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
        {
            _device = device;
            _context = context;
            _width = Math.Max(16, width);
            _height = Math.Max(16, height);
            try
            {
                CreatePipeline();
                _flow = new FrameGenPipeline();
                _flow.Attach(device, context);
                _flow.SetQuality(1);
                _flow.EnsureSize(_width, _height);
            }
            catch
            {
                DisposeExternal();
                throw;
            }
            ResetHistory();
            App.Logger.WriteLine(LOG_IDENT, $"Attached as a per pixel motion blur stage at {_width}x{_height}");
        }

        private ID3D11PixelShader CompilePs(string entry)
        {
            Vortice.D3DCompiler.Compiler.Compile(MotionBlurShaders.Source, entry, "MotionBlur", "ps_5_0", out var blob, out var err);
            using (err)
            {
                if (blob == null)
                {
                    string msg = err != null ? err.AsString() : "unknown";
                    throw new InvalidOperationException("MotionBlur shader compile failed for " + entry + ": " + msg);
                }
            }
            using (blob)
            {
                return _device!.CreatePixelShader(blob.AsBytes());
            }
        }

        private void CreatePipeline()
        {
            Vortice.D3DCompiler.Compiler.Compile(MotionBlurShaders.Source, "VSMain", "MotionBlur", "vs_5_0", out var vsBlob, out var vsErr);
            using (vsErr)
            {
                if (vsBlob == null)
                    throw new InvalidOperationException("MotionBlur vertex shader compile failed");
            }
            using (vsBlob)
            {
                _vs = _device!.CreateVertexShader(vsBlob.AsBytes());
            }
            _psPass = CompilePs("PSPass");
            _psBlur = CompilePs("PSBlur");

            _sampler = _device!.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            });

            _cbuffer = _device!.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<MotionBlurParams>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });
        }

        private void ResetHistory()
        {
            _hasPrevious = false;
            _lastFrameMs = 0;
            _intervalEmaMs = 0;
            _flow?.ResetHistory();
        }

        private void DrawPass(ID3D11PixelShader ps, ID3D11RenderTargetView target, ID3D11ShaderResourceView t0, ID3D11ShaderResourceView? t1 = null, ID3D11ShaderResourceView? t2 = null)
        {
            _context!.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(target);
            _context.PSSetShader(ps);
            _context.PSSetShaderResource(0, t0);
            if (t1 != null)
                _context.PSSetShaderResource(1, t1);
            if (t2 != null)
                _context.PSSetShaderResource(2, t2);
            _context.Draw(3, 0);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        private void BindState()
        {
            _context!.VSSetShader(_vs);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
        }

        public void RenderInto(ID3D11ShaderResourceView input, ID3D11RenderTargetView output, int width, int height)
        {
            width = Math.Max(16, width);
            height = Math.Max(16, height);
            if (width != _width || height != _height)
            {
                _width = width;
                _height = height;
                _flow!.EnsureSize(_width, _height);
                ResetHistory();
            }

            int strength = MotionBlurSettings.StrengthIndex;
            double now = _clock.Elapsed.TotalMilliseconds;
            double gap = _lastFrameMs > 0 ? now - _lastFrameMs : 0;
            _lastFrameMs = now;
            if (strength <= 0 || !_flow!.Ready || gap > ResyncGapMs)
            {
                if (gap > ResyncGapMs)
                    ResetHistory();
                _lastFrameMs = now;
                if (strength > 0 && _flow!.Ready)
                {
                    _flow.BuildPyramid(_set, input);
                    _hasPrevious = true;
                }
                BindState();
                DrawPass(_psPass!, output, input);
                return;
            }

            if (gap > 0.5)
                _intervalEmaMs = _intervalEmaMs <= 0 ? gap : _intervalEmaMs * 0.9 + gap * 0.1;
            int current = _set ^ 1;
            _flow.BuildPyramid(current, input);
            if (!_hasPrevious)
            {
                _set = current;
                _hasPrevious = true;
                BindState();
                DrawPass(_psPass!, output, input);
                return;
            }

            double fps = _intervalEmaMs > 0.5 ? 1000.0 / _intervalEmaMs : 60.0;
            float searchRange = (float)Math.Clamp(12.0 * (55.0 / fps), 6.0, 28.0);
            _flow.ComputeBackwardFlow(_set, current, searchRange);
            _set = current;

            BindState();
            _context!.UpdateSubresource(new MotionBlurParams
            {
                Dims = new Vector4(_width, _height, 1f / _width, 1f / _height),
                Mb = new Vector4(MotionBlurSettings.ShutterFor(strength), MotionBlurSettings.MaxBlurPixelsFor(strength), DeadZonePixels, 0f),
            }, _cbuffer!);
            DrawPass(_psBlur!, output, input, _flow.BackwardFlowSrv, _flow.PreviousBackwardFlowSrv);
        }

        public void DisposeExternal()
        {
            try
            {
                _flow?.Dispose();
                _flow = null;
                _cbuffer?.Dispose();
                _sampler?.Dispose();
                _psPass?.Dispose();
                _psBlur?.Dispose();
                _vs?.Dispose();
                _cbuffer = null;
                _sampler = null;
                _psPass = null;
                _psBlur = null;
                _vs = null;
                _hasPrevious = false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("MotionBlurOverlay::DisposeExternal", ex);
            }
        }
    }
}
