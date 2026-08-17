using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Voidstrap.Integrations.Overlays
{
    internal sealed class OverlayCrosshair
    {
        public const int TexWidth = 128;
        public const int TexHeight = 128;

        private ID3D11Device _device = null!;
        private ID3D11Texture2D? _tex;
        private ID3D11ShaderResourceView? _srv;
        private Bitmap? _bitmap;
        private Graphics? _graphics;
        private string _last = "";

        public ID3D11ShaderResourceView? Srv => _srv;

        public void Init(ID3D11Device device)
        {
            Dispose();
            _device = device;
            _tex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = TexWidth,
                Height = TexHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write,
            });
            _srv = _device.CreateShaderResourceView(_tex);
            _bitmap = new Bitmap(TexWidth, TexHeight, PixelFormat.Format32bppArgb);
            _graphics = Graphics.FromImage(_bitmap);
            _graphics.SmoothingMode = SmoothingMode.AntiAlias;
            _last = "";
        }

        public static bool IsEnabled()
        {
            try
            {
                return OverlayHub.InGame && App.Settings?.Prop?.Crosshair == true && App.Settings.Prop.CrosshairShapeIndex != 3;
            }
            catch
            {
                return false;
            }
        }

        public void Update(ID3D11DeviceContext context)
        {
            if (_tex == null || _bitmap == null || _graphics == null)
                return;

            var prop = App.Settings.Prop;
            int shape = Math.Clamp(prop.CrosshairShapeIndex, 0, 2);
            int size = Math.Clamp(prop.CrosshairSize, 2, 60);
            int thickness = Math.Clamp(prop.CrosshairLineThickness, 1, 16);
            int gap = Math.Clamp(prop.CrosshairGap, 0, 40);
            double opacity = Math.Clamp(prop.CrosshairOpacity, 0.05, 1.0);
            string signature = $"{shape}|{size}|{thickness}|{gap}|{opacity:0.00}|{prop.CrosshairColorHex}|{prop.CrosshairOutlineColorHex}";
            if (signature == _last)
                return;
            _last = signature;

            Color fill = ParseColor(prop.CrosshairColorHex, Color.Lime, opacity);
            Color outline = ParseColor(prop.CrosshairOutlineColorHex, Color.Black, opacity);

            _graphics.Clear(Color.Transparent);
            using (var fillBrush = new SolidBrush(fill))
            using (var outlinePen = new Pen(outline, Math.Max(1f, thickness * 0.5f)))
            using (var fillPen = new Pen(fill, thickness))
            {
                float cx = TexWidth / 2f;
                float cy = TexHeight / 2f;
                if (shape == 0)
                {
                    float inner = gap;
                    float outer = gap + size;
                    DrawArm(outlinePen, fillPen, cx, cy - inner, cx, cy - outer);
                    DrawArm(outlinePen, fillPen, cx, cy + inner, cx, cy + outer);
                    DrawArm(outlinePen, fillPen, cx - inner, cy, cx - outer, cy);
                    DrawArm(outlinePen, fillPen, cx + inner, cy, cx + outer, cy);
                }
                else if (shape == 1)
                {
                    float r = Math.Max(1f, size * 0.5f);
                    _graphics.FillEllipse(fillBrush, cx - r, cy - r, r * 2f, r * 2f);
                    _graphics.DrawEllipse(outlinePen, cx - r, cy - r, r * 2f, r * 2f);
                }
                else
                {
                    float r = Math.Max(1f, size * 0.5f);
                    _graphics.DrawEllipse(outlinePen, cx - r, cy - r, r * 2f, r * 2f);
                    _graphics.DrawEllipse(fillPen, cx - r, cy - r, r * 2f, r * 2f);
                }
            }

            Upload(context);
        }

        private void DrawArm(Pen outlinePen, Pen fillPen, float x1, float y1, float x2, float y2)
        {
            _graphics!.DrawLine(outlinePen, x1, y1, x2, y2);
            _graphics.DrawLine(fillPen, x1, y1, x2, y2);
        }

        private static Color ParseColor(string? hex, Color fallback, double opacity)
        {
            Color parsed = fallback;
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                {
                    string trimmed = hex.TrimStart('#');
                    if (trimmed.Length == 6 && int.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                        parsed = Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                    else if (trimmed.Length == 8 && uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
                        parsed = Color.FromArgb((int)((argb >> 24) & 0xFF), (int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
                }
            }
            catch
            {
                parsed = fallback;
            }
            int alpha = (int)Math.Round(parsed.A * opacity);
            return Color.FromArgb(Math.Clamp(alpha, 0, 255), parsed.R, parsed.G, parsed.B);
        }

        private void Upload(ID3D11DeviceContext context)
        {
            var locked = _bitmap!.LockBits(new Rectangle(0, 0, TexWidth, TexHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var mapped = context.Map(_tex!, 0, MapMode.WriteDiscard, MapFlags.None);
                try
                {
                    int rowBytes = TexWidth * 4;
                    unsafe
                    {
                        byte* src = (byte*)locked.Scan0;
                        byte* dst = (byte*)mapped.DataPointer;
                        for (int py = 0; py < TexHeight; py++)
                            Buffer.MemoryCopy(src + py * locked.Stride, dst + py * (int)mapped.RowPitch, rowBytes, rowBytes);
                    }
                }
                finally
                {
                    context.Unmap(_tex!, 0);
                }
            }
            finally
            {
                _bitmap.UnlockBits(locked);
            }
        }

        public void Dispose()
        {
            _srv?.Dispose();
            _tex?.Dispose();
            _graphics?.Dispose();
            _bitmap?.Dispose();
            _srv = null;
            _tex = null;
            _graphics = null;
            _bitmap = null;
            _last = "";
        }
    }
}
