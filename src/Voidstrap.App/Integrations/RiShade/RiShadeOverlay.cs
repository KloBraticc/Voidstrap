using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Voidstrap.Integrations.RiShade
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RiShadeParams
    {
        public Vector4 PA;
        public Vector4 PB;
        public Vector4 PC;
        public Vector4 PD;
        public Vector4 PE;
        public Vector4 PF;
        public Vector4 PG;
        public Vector4 PH;
        public Vector4 PI;
        public Vector4 PJ;
        public Vector4 PK;
        public Vector4 PL;
        public Vector4 PM;
        public Vector4 PN;
        public Vector4 PO;
        public Vector4 PP;
        public Vector4 PQ;
        public Vector4 PR;
        public Vector4 PS;
        public Vector4 PT;
    }

    internal sealed class RiShadeOverlay
    {
        private const string LOG_IDENT = "RiShade";
        private const int RtA = 0;
        private const int RtB = 1;
        private const int RtGlossWide = 3;
        private const int RtGlossTemp = 4;
        private const int RtDown0 = 5;
        private const int RtUp0 = 10;
        private const int RtSceneBlurA = 14;
        private const int RtSceneBlurB = 15;
        private const int AiFeedStride = 2;
        private const int StagingCount = 3;
        private const float DepthPredictLead = 2.5f;
        private const long DepthIdleReleaseMs = 15000;

        private static readonly ID3D11ShaderResourceView[] _nullSrvs = new ID3D11ShaderResourceView[4];

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;

        private readonly ID3D11Texture2D?[] _workTex = new ID3D11Texture2D?[16];
        private readonly ID3D11ShaderResourceView?[] _workSrv = new ID3D11ShaderResourceView?[16];
        private readonly ID3D11RenderTargetView?[] _workRtv = new ID3D11RenderTargetView?[16];

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psMain;
        private ID3D11PixelShader? _psDownPrefilter;
        private ID3D11PixelShader? _psDown;
        private ID3D11PixelShader? _psUpTent;
        private ID3D11PixelShader? _psBlurH;
        private ID3D11PixelShader? _psBlurV;
        private ID3D11PixelShader? _psBloomCombine;
        private ID3D11PixelShader? _psDepthUp;
        private ID3D11PixelShader? _psGi;
        private ID3D11PixelShader? _psSsr;
        private ID3D11PixelShader? _psComposite;
        private ID3D11PixelShader? _psPassthrough;
        private ID3D11SamplerState? _sampler;
        private ID3D11Buffer? _cbuffer;
        private ID3D11Buffer? _passCbuffer;
        private readonly List<ID3D11PixelShader> _customEffects = [];

        private ID3D11Texture2D? _depthInputTex;
        private ID3D11RenderTargetView? _depthInputRtv;
        private ID3D11ShaderResourceView? _depthInputSrv;
        private ID3D11Texture2D? _aiDepthUpTex;
        private ID3D11RenderTargetView? _aiDepthUpRtv;
        private ID3D11ShaderResourceView? _aiDepthUpSrv;
        private ID3D11Texture2D? _aiDepthTex;
        private ID3D11ShaderResourceView? _aiDepthSrv;
        private readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[StagingCount];
        private readonly bool[] _stagingBusy = new bool[StagingCount];
        private readonly Queue<int> _stagingQueue = new(StagingCount);

        private readonly byte[] _depthReadback = new byte[RiShadeDepth.Size * RiShadeDepth.Size * 4];
        private readonly float[] _depthFloats = new float[RiShadeDepth.Size * RiShadeDepth.Size];
        private readonly RiShadeAntiSmear _antiSmear = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private float _adaptAvg;
        private float _adaptExposure = 1f;
        private Vector3 _planeN = new(0f, 1f, 0f);
        private float _planeD;
        private bool _planeValid;
        private float _depthBaseX;
        private float _depthBaseY;
        private int _aiFeedTick;
        private int _framesSinceFeed;
        private bool _hasFeed;
        private float _velAccumX;
        private float _velAccumY;
        private float _prevFeedAccumX;
        private float _prevFeedAccumY;
        private float _predAccumX;
        private float _predAccumY;
        private int _depthSeenVersion;
        private bool _aiDepthUploaded;
        private bool _lastAiFlag;
        private long _depthUnusedSinceMs;

        private int _width;
        private int _height;
        private int _rw;
        private int _rh;
        private int _builtRenderScaleIndex;
        private int _lastSettingsVersion = -1;
        private Vector4 _lastPassPx = new(-1f);

        private ID3D11PixelShader CreatePs(string entry)
        {
            return _device!.CreatePixelShader(RiShadeShaderCache.Get(RiShadeShaders.Source, entry, "ps_5_0", "RiShade"));
        }

        private void LoadCustomEffects()
        {
            try
            {
                string dir = System.IO.Path.Combine(Paths.RiShade, "Effects");
                System.IO.Directory.CreateDirectory(dir);
                foreach (string file in System.IO.Directory.GetFiles(dir, "*.hlsl"))
                {
                    string name = System.IO.Path.GetFileName(file);
                    try
                    {
                        string source = RiShadeShaders.Source + "\n" + System.IO.File.ReadAllText(file);
                        _customEffects.Add(_device!.CreatePixelShader(RiShadeShaderCache.Get(source, "PSCustom", "ps_5_0", name)));
                        App.Logger.WriteLine(LOG_IDENT, $"Custom effect {name} loaded, entry PSCustom, scene on t0 and AI depth on t1");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Custom effect {name} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Custom effects folder scan failed: " + ex.Message);
            }
        }

        private void CreatePipeline()
        {
            _builtRenderScaleIndex = RiShadeSettings.Current.RenderScaleIndex;
            float renderScale = RiShadeSettings.Current.ResolveRenderScale();
            _rw = Math.Max(64, (int)Math.Round(_width * renderScale));
            _rh = Math.Max(64, (int)Math.Round(_height * renderScale));
            var sw = Stopwatch.StartNew();
            _vs = _device!.CreateVertexShader(RiShadeShaderCache.Get(RiShadeShaders.Source, "VSMain", "vs_5_0", "RiShade"));
            _psMain = CreatePs("PSMain");
            _psDownPrefilter = CreatePs("PSDownsamplePrefilter");
            _psDown = CreatePs("PSDownsample");
            _psUpTent = CreatePs("PSUpsampleTent");
            _psBlurH = CreatePs("PSBlurH");
            _psBlurV = CreatePs("PSBlurV");
            _psBloomCombine = CreatePs("PSBloomCombine");
            _psDepthUp = CreatePs("PSDepthUp");
            _psGi = CreatePs("PSGi");
            _psSsr = CreatePs("PSSsr");
            _psComposite = CreatePs("PSComposite");
            _psPassthrough = CreatePs("PSPassthrough");
            App.Logger.WriteLine(LOG_IDENT, $"Shader pipeline created in {sw.ElapsedMilliseconds}ms");

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
                ByteWidth = (uint)Marshal.SizeOf<RiShadeParams>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            _passCbuffer = _device!.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<Vector4>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            uint ds = (uint)RiShadeDepth.Size;
            _depthInputTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = ds,
                Height = ds,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _depthInputRtv = _device!.CreateRenderTargetView(_depthInputTex);
            _depthInputSrv = _device!.CreateShaderResourceView(_depthInputTex);
            for (int i = 0; i < StagingCount; i++)
            {
                _staging[i] = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = ds,
                    Height = ds,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });
            }
            _aiDepthTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = ds,
                Height = ds,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _aiDepthSrv = _device!.CreateShaderResourceView(_aiDepthTex);

            CreateSizedResources();
        }

        private void CreateSizedResources()
        {
            for (int i = 0; i < _workTex.Length; i++)
            {
                _workRtv[i]?.Dispose();
                _workSrv[i]?.Dispose();
                _workTex[i]?.Dispose();
                var (w, h) = WorkTexSize(i);
                _workTex[i] = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = w,
                    Height = h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R16G16B16A16_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                });
                _workSrv[i] = _device!.CreateShaderResourceView(_workTex[i]!);
                _workRtv[i] = _device!.CreateRenderTargetView(_workTex[i]!);
            }

            _aiDepthUpSrv?.Dispose();
            _aiDepthUpRtv?.Dispose();
            _aiDepthUpTex?.Dispose();
            _aiDepthUpTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)LvlW(1),
                Height = (uint)LvlH(1),
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _aiDepthUpSrv = _device!.CreateShaderResourceView(_aiDepthUpTex);
            _aiDepthUpRtv = _device!.CreateRenderTargetView(_aiDepthUpTex);
            _lastPassPx = new Vector4(-1f);
        }

        private int LvlW(int level) => Math.Max(8, _rw >> level);

        private int LvlH(int level) => Math.Max(8, _rh >> level);

        private (uint, uint) WorkTexSize(int index)
        {
            if (index >= RtDown0 && index < RtUp0)
            {
                int level = index - RtDown0 + 1;
                return ((uint)LvlW(level), (uint)LvlH(level));
            }
            if (index >= RtUp0 && index < RtSceneBlurA)
            {
                int level = index - RtUp0 + 1;
                return ((uint)LvlW(level), (uint)LvlH(level));
            }
            if (index == RtSceneBlurA || index == RtSceneBlurB || index == RtGlossWide || index == RtGlossTemp)
                return ((uint)LvlW(1), (uint)LvlH(1));
            return ((uint)_rw, (uint)_rh);
        }

        private void SetPassPx(int srcW, int srcH, bool upscale = false)
        {
            var v = new Vector4(1f / Math.Max(srcW, 1), 1f / Math.Max(srcH, 1), upscale ? 1f : 0f, 0f);
            if (v == _lastPassPx)
                return;
            _lastPassPx = v;
            _context!.UpdateSubresource(v, _passCbuffer!);
        }

        private void SetVp(int w, int h)
        {
            _context!.RSSetViewport(new Viewport(0, 0, w, h, 0, 1));
        }

        private int FreeStagingSlot()
        {
            for (int i = 0; i < StagingCount; i++)
            {
                if (!_stagingBusy[i])
                    return i;
            }
            return -1;
        }

        private unsafe bool TryReadStaging()
        {
            bool read = false;
            while (_stagingQueue.Count > 0)
            {
                int slot = _stagingQueue.Peek();
                var texture = _staging[slot]!;
                var result = _context!.Map(texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out MappedSubresource mapped);
                if (result == Vortice.DXGI.ResultCode.WasStillDrawing)
                    break;
                _stagingQueue.Dequeue();
                _stagingBusy[slot] = false;
                if (result.Failure)
                    continue;
                try
                {
                    int rowBytes = RiShadeDepth.Size * 4;
                    byte* src = (byte*)mapped.DataPointer;
                    fixed (byte* dst = _depthReadback)
                    {
                        for (int y = 0; y < RiShadeDepth.Size; y++)
                            Buffer.MemoryCopy(src + y * (int)mapped.RowPitch, dst + y * rowBytes, rowBytes, rowBytes);
                    }
                    read = true;
                }
                finally
                {
                    _context.Unmap(texture, 0);
                }
            }
            return read;
        }

        private unsafe void UpdateAiDepth(RiShadeSettings s, ID3D11ShaderResourceView inputSrv)
        {
            RiShadeDepth.EnsureStarted();
            SetPassPx(_width, _height);
            SetVp(LvlW(1), LvlH(1));
            DrawPass(_psDown!, _workRtv[RtDown0]!, inputSrv);
            if (!RiShadeDepth.IsReady)
                return;

            bool doFeed = (_aiFeedTick++ % AiFeedStride) == 0;
            int slot = doFeed ? FreeStagingSlot() : -1;
            if (slot >= 0)
            {
                SetPassPx(LvlW(1), LvlH(1));
                SetVp(LvlW(2), LvlH(2));
                DrawPass(_psDown!, _workRtv[RtDown0 + 1]!, _workSrv[RtDown0]);
                int depthSrc = RtDown0 + 1;
                int depthSrcLvl = 2;
                if (LvlW(2) > 700)
                {
                    SetPassPx(LvlW(2), LvlH(2));
                    SetVp(LvlW(3), LvlH(3));
                    DrawPass(_psDown!, _workRtv[RtDown0 + 2]!, _workSrv[RtDown0 + 1]);
                    depthSrc = RtDown0 + 2;
                    depthSrcLvl = 3;
                }
                SetPassPx(LvlW(depthSrcLvl), LvlH(depthSrcLvl));
                SetVp(RiShadeDepth.Size, RiShadeDepth.Size);
                DrawPass(_psDown!, _depthInputRtv!, _workSrv[depthSrc]);
                _context!.CopyResource(_staging[slot]!, _depthInputTex!);
                _stagingBusy[slot] = true;
                _stagingQueue.Enqueue(slot);
            }

            if (TryReadStaging())
            {
                _antiSmear.Analyze(_depthReadback);
                float feedAx = _antiSmear.AccumX;
                float feedAy = _antiSmear.AccumY;
                if (_hasFeed)
                {
                    int span = Math.Max(1, _framesSinceFeed);
                    _velAccumX = _velAccumX * 0.5f + ((feedAx - _prevFeedAccumX) / span) * 0.5f;
                    _velAccumY = _velAccumY * 0.5f + ((feedAy - _prevFeedAccumY) / span) * 0.5f;
                }
                _hasFeed = true;
                _prevFeedAccumX = feedAx;
                _prevFeedAccumY = feedAy;
                _framesSinceFeed = 0;
                RiShadeDepth.SubmitFrame(_depthReadback, feedAx, feedAy);
                if (s.EyeAdaptEnabled)
                    UpdateAdaptExposure(s);
                else
                    _adaptExposure = 1f;
            }

            _framesSinceFeed++;
            _predAccumX = _antiSmear.AccumX + _velAccumX * (_framesSinceFeed + DepthPredictLead);
            _predAccumY = _antiSmear.AccumY + _velAccumY * (_framesSinceFeed + DepthPredictLead);

            if (RiShadeDepth.TryGetDepth(ref _depthSeenVersion, _depthFloats, out float tagX, out float tagY))
            {
                fixed (float* p = _depthFloats)
                {
                    _context!.UpdateSubresource(_aiDepthTex!, 0, null, (IntPtr)p, (uint)(RiShadeDepth.Size * 4), 0);
                }
                if (!_aiDepthUploaded)
                {
                    _aiDepthUploaded = true;
                    App.Logger.WriteLine(LOG_IDENT, "AI depth map is live in the shader pipeline");
                }
                _depthBaseX = tagX;
                _depthBaseY = tagY;
                if (s.SsrEnabled)
                    FitFloorPlane();
            }

            if (_aiDepthUploaded)
            {
                SetPassPx(LvlW(1), LvlH(1));
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psDepthUp!, _aiDepthUpRtv!, _workSrv[RtDown0], _aiDepthSrv, _depthInputSrv);
            }
        }

        private void FitFloorPlane()
        {
            int size = RiShadeDepth.Size;
            float aspect = (float)_width / Math.Max(_height, 1);
            const float tanHalf = 0.7f;
            int yStart = (int)(size * 0.40f);
            double a = 0, b = 0, c = 0;
            bool haveFit = false;
            for (int round = 0; round < 2; round++)
            {
                double sxx = 0, sxz = 0, sx = 0, szz = 0, sz = 0, sw = 0, sxy = 0, szy = 0, sy = 0;
                for (int py = yStart; py < size; py += 3)
                {
                    float gy = 1f - (py + 0.5f) / size;
                    for (int px = 0; px < size; px += 3)
                    {
                        float disp = _depthFloats[py * size + px];
                        float z = 1f / (disp * 3f + 0.25f);
                        float gx = (px + 0.5f) / size;
                        double X = (gx * 2f - 1f) * tanHalf * aspect * z;
                        double Y = (gy * 2f - 1f) * tanHalf * z;
                        double w = 1.0;
                        if (haveFit)
                        {
                            double r = Y - (a * X + b * z + c);
                            w = 1.0 / (1.0 + (r * r) / 0.0064);
                        }
                        sxx += w * X * X; sxz += w * X * z; sx += w * X;
                        szz += w * z * z; sz += w * z; sw += w;
                        sxy += w * X * Y; szy += w * z * Y; sy += w * Y;
                    }
                }
                double det = sxx * (szz * sw - sz * sz) - sxz * (sxz * sw - sz * sx) + sx * (sxz * sz - szz * sx);
                if (Math.Abs(det) < 1e-9 || sw < 200)
                {
                    _planeValid = false;
                    return;
                }
                double detA = sxy * (szz * sw - sz * sz) - sxz * (szy * sw - sz * sy) + sx * (szy * sz - szz * sy);
                double detB = sxx * (szy * sw - sy * sz) - sxy * (sxz * sw - sz * sx) + sx * (sxz * sy - szy * sx);
                double detC = sxx * (szz * sy - sz * szy) - sxz * (sxz * sy - szy * sx) + sxy * (sxz * sz - szz * sx);
                a = detA / det;
                b = detB / det;
                c = detC / det;
                haveFit = true;
            }
            float nx = (float)(-a), ny = 1f, nz = (float)(-b);
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            var n = new Vector3(nx / len, ny / len, nz / len);
            float d = (float)c / len;
            if (n.Y < 0.55f)
            {
                _planeValid = false;
                return;
            }
            if (!_planeValid)
            {
                _planeN = n;
                _planeD = d;
                _planeValid = true;
                return;
            }
            float dev = 1f - Math.Clamp(Vector3.Dot(n, _planeN), -1f, 1f);
            float ddev = Math.Abs(d - _planeD);
            float pa;
            if (dev < 0.02f && ddev < 0.02f)
                pa = 0.05f;
            else if (dev < 0.08f && ddev < 0.06f)
                pa = 0.15f;
            else
                pa = 0.30f;
            _planeN = Vector3.Normalize(_planeN + (n - _planeN) * pa);
            _planeD += (d - _planeD) * pa;
        }

        private void UpdateAdaptExposure(RiShadeSettings s)
        {
            float sum = 0f;
            int n = 0;
            for (int i = 0; i + 3 < _depthReadback.Length; i += 64)
            {
                sum += _depthReadback[i + 2] * 0.299f + _depthReadback[i + 1] * 0.587f + _depthReadback[i] * 0.114f;
                n++;
            }
            float avg = sum / (n * 255f);
            if (_adaptAvg <= 0f)
                _adaptAvg = avg;
            else
                _adaptAvg += (avg - _adaptAvg) * 0.04f;
            float ratio = 0.42f / Math.Max(_adaptAvg, 0.06f);
            float exp = 1f + (ratio - 1f) * Math.Clamp(s.EyeAdaptStrength, 0f, 1f);
            _adaptExposure = Math.Clamp(exp, 0.6f, 1.7f);
        }

        private void UpdateParamsIfNeeded(RiShadeSettings s)
        {
            int version = RiShadeSettings.Version;
            bool aiFlag = _aiDepthUploaded;
            bool needsTime = s.GrainEnabled || s.EyeAdaptEnabled || _aiDepthUploaded;
            if (version == _lastSettingsVersion && !needsTime && aiFlag == _lastAiFlag)
                return;
            _lastSettingsVersion = version;
            _lastAiFlag = aiFlag;

            float[] temp = s.ResolveColorTemp();
            var p = new RiShadeParams
            {
                PA = new Vector4(s.GradeEnabled ? 1f : 0f, 1f, 1f, s.Brightness),
                PB = new Vector4(s.Gamma, s.HueShift, (float)_clock.Elapsed.TotalSeconds, s.ChromaEnabled ? 1f : 0f),
                PC = new Vector4(s.Lift[0], s.Lift[1], s.Lift[2], s.TonemapEnabled ? 1f : 0f),
                PD = new Vector4(s.Gain[0], s.Gain[1], s.Gain[2], s.TonemapMode),
                PE = new Vector4(s.ColorBalance[0], s.ColorBalance[1], s.ColorBalance[2], s.TonemapExposure),
                PF = new Vector4(temp[0], temp[1], temp[2], s.TonemapWhitepoint),
                PG = new Vector4(s.VignetteEnabled ? 1f : 0f, s.VignetteStrength, s.VignetteFeather, s.VignetteCenterX),
                PH = new Vector4(s.VignetteCenterY, s.SharpenEnabled ? 1f : 0f, s.SharpenStrength, s.SharpenRadius),
                PI = new Vector4(s.SharpenClamp, s.ChromaStrength, s.ChromaRadial ? 1f : 0f, s.GrainEnabled ? 1f : 0f),
                PJ = new Vector4(s.GrainStrength, s.GrainSize, s.GrainColored ? 1f : 0f, s.DofEnabled ? 1f : 0f),
                PK = new Vector4(s.DofStrength, s.DofFocusRange, s.DofFeather, s.AoEnabled ? 1f : 0f),
                PL = new Vector4(s.AoStrength, s.AoRadius, s.ResolveAoSamples(), _rw),
                PM = new Vector4(_rh, s.BloomStrength, s.BloomThreshold, s.BloomRadius),
                PN = new Vector4(s.BloomTint[0], s.BloomTint[1], s.BloomTint[2], s.SsrIntensity),
                PO = new Vector4(s.SsrGlossiness, s.SsrReflectivity, s.SsrDistance, s.ClarityStrength),
                PP = new Vector4(s.DebandEnabled ? 1f : 0f, s.DebandStrength, s.GiStrength, s.GiRadius),
                PQ = new Vector4(s.FogStrength, s.FogStart, s.FogBrightness, s.AmbientStrength),
                PR = new Vector4(s.EyeAdaptEnabled ? _adaptExposure : 1f, _planeN.X, _planeN.Y, _planeN.Z),
                PS = new Vector4(s.SsrSheen, Math.Clamp((_predAccumX - _depthBaseX) / RiShadeDepth.Size, -0.25f, 0.25f), Math.Clamp(-(_predAccumY - _depthBaseY) / RiShadeDepth.Size, -0.25f, 0.25f), 0f),
                PT = new Vector4(_planeD, aiFlag ? 1f : 0f, s.DebugView, _planeValid ? 1f : 0f),
            };
            _context!.UpdateSubresource(p, _cbuffer!);
        }

        private void DrawPass(ID3D11PixelShader ps, ID3D11RenderTargetView target, ID3D11ShaderResourceView? t0, ID3D11ShaderResourceView? t1 = null, ID3D11ShaderResourceView? t2 = null, ID3D11ShaderResourceView? t3 = null)
        {
            _context!.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(target);
            _context.PSSetShader(ps);
            _context.PSSetShaderResource(0, t0!);
            if (t1 != null) _context.PSSetShaderResource(1, t1);
            if (t2 != null) _context.PSSetShaderResource(2, t2);
            if (t3 != null) _context.PSSetShaderResource(3, t3);
            _context.Draw(3, 0);
        }

        private void RebuildForScaleIfNeeded(RiShadeSettings s)
        {
            if (s.RenderScaleIndex == _builtRenderScaleIndex)
                return;
            _builtRenderScaleIndex = s.RenderScaleIndex;
            float scale = s.ResolveRenderScale();
            _rw = Math.Max(64, (int)Math.Round(_width * scale));
            _rh = Math.Max(64, (int)Math.Round(_height * scale));
            try
            {
                CreateSizedResources();
                _lastSettingsVersion = -1;
                App.Logger.WriteLine(LOG_IDENT, $"Render resolution changed live to {_rw}x{_rh}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeOverlay::RebuildForScale", ex);
            }
        }

        private void TrackDepthUse(bool needsDepth)
        {
            if (needsDepth || !RiShadeDepth.IsActive)
            {
                _depthUnusedSinceMs = 0;
                return;
            }
            long now = Environment.TickCount64;
            if (_depthUnusedSinceMs == 0)
            {
                _depthUnusedSinceMs = now;
                return;
            }
            if (now - _depthUnusedSinceMs < DepthIdleReleaseMs)
                return;
            _depthUnusedSinceMs = 0;
            App.Logger.WriteLine(LOG_IDENT, "No active effect uses AI depth, releasing the AI model");
            RiShadeDepth.Shutdown(wait: false);
            ResetDepthState();
        }

        private void RenderPasses(RiShadeSettings s, ID3D11ShaderResourceView inputSrv, ID3D11RenderTargetView dst)
        {
            _context!.VSSetShader(_vs);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetConstantBuffer(1, _passCbuffer);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            bool needsDepth = s.NeedsDepth;
            TrackDepthUse(needsDepth);
            if (!s.HasVisibleEffects)
            {
                SetPassPx(_width, _height);
                SetVp(_width, _height);
                DrawPass(_psPassthrough!, dst, inputSrv);
                _context.PSSetShaderResources(0, _nullSrvs);
                return;
            }
            if (needsDepth)
                UpdateAiDepth(s, inputSrv);
            UpdateParamsIfNeeded(s);

            var depthSrv = _aiDepthUploaded ? _aiDepthUpSrv : _aiDepthSrv;
            if (s.ClarityStrength > 0f || s.AmbientStrength > 0f)
            {
                if (!needsDepth)
                {
                    SetPassPx(_width, _height);
                    SetVp(LvlW(1), LvlH(1));
                    DrawPass(_psDown!, _workRtv[RtDown0]!, inputSrv);
                }
                SetPassPx(LvlW(1), LvlH(1));
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psBlurH!, _workRtv[RtSceneBlurB]!, _workSrv[RtDown0]);
                DrawPass(_psBlurV!, _workRtv[RtSceneBlurA]!, _workSrv[RtSceneBlurB]);
            }
            if (s.GiStrength > 0f && _aiDepthUploaded)
            {
                SetPassPx(LvlW(1), LvlH(1));
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psGi!, _workRtv[RtUp0]!, _workSrv[RtDown0], depthSrv);
            }
            SetVp(_rw, _rh);
            DrawPass(_psMain!, _workRtv[RtA]!, inputSrv, depthSrv, _workSrv[RtSceneBlurA], _workSrv[RtUp0]);
            int scene = RtA;

            if (s.BloomEnabled && !s.PerfMode)
            {
                int levels = Math.Clamp(s.BloomPasses + 1, 2, 5);
                SetPassPx(_rw, _rh);
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psDownPrefilter!, _workRtv[RtDown0]!, _workSrv[scene]);
                for (int i = 1; i < levels; i++)
                {
                    SetPassPx(LvlW(i), LvlH(i));
                    SetVp(LvlW(i + 1), LvlH(i + 1));
                    DrawPass(_psDown!, _workRtv[RtDown0 + i]!, _workSrv[RtDown0 + i - 1]);
                }
                int src = RtDown0 + levels - 1;
                int srcLevel = levels;
                for (int i = levels - 2; i >= 0; i--)
                {
                    SetPassPx(LvlW(srcLevel), LvlH(srcLevel));
                    SetVp(LvlW(i + 1), LvlH(i + 1));
                    DrawPass(_psUpTent!, _workRtv[RtUp0 + i]!, _workSrv[src], _workSrv[RtDown0 + i]);
                    src = RtUp0 + i;
                    srcLevel = i + 1;
                }
                SetVp(_rw, _rh);
                DrawPass(_psBloomCombine!, _workRtv[RtB]!, _workSrv[scene], _workSrv[src]);
                scene = RtB;
            }

            if (s.SsrEnabled && !s.PerfMode)
            {
                SetPassPx(_rw, _rh);
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psDown!, _workRtv[RtSceneBlurA]!, _workSrv[scene]);
                SetPassPx(LvlW(1), LvlH(1));
                DrawPass(_psBlurH!, _workRtv[RtSceneBlurB]!, _workSrv[RtSceneBlurA]);
                DrawPass(_psBlurV!, _workRtv[RtSceneBlurA]!, _workSrv[RtSceneBlurB]);
                SetPassPx(LvlW(1) / 2, LvlH(1) / 2);
                DrawPass(_psBlurH!, _workRtv[RtGlossTemp]!, _workSrv[RtSceneBlurA]);
                DrawPass(_psBlurV!, _workRtv[RtGlossWide]!, _workSrv[RtGlossTemp]);
                DrawPass(_psSsr!, _workRtv[RtSceneBlurB]!, _workSrv[scene], _workSrv[RtGlossWide], _workSrv[RtSceneBlurA], depthSrv);
                SetVp(_rw, _rh);
                int other = scene == RtA ? RtB : RtA;
                DrawPass(_psComposite!, _workRtv[other]!, _workSrv[scene], _workSrv[RtSceneBlurB]);
                scene = other;
            }

            foreach (var custom in _customEffects)
            {
                int next = scene == RtA ? RtB : RtA;
                DrawPass(custom, _workRtv[next]!, _workSrv[scene], depthSrv);
                scene = next;
            }

            SetPassPx(_rw, _rh, _rw < _width || _rh < _height);
            SetVp(_width, _height);
            DrawPass(_psPassthrough!, dst, _workSrv[scene], depthSrv);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        public void AttachExternal(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
        {
            _device = device;
            _context = context;
            _width = Math.Max(16, width);
            _height = Math.Max(16, height);
            ResetDepthState();
            CreatePipeline();
            LoadCustomEffects();
            _lastSettingsVersion = -1;
            App.Logger.WriteLine(LOG_IDENT, $"Attached as a composited stage at {_width}x{_height}, render {_rw}x{_rh}");
        }

        public void EnsureExternalSize(int width, int height)
        {
            width = Math.Max(16, width);
            height = Math.Max(16, height);
            if (width == _width && height == _height)
                return;
            _width = width;
            _height = height;
            float scale = RiShadeSettings.Current.ResolveRenderScale();
            _rw = Math.Max(64, (int)Math.Round(_width * scale));
            _rh = Math.Max(64, (int)Math.Round(_height * scale));
            CreateSizedResources();
            _lastSettingsVersion = -1;
        }

        public void RenderInto(ID3D11ShaderResourceView input, ID3D11RenderTargetView output, int width, int height)
        {
            RiShadeSettings s = RiShadeSettings.Current;
            EnsureExternalSize(width, height);
            RebuildForScaleIfNeeded(s);
            RenderPasses(s, input, output);
        }

        private void ResetDepthState()
        {
            _antiSmear.Reset();
            _aiFeedTick = 0;
            _framesSinceFeed = 0;
            _hasFeed = false;
            _velAccumX = 0f;
            _velAccumY = 0f;
            _prevFeedAccumX = 0f;
            _prevFeedAccumY = 0f;
            _predAccumX = 0f;
            _predAccumY = 0f;
            _depthBaseX = 0f;
            _depthBaseY = 0f;
            _depthSeenVersion = RiShadeDepth.DepthVersion;
            _aiDepthUploaded = false;
            _planeValid = false;
            _planeN = new Vector3(0f, 1f, 0f);
            _planeD = 0f;
            _adaptAvg = 0f;
            _adaptExposure = 1f;
            _depthUnusedSinceMs = 0;
            _stagingQueue.Clear();
            Array.Clear(_stagingBusy);
        }

        public void DisposeExternal(bool stopDepth)
        {
            try
            {
                if (stopDepth)
                    RiShadeDepth.Shutdown(wait: false);
                _context?.ClearState();
                for (int i = 0; i < _workTex.Length; i++)
                {
                    _workRtv[i]?.Dispose();
                    _workSrv[i]?.Dispose();
                    _workTex[i]?.Dispose();
                    _workRtv[i] = null;
                    _workSrv[i] = null;
                    _workTex[i] = null;
                }
                for (int i = 0; i < StagingCount; i++)
                {
                    _staging[i]?.Dispose();
                    _staging[i] = null;
                }
                _aiDepthSrv?.Dispose();
                _aiDepthTex?.Dispose();
                _aiDepthUpSrv?.Dispose();
                _aiDepthUpRtv?.Dispose();
                _aiDepthUpTex?.Dispose();
                _depthInputRtv?.Dispose();
                _depthInputSrv?.Dispose();
                _depthInputTex?.Dispose();
                foreach (var custom in _customEffects)
                    custom.Dispose();
                _customEffects.Clear();
                _psMain?.Dispose();
                _psDownPrefilter?.Dispose();
                _psDown?.Dispose();
                _psUpTent?.Dispose();
                _psBlurH?.Dispose();
                _psBlurV?.Dispose();
                _psBloomCombine?.Dispose();
                _psDepthUp?.Dispose();
                _psGi?.Dispose();
                _psSsr?.Dispose();
                _psComposite?.Dispose();
                _psPassthrough?.Dispose();
                _sampler?.Dispose();
                _cbuffer?.Dispose();
                _passCbuffer?.Dispose();
                _vs?.Dispose();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeOverlay::DisposeExternal", ex);
            }
            finally
            {
                _aiDepthSrv = null;
                _aiDepthTex = null;
                _aiDepthUpSrv = null;
                _aiDepthUpRtv = null;
                _aiDepthUpTex = null;
                _depthInputRtv = null;
                _depthInputSrv = null;
                _depthInputTex = null;
                _psMain = null;
                _psDownPrefilter = null;
                _psDown = null;
                _psUpTent = null;
                _psBlurH = null;
                _psBlurV = null;
                _psBloomCombine = null;
                _psDepthUp = null;
                _psGi = null;
                _psSsr = null;
                _psComposite = null;
                _psPassthrough = null;
                _sampler = null;
                _cbuffer = null;
                _passCbuffer = null;
                _vs = null;
                ResetDepthState();
            }
        }
    }
}
