using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Voidstrap.Integrations.FrameGeneration
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameGenPipelineParams
    {
        public Vector4 Dims;
        public Vector4 SrcRect;
        public Vector4 Interp;
    }

    internal sealed class FrameGenPipeline
    {
        private const int PyramidLevels = 5;

        private ID3D11Device _device = null!;
        private ID3D11DeviceContext _context = null!;

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psPass;
        private ID3D11PixelShader? _psLumaColor;
        private ID3D11PixelShader? _psLumaDown;
        private ID3D11PixelShader? _psFlowCoarse;
        private ID3D11PixelShader? _psFlowCoarseFast;
        private ID3D11PixelShader? _psFlowRefine;
        private ID3D11PixelShader? _psFlowRefineFast;
        private ID3D11PixelShader? _psFlowRefineFine;
        private ID3D11PixelShader? _psFlowSmooth;
        private ID3D11PixelShader? _psFlowSmoothWide;
        private ID3D11PixelShader? _psFlowGlobal;
        private ID3D11PixelShader? _psWarp;
        private ID3D11PixelShader? _psWarpFast;
        private ID3D11PixelShader? _psWarpX;
        private ID3D11PixelShader? _psWarpXFast;
        private ID3D11SamplerState? _sampler;
        private ID3D11Buffer? _cbuffer;

        private readonly ID3D11Texture2D?[,] _lumaTex = new ID3D11Texture2D?[2, PyramidLevels];
        private readonly ID3D11ShaderResourceView?[,] _lumaSrv = new ID3D11ShaderResourceView?[2, PyramidLevels];
        private readonly ID3D11RenderTargetView?[,] _lumaRtv = new ID3D11RenderTargetView?[2, PyramidLevels];
        private readonly ID3D11Texture2D?[] _flowTex = new ID3D11Texture2D?[PyramidLevels];
        private readonly ID3D11ShaderResourceView?[] _flowSrv = new ID3D11ShaderResourceView?[PyramidLevels];
        private readonly ID3D11RenderTargetView?[] _flowRtv = new ID3D11RenderTargetView?[PyramidLevels];
        private readonly ID3D11Texture2D?[,] _flowSmoothTex = new ID3D11Texture2D?[2, 2];
        private readonly ID3D11ShaderResourceView?[,] _flowSmoothSrv = new ID3D11ShaderResourceView?[2, 2];
        private readonly ID3D11RenderTargetView?[,] _flowSmoothRtv = new ID3D11RenderTargetView?[2, 2];
        private readonly int[] _smoothIdx = new int[2];
        private readonly ID3D11Texture2D?[,] _flowGlobalTex = new ID3D11Texture2D?[2, 2];
        private readonly ID3D11ShaderResourceView?[,] _flowGlobalSrv = new ID3D11ShaderResourceView?[2, 2];
        private readonly ID3D11RenderTargetView?[,] _flowGlobalRtv = new ID3D11RenderTargetView?[2, 2];
        private readonly int[] _globalIdx = new int[2];
        private readonly int[] _levelW = new int[PyramidLevels];
        private readonly int[] _levelH = new int[PyramidLevels];

        private int _width;
        private int _height;
        private int _quality;
        private int _flowLevel = 2;
        private int _allocatedFlowLevel = -1;
        private bool _historyValid;
		private bool _disposed;

        private static readonly ID3D11ShaderResourceView?[] _nullSrvs = new ID3D11ShaderResourceView?[8];
        private static readonly int ProcessorCount = Environment.ProcessorCount;
        private static readonly string[] ShaderEntries = ["VSMain", "PSPass", "PSLumaColor", "PSLumaDown", "PSFlowCoarse", "PSFlowCoarseFast", "PSFlowRefine", "PSFlowRefineFast", "PSFlowRefineFine", "PSFlowSmooth", "PSFlowSmoothWide", "PSFlowGlobal", "PSWarp", "PSWarpFast", "PSWarpX", "PSWarpXFast"];
        private static readonly Lazy<byte[][]> ShaderBytecode = new(CompileShaders);
        private static volatile bool _prepareFailed;

        public static bool IsPrepared => ShaderBytecode.IsValueCreated;
        public static bool PrepareFailed => _prepareFailed;

        public static void Prepare()
        {
            try
            {
                _ = ShaderBytecode.Value;
                App.Logger.WriteLine("FrameGen", "Frame Generation shaders prepared");
            }
            catch (Exception ex)
            {
                _prepareFailed = true;
                App.Logger.WriteException("FrameGenPipeline::Prepare", ex);
            }
        }

        private static byte[][] CompileShaders()
        {
            byte[][] bytecode = new byte[ShaderEntries.Length][];
            Parallel.For(0, ShaderEntries.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(ProcessorCount / 2, 1, 2) }, i =>
            {
                string entry = ShaderEntries[i];
                Vortice.D3DCompiler.Compiler.Compile(FrameGenShaders.Source, entry, "FrameGen", i == 0 ? "vs_5_0" : "ps_5_0", out var blob, out var error);
                using (error)
                {
                    if (blob == null)
                    {
                        string message = error != null ? error.AsString() : "unknown";
                        throw new InvalidOperationException("FrameGen shader compile failed for " + entry + ": " + message);
                    }
                }
                using (blob)
                {
                    bytecode[i] = [.. blob.AsBytes()];
                }
            });
            return bytecode;
        }

        public void Attach(ID3D11Device device, ID3D11DeviceContext context)
        {
			_disposed = false;
            _device = device;
            _context = context;
            byte[][] bytecode = ShaderBytecode.Value;
            _vs = _device.CreateVertexShader(bytecode[0]);
            _psPass = _device.CreatePixelShader(bytecode[1]);
            _psLumaColor = _device.CreatePixelShader(bytecode[2]);
            _psLumaDown = _device.CreatePixelShader(bytecode[3]);
            _psFlowCoarse = _device.CreatePixelShader(bytecode[4]);
            _psFlowCoarseFast = _device.CreatePixelShader(bytecode[5]);
            _psFlowRefine = _device.CreatePixelShader(bytecode[6]);
            _psFlowRefineFast = _device.CreatePixelShader(bytecode[7]);
            _psFlowRefineFine = _device.CreatePixelShader(bytecode[8]);
            _psFlowSmooth = _device.CreatePixelShader(bytecode[9]);
            _psFlowSmoothWide = _device.CreatePixelShader(bytecode[10]);
            _psFlowGlobal = _device.CreatePixelShader(bytecode[11]);
            _psWarp = _device.CreatePixelShader(bytecode[12]);
            _psWarpFast = _device.CreatePixelShader(bytecode[13]);
            _psWarpX = _device.CreatePixelShader(bytecode[14]);
            _psWarpXFast = _device.CreatePixelShader(bytecode[15]);

            _sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            });

            _cbuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<FrameGenPipelineParams>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });
        }

        public void SetQuality(int quality)
        {
            _quality = Math.Clamp(quality, 0, 2);
            _flowLevel = _quality >= 2 ? 1 : 2;
        }

        public void ResetHistory()
        {
            _historyValid = false;
        }

        public bool EnsureSize(int width, int height)
        {
            width = Math.Max(16, width);
            height = Math.Max(16, height);
            if (width == _width && height == _height && _allocatedFlowLevel == _flowLevel && _flowSmoothTex[0, 0] != null)
                return false;
            _width = width;
            _height = height;
            ReleaseSized();
            try
            {
                AllocateSized();
            }
            catch (Exception ex)
            {
                ReleaseSized();
                _width = 0;
                _height = 0;
                _historyValid = false;
                App.Logger.WriteLine("FrameGenPipeline", $"Could not allocate flow resources for {width}x{height}, frame generation stays idle: {ex.Message}");
                return true;
            }
            _allocatedFlowLevel = _flowLevel;
            _historyValid = false;
            return true;
        }

        public bool Ready => _flowSmoothTex[0, 0] != null && _allocatedFlowLevel == _flowLevel;

        private void AllocateSized()
        {
            for (int i = 0; i < PyramidLevels; i++)
            {
                _levelW[i] = Math.Max(1, _width >> (i + 1));
                _levelH[i] = Math.Max(1, _height >> (i + 1));
                for (int set = 0; set < 2; set++)
                {
                    _lumaTex[set, i] = CreateTexture(_levelW[i], _levelH[i], Format.R16_Float);
                    _lumaSrv[set, i] = _device.CreateShaderResourceView(_lumaTex[set, i]);
                    _lumaRtv[set, i] = _device.CreateRenderTargetView(_lumaTex[set, i]);
                }
            }
            for (int i = 1; i < PyramidLevels; i++)
            {
                _flowTex[i] = CreateTexture(_levelW[i], _levelH[i], Format.R16G16B16A16_Float);
                _flowSrv[i] = _device.CreateShaderResourceView(_flowTex[i]);
                _flowRtv[i] = _device.CreateRenderTargetView(_flowTex[i]);
            }
            for (int d = 0; d < 2; d++)
            {
                for (int i = 0; i < 2; i++)
                {
                    _flowSmoothTex[d, i] = CreateTexture(_levelW[_flowLevel], _levelH[_flowLevel], Format.R16G16B16A16_Float);
                    _flowSmoothSrv[d, i] = _device.CreateShaderResourceView(_flowSmoothTex[d, i]);
                    _flowSmoothRtv[d, i] = _device.CreateRenderTargetView(_flowSmoothTex[d, i]);
                    _flowGlobalTex[d, i] = CreateTexture(1, 1, Format.R16G16B16A16_Float);
                    _flowGlobalSrv[d, i] = _device.CreateShaderResourceView(_flowGlobalTex[d, i]);
                    _flowGlobalRtv[d, i] = _device.CreateRenderTargetView(_flowGlobalTex[d, i]);
                }
                _globalIdx[d] = 0;
                _smoothIdx[d] = 0;
            }
        }

        private ID3D11Texture2D CreateTexture(int w, int h, Format format)
        {
            return _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });
        }

        private void SetState()
        {
            _context.VSSetShader(_vs);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        }

        private void SetPass(int w, int h, float t)
        {
            SetPass(w, h, t, 0f);
        }

        private void SetPass(int w, int h, float t, float aux)
        {
            _context.RSSetViewport(new Viewport(0, 0, w, h, 0, 1));
            _context.UpdateSubresource(new FrameGenPipelineParams
            {
                Dims = new Vector4(w, h, 1f / Math.Max(w, 1), 1f / Math.Max(h, 1)),
                SrcRect = new Vector4(0f, 0f, 1f, 1f),
                Interp = new Vector4(t, aux, 0f, 0f),
            }, _cbuffer!);
        }

        private void DrawPass(ID3D11PixelShader ps, ID3D11RenderTargetView target, ID3D11ShaderResourceView? t0, ID3D11ShaderResourceView? t1 = null, ID3D11ShaderResourceView? t2 = null)
        {
            _context.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(target);
            _context.PSSetShader(ps);
            _context.PSSetShaderResource(0, t0);
            if (t1 != null) _context.PSSetShaderResource(1, t1);
            if (t2 != null) _context.PSSetShaderResource(2, t2);
            _context.Draw(3, 0);
        }

        public void BuildPyramid(int set, ID3D11ShaderResourceView colorSrv)
        {
            SetState();
            SetPass(_levelW[0], _levelH[0], 0f);
            DrawPass(_psLumaColor!, _lumaRtv[set, 0]!, colorSrv);
            for (int i = 1; i < PyramidLevels; i++)
            {
                SetPass(_levelW[i], _levelH[i], 0f);
                DrawPass(_psLumaDown!, _lumaRtv[set, i]!, _lumaSrv[set, i - 1]);
            }
        }

        private float _coarseRange = 12f;

        public void ComputeFlow(int prevSet, int currSet)
        {
            ComputeFlow(prevSet, currSet, 12f);
        }

        public void ComputeFlow(int prevSet, int currSet, float searchRange)
        {
            _coarseRange = searchRange;
            SetState();
            ComputeFlowDirection(0, prevSet, currSet);
            ComputeFlowDirection(1, currSet, prevSet);
            _historyValid = true;
        }

        private void ComputeFlowDirection(int dir, int aSet, int bSet)
        {
            int coarse = PyramidLevels - 1;
            ID3D11PixelShader coarsePs = _quality == 0 ? _psFlowCoarseFast! : _psFlowCoarse!;
            ID3D11PixelShader refinePs = _quality == 0 ? _psFlowRefineFast! : _psFlowRefine!;
            SetPass(_levelW[coarse], _levelH[coarse], 0f, _coarseRange);
            DrawPass(coarsePs, _flowRtv[coarse]!, _lumaSrv[aSet, coarse], _lumaSrv[bSet, coarse]);
            for (int i = coarse - 1; i >= _flowLevel; i--)
            {
                SetPass(_levelW[i], _levelH[i], 0f);
                DrawPass(refinePs, _flowRtv[i]!, _lumaSrv[aSet, i], _lumaSrv[bSet, i], _flowSrv[i + 1]);
            }
            int sPrev = _smoothIdx[dir];
            _smoothIdx[dir] ^= 1;
            int sNow = _smoothIdx[dir];
            bool lowRate = _coarseRange > 18f;
            var smoothPs = _quality >= 2 ? _psFlowSmoothWide! : _psFlowSmooth!;
            float temporal = !_historyValid || _coarseRange >= 36f
                ? 0f
                : _quality == 0
                ? 0.22f
                : Math.Clamp(0.36f * 12f / Math.Max(12f, _coarseRange), 0.12f, 0.36f);
            int flowLevel = _flowLevel;
            SetPass(_levelW[flowLevel], _levelH[flowLevel], 0f, temporal);
            DrawPass(smoothPs, _flowSmoothRtv[dir, sNow]!, _flowSrv[flowLevel], _lumaSrv[bSet, flowLevel], _flowSmoothSrv[dir, sPrev]);
            if (lowRate && _quality >= 2 && flowLevel == 1)
            {
                SetPass(_levelW[flowLevel], _levelH[flowLevel], 0f);
                DrawPass(_psFlowRefineFine!, _flowRtv[flowLevel]!, _lumaSrv[aSet, flowLevel], _lumaSrv[bSet, flowLevel], _flowSmoothSrv[dir, sNow]);
                SetPass(_levelW[flowLevel], _levelH[flowLevel], 0f, temporal);
                DrawPass(smoothPs, _flowSmoothRtv[dir, sNow]!, _flowSrv[flowLevel], _lumaSrv[bSet, flowLevel], _flowSmoothSrv[dir, sPrev]);
            }
            int gPrev = _globalIdx[dir];
            _globalIdx[dir] ^= 1;
            float blend = _historyValid && _coarseRange < 36f ? Math.Clamp(0.45f * 12f / Math.Max(12f, _coarseRange), 0.18f, 0.45f) : 0f;
            SetPass(1, 1, blend);
            DrawPass(_psFlowGlobal!, _flowGlobalRtv[dir, _globalIdx[dir]]!, _flowSmoothSrv[dir, sNow], _flowGlobalSrv[dir, gPrev]);
        }

        public void Warp(ID3D11ShaderResourceView prevColor, ID3D11ShaderResourceView currColor, float t, ID3D11RenderTargetView output)
        {
            SetState();
            SetPass(_width, _height, t, _coarseRange / 12f);
            _context.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(output);
            _context.PSSetShader(_quality == 0 ? _psWarpFast : _psWarp);
            _context.PSSetShaderResource(0, prevColor);
            _context.PSSetShaderResource(1, currColor);
            _context.PSSetShaderResource(2, _flowSmoothSrv[0, _smoothIdx[0]]);
            _context.PSSetShaderResource(3, _flowGlobalSrv[0, _globalIdx[0]]);
            _context.PSSetShaderResource(4, _flowSmoothSrv[1, _smoothIdx[1]]);
            _context.PSSetShaderResource(5, _flowGlobalSrv[1, _globalIdx[1]]);
            _context.PSSetShaderResource(6, _flowGlobalSrv[0, _globalIdx[0] ^ 1]);
            _context.PSSetShaderResource(7, _flowGlobalSrv[1, _globalIdx[1] ^ 1]);
            _context.Draw(3, 0);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        public void WarpForward(ID3D11ShaderResourceView currColor, float t, ID3D11RenderTargetView output)
        {
            SetState();
            SetPass(_width, _height, t, _coarseRange / 12f);
            _context.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(output);
            _context.PSSetShader(_quality == 0 ? _psWarpXFast : _psWarpX);
            _context.PSSetShaderResource(0, _flowGlobalSrv[1, _globalIdx[1]]);
            _context.PSSetShaderResource(1, currColor);
            _context.PSSetShaderResource(2, _flowSmoothSrv[1, _smoothIdx[1]]);
            _context.PSSetShaderResource(3, _flowGlobalSrv[1, _globalIdx[1] ^ 1]);
            _context.Draw(3, 0);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        public void Blit(ID3D11ShaderResourceView input, ID3D11RenderTargetView output)
        {
            SetState();
            SetPass(_width, _height, 0f);
            DrawPass(_psPass!, output, input);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        private void ReleaseSized()
        {
            for (int set = 0; set < 2; set++)
            {
                for (int i = 0; i < PyramidLevels; i++)
                {
                    _lumaRtv[set, i]?.Dispose();
                    _lumaSrv[set, i]?.Dispose();
                    _lumaTex[set, i]?.Dispose();
                    _lumaRtv[set, i] = null;
                    _lumaSrv[set, i] = null;
                    _lumaTex[set, i] = null;
                }
            }
            for (int i = 0; i < PyramidLevels; i++)
            {
                _flowRtv[i]?.Dispose();
                _flowSrv[i]?.Dispose();
                _flowTex[i]?.Dispose();
                _flowRtv[i] = null;
                _flowSrv[i] = null;
                _flowTex[i] = null;
            }
            for (int d = 0; d < 2; d++)
            {
                for (int i = 0; i < 2; i++)
                {
                    _flowSmoothRtv[d, i]?.Dispose();
                    _flowSmoothSrv[d, i]?.Dispose();
                    _flowSmoothTex[d, i]?.Dispose();
                    _flowSmoothRtv[d, i] = null;
                    _flowSmoothSrv[d, i] = null;
                    _flowSmoothTex[d, i] = null;
                    _flowGlobalRtv[d, i]?.Dispose();
                    _flowGlobalSrv[d, i]?.Dispose();
                    _flowGlobalTex[d, i]?.Dispose();
                    _flowGlobalRtv[d, i] = null;
                    _flowGlobalSrv[d, i] = null;
                    _flowGlobalTex[d, i] = null;
                }
            }
            _allocatedFlowLevel = -1;
        }

        public void Dispose()
        {
			if (_disposed)
				return;
			_disposed = true;
            ReleaseSized();
            _cbuffer?.Dispose();
            _sampler?.Dispose();
            _psPass?.Dispose();
            _psLumaColor?.Dispose();
            _psLumaDown?.Dispose();
            _psFlowCoarse?.Dispose();
            _psFlowCoarseFast?.Dispose();
            _psFlowRefine?.Dispose();
            _psFlowRefineFast?.Dispose();
            _psFlowRefineFine?.Dispose();
            _psFlowSmooth?.Dispose();
            _psFlowSmoothWide?.Dispose();
            _psFlowGlobal?.Dispose();
            _psWarp?.Dispose();
            _psWarpFast?.Dispose();
            _psWarpX?.Dispose();
            _psWarpXFast?.Dispose();
            _vs?.Dispose();
			_cbuffer = null;
			_sampler = null;
			_psPass = null;
			_psLumaColor = null;
			_psLumaDown = null;
			_psFlowCoarse = null;
			_psFlowCoarseFast = null;
			_psFlowRefine = null;
			_psFlowRefineFast = null;
			_psFlowRefineFine = null;
			_psFlowSmooth = null;
			_psFlowSmoothWide = null;
			_psFlowGlobal = null;
			_psWarp = null;
			_psWarpFast = null;
			_psWarpX = null;
			_psWarpXFast = null;
			_vs = null;
        }
    }
}
