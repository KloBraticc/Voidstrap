using System;
using System.Runtime.InteropServices;
using System.Windows;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.Utility
{
    public static class ScreenColorEffect
    {
        public enum ColorBlindnessType
        {
            Protanopia,
            Deuteranopia,
            Tritanopia
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MAGCOLOREFFECT
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
            public float[] transform;
        }

        [DllImport("Magnification.dll", SetLastError = true)]
        private static extern bool MagInitialize();

        [DllImport("Magnification.dll")]
        private static extern bool MagUninitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT effect);

        private static readonly object Sync = new object();
        private static bool _initialized;
        private static bool _hooked;
        private static IDisposable? _trackerLease;
        private static bool _pushed;
        private static bool _failed;
        private static float[]? _desired;

        private static readonly float[] Identity =
        {
            1f, 0f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
            0f, 0f, 0f, 0f, 1f
        };

        private static readonly double[,] Rgb2Lms =
        {
            { 0.390360, 0.549759, 0.008881 },
            { 0.070925, 0.963107, 0.001368 },
            { 0.023142, 0.128012, 0.936051 }
        };

        private static readonly double[,] Lms2Rgb = Invert33(Rgb2Lms);

        private static readonly double[] LmsWhite = Mul33Vec(Rgb2Lms, new double[] { 1, 1, 1 });
        private static readonly double[] LmsBlue = Mul33Vec(Rgb2Lms, new double[] { 0, 0, 1 });
        private static readonly double[] LmsRed = Mul33Vec(Rgb2Lms, new double[] { 1, 0, 0 });

        private static readonly double[] P0 = Cross(LmsWhite, LmsBlue);
        private static readonly double[] P1 = Cross(LmsWhite, LmsRed);

        public static void ApplyConfigured()
        {
            Apply(
                App.Settings.Prop.Saturation,
                App.Settings.Prop.Contrast,
                App.Settings.Prop.ColorTemperature,
                App.Settings.Prop.ColorBlindnessEnabled,
                (ColorBlindnessType)App.Settings.Prop.ColorBlindnessType,
                App.Settings.Prop.ColorBlindnessSeverity / 100.0,
                App.Settings.Prop.ColorBlindnessSimulate);
        }

        private static readonly double[,] ProtoSpread = new double[,]
        {
            { 1.0, 0.7, 0.7 },
            { 0.0, 1.0, 0.0 },
            { 0.0, 0.0, 1.0 }
        };

        private static readonly double[,] DeuteranSpread = new double[,]
        {
            { 1.0, 0.0, 0.0 },
            { 0.7, 1.0, 0.7 },
            { 0.0, 0.0, 1.0 }
        };

        private static readonly double[,] TritanSpread = new double[,]
        {
            { 1.0, 0.0, 0.0 },
            { 0.0, 1.0, 0.0 },
            { 0.7, 0.7, 1.0 }
        };

        public static void Apply(double saturation, double contrast, double colorTemperature,
            bool cbEnabled = false, ColorBlindnessType cbType = ColorBlindnessType.Deuteranopia,
            double cbSeverity = 1.0, bool cbSimulate = false)
        {
            float sat = (float)Math.Clamp(saturation / 100.0, 0.0, 2.0);
            float con = (float)Math.Clamp(contrast / 100.0, 0.0, 2.0);
            double temp = Math.Clamp(colorTemperature / 100.0, -1.0, 1.0);

            bool neutral = Math.Abs(sat - 1f) < 0.001f && Math.Abs(con - 1f) < 0.001f
                && Math.Abs(temp) < 0.001 && !cbEnabled;

            float[]? matrix = null;
            if (!neutral)
            {
                matrix = Multiply(
                    Multiply(SaturationMatrix(sat), ContrastMatrix(con)),
                    TemperatureMatrix(temp));

                if (cbEnabled)
                    matrix = Multiply(matrix, BuildCbMatrix(cbType, Math.Clamp(cbSeverity, 0.0, 1.0), cbSimulate));
            }

            OnUi(delegate
            {
                lock (Sync)
                {
                    _desired = matrix;
                    if (matrix == null)
                    {
                        Unhook();
                        Push();
                        return;
                    }
                    if (!EnsureInitialized())
                        return;
                    Hook();
                    Push();
                }
            });
        }

        public static void Reset()
        {
            OnUi(delegate
            {
                lock (Sync)
                {
                    _desired = null;
                    Unhook();
                    Push();
                }
            });
        }

        public static void Shutdown()
        {
            OnUi(delegate
            {
                lock (Sync)
                {
                    _desired = null;
                    Unhook();
                    Push();
                    if (_initialized)
                    {
                        try { MagUninitialize(); } catch { }
                        _initialized = false;
                    }
                }
            });
        }

        private static void OnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished || dispatcher.CheckAccess())
            {
                action();
                return;
            }
            dispatcher.BeginInvoke(action);
        }

        private static void Push()
        {
            if (!_initialized || _failed)
                return;

            bool wanted = _desired != null && RobloxWindowTracker.Current.Valid && RobloxWindowTracker.Current.Foreground;
            if (!wanted && !_pushed)
                return;

            float[] transform = wanted ? _desired! : Identity;
            MAGCOLOREFFECT effect = new MAGCOLOREFFECT { transform = transform };
            if (!MagSetFullscreenColorEffect(ref effect))
            {
                _failed = true;
                App.Logger?.WriteLine("ScreenColorEffect", "MagSetFullscreenColorEffect failed, color effects unavailable. Error: " + Marshal.GetLastWin32Error());
                return;
            }
            _pushed = wanted;
        }

        private static bool EnsureInitialized()
        {
            if (_initialized)
                return true;
            try
            {
                _initialized = MagInitialize();
                if (!_initialized)
                    App.Logger?.WriteLine("ScreenColorEffect", "MagInitialize failed. Error: " + Marshal.GetLastWin32Error());
            }
            catch
            {
                _initialized = false;
            }
            return _initialized;
        }

        private static void Hook()
        {
            if (_hooked)
                return;
            RobloxWindowTracker.Changed += OnRobloxWindowChanged;
            _trackerLease = RobloxWindowTracker.Acquire();
            _hooked = true;
        }

        private static void Unhook()
        {
            if (!_hooked)
                return;
            RobloxWindowTracker.Changed -= OnRobloxWindowChanged;
            _trackerLease?.Dispose();
            _trackerLease = null;
            _hooked = false;
        }

        private static void OnRobloxWindowChanged(object? sender, RobloxWindowRect rect)
        {
            lock (Sync)
            {
                Push();
            }
        }

        private static float[] SaturationMatrix(float s)
        {
            const float lumR = 0.3086f;
            const float lumG = 0.6094f;
            const float lumB = 0.0820f;
            float inv = 1f - s;
            return new float[]
            {
                inv * lumR + s, inv * lumR,     inv * lumR,     0f, 0f,
                inv * lumG,     inv * lumG + s, inv * lumG,     0f, 0f,
                inv * lumB,     inv * lumB,     inv * lumB + s, 0f, 0f,
                0f,             0f,             0f,             1f, 0f,
                0f,             0f,             0f,             0f, 1f
            };
        }

        private static float[] ContrastMatrix(float c)
        {
            float t = 0.5f * (1f - c);
            return new float[]
            {
                c,  0f, 0f, 0f, 0f,
                0f, c,  0f, 0f, 0f,
                0f, 0f, c,  0f, 0f,
                0f, 0f, 0f, 1f, 0f,
                t,  t,  t,  0f, 1f
            };
        }

        private static float[] TemperatureMatrix(double temp)
        {
            float r = 1f + (float)(temp * 0.2);
            float b = 1f - (float)(temp * 0.2);
            return new float[]
            {
                r,  0f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f, 0f,
                0f, 0f, b,  0f, 0f,
                0f, 0f, 0f, 1f, 0f,
                0f, 0f, 0f, 0f, 1f
            };
        }

        private static float[] Multiply(float[] a, float[] b)
        {
            float[] result = new float[25];
            for (int row = 0; row < 5; row++)
            {
                for (int col = 0; col < 5; col++)
                {
                    float sum = 0f;
                    for (int k = 0; k < 5; k++)
                        sum += a[row * 5 + k] * b[k * 5 + col];
                    result[row * 5 + col] = sum;
                }
            }
            return result;
        }

        private static float[] BuildCbMatrix(ColorBlindnessType type, double severity, bool simulate)
        {
            double[,] sim = BuildSimulation(type);
            double[,] spread = GetSpread(type);

            double[,] simRgb2Lms = Mul33(sim, Rgb2Lms);
            double[,] diff = Sub33(Rgb2Lms, simRgb2Lms);
            double[,] spreadDiff = Mul33(spread, diff);
            double[,] sum = Add33(simRgb2Lms, spreadDiff);
            double[,] inner = Mul33(sim, sum);
            double[,] mat33 = Mul33(Lms2Rgb, inner);

            if (simulate)
                mat33 = Mul33(Mul33(Lms2Rgb, sim), Rgb2Lms);

            if (severity < 1.0)
                mat33 = LerpIdentity(mat33, severity);

            return To55f(mat33);
        }

        private static double[,] BuildSimulation(ColorBlindnessType type)
        {
            switch (type)
            {
                case ColorBlindnessType.Protanopia:
                    return new double[,]
                    {
                        { 0.0,         -P0[1] / P0[0], -P0[2] / P0[0] },
                        { 0.0,          1.0,            0.0            },
                        { 0.0,          0.0,            1.0            }
                    };
                case ColorBlindnessType.Deuteranopia:
                    return new double[,]
                    {
                        { 1.0,          -P0[0] / P0[1], 0.0            },
                        { 0.0,          0.0,            0.0            },
                        { 0.0,          -P0[2] / P0[1], 1.0            }
                    };
                case ColorBlindnessType.Tritanopia:
                    return new double[,]
                    {
                        { 1.0,          0.0,            -P1[0] / P1[2] },
                        { 0.0,          1.0,            -P1[1] / P1[2] },
                        { 0.0,          0.0,            0.0            }
                    };
                default:
                    return Identity33();
            }
        }

        private static double[,] GetSpread(ColorBlindnessType type)
        {
            switch (type)
            {
                case ColorBlindnessType.Protanopia: return ProtoSpread;
                case ColorBlindnessType.Deuteranopia: return DeuteranSpread;
                case ColorBlindnessType.Tritanopia: return TritanSpread;
                default: return Identity33();
            }
        }

        private static double[] Cross(double[] a, double[] b)
        {
            return new double[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0]
            };
        }

        private static double[] Mul33Vec(double[,] m, double[] v)
        {
            return new double[]
            {
                m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
                m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
                m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2]
            };
        }

        private static double[,] Mul33(double[,] a, double[,] b)
        {
            double[,] r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        r[i, j] += a[i, k] * b[k, j];
            return r;
        }

        private static double[,] Add33(double[,] a, double[,] b)
        {
            double[,] r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i, j] = a[i, j] + b[i, j];
            return r;
        }

        private static double[,] Sub33(double[,] a, double[,] b)
        {
            double[,] r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i, j] = a[i, j] - b[i, j];
            return r;
        }

        private static double[,] LerpIdentity(double[,] m, double t)
        {
            double[,] r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i, j] = (i == j ? 1.0 : 0.0) * (1.0 - t) + m[i, j] * t;
            return r;
        }

        private static double[,] Identity33()
        {
            return new double[,]
            {
                { 1.0, 0.0, 0.0 },
                { 0.0, 1.0, 0.0 },
                { 0.0, 0.0, 1.0 }
            };
        }

        private static double[,] Invert33(double[,] m)
        {
            double a = m[0, 0], b = m[0, 1], c = m[0, 2];
            double d = m[1, 0], e = m[1, 1], f = m[1, 2];
            double g = m[2, 0], h = m[2, 1], i = m[2, 2];

            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

            double invDet = 1.0 / det;

            return new double[,]
            {
                {  (e * i - f * h) * invDet, -(b * i - c * h) * invDet,  (b * f - c * e) * invDet },
                { -(d * i - f * g) * invDet,  (a * i - c * g) * invDet, -(a * f - c * d) * invDet },
                {  (d * h - e * g) * invDet, -(a * h - b * g) * invDet,  (a * e - b * d) * invDet }
            };
        }

        private static float[] To55f(double[,] m33)
        {
            float[] result = new float[25];
            for (int i = 0; i < 25; i++)
                result[i] = Identity[i];

            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    result[r * 5 + c] = (float)m33[c, r];

            return result;
        }
    }
}
