using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Voidstrap.Integrations.Overlays
{
    internal sealed class OverlayHud
    {
        public const int TexWidth = 300;
        public const int TexHeight = 96;

        private const int LabelCols = 16;

        private ID3D11Device _device = null!;
        private ID3D11Texture2D? _tex;
        private ID3D11ShaderResourceView? _srv;
        private string _last = "";
		private Bitmap? _bitmap;
		private Graphics? _graphics;
		private Font? _font;
		private SolidBrush? _backgroundBrush;
		private SolidBrush? _labelBrush;
		private SolidBrush? _dotBrush;
		private SolidBrush? _valueBrush;
		private StringFormat? _textFormat;
		private float _characterWidth;

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
			_graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
			_font = new Font("Consolas", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
			_backgroundBrush = new SolidBrush(Color.FromArgb(190, 12, 13, 16));
			_labelBrush = new SolidBrush(Color.FromArgb(240, 226, 229, 233));
			_dotBrush = new SolidBrush(Color.FromArgb(150, 128, 138, 128));
			_valueBrush = new SolidBrush(Color.FromArgb(255, 150, 226, 150));
			_textFormat = new StringFormat(StringFormat.GenericTypographic);
			_characterWidth = _graphics.MeasureString("00000000", _font, PointF.Empty, _textFormat).Width / 8f;
			_last = "";
        }

        public void Update(ID3D11DeviceContext context, string[] labels, string[] values)
        {
            if (_tex == null || _bitmap == null || _graphics == null || _font == null || _backgroundBrush == null || _labelBrush == null || _dotBrush == null || _valueBrush == null || _textFormat == null)
                return;
            int rows = Math.Min(labels.Length, values.Length);
            string signature = string.Join("", labels, 0, rows) + "" + string.Join("", values, 0, rows);
            if (signature == _last)
                return;
            _last = signature;

			_graphics.Clear(Color.Transparent);
			_graphics.FillRectangle(_backgroundBrush, 0, 0, TexWidth, TexHeight);

			float x = 8f;
			float y = 6f;
			float rowH = (TexHeight - 12f) / Math.Max(1, rows);
			for (int i = 0; i < rows; i++)
			{
				string label = labels[i];
				int pad = Math.Max(1, LabelCols - label.Length);
				string dots = new string('.', pad);
				_graphics.DrawString(label, _font, _labelBrush, x, y, _textFormat);
				_graphics.DrawString(dots, _font, _dotBrush, x + label.Length * _characterWidth, y, _textFormat);
				_graphics.DrawString(":", _font, _labelBrush, x + LabelCols * _characterWidth, y, _textFormat);
				_graphics.DrawString(values[i], _font, _valueBrush, x + (LabelCols + 2) * _characterWidth, y, _textFormat);
				y += rowH;
			}

			var locked = _bitmap.LockBits(new Rectangle(0, 0, TexWidth, TexHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
				var mapped = context.Map(_tex, 0, MapMode.WriteDiscard, MapFlags.None);
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
					context.Unmap(_tex, 0);
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
			_font?.Dispose();
			_backgroundBrush?.Dispose();
			_labelBrush?.Dispose();
			_dotBrush?.Dispose();
			_valueBrush?.Dispose();
			_textFormat?.Dispose();
            _srv = null;
            _tex = null;
			_graphics = null;
			_bitmap = null;
			_font = null;
			_backgroundBrush = null;
			_labelBrush = null;
			_dotBrush = null;
			_valueBrush = null;
			_textFormat = null;
			_last = "";
        }
    }
}
