using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ImageRecolorWindow : UiWindow{
	private readonly string _sourcePath;

	private readonly string _relativePath;

	private Image<Rgba32> _originalBitmap;

	private Image<Rgba32> _currentBitmap;

	public ImageRecolorWindow(string sourcePath, string relativePath)
	{
		Voidstrap.UI.RoundedWindowChrome.Prepare(this);
		InitializeComponent();
		_sourcePath = sourcePath;
		_relativePath = relativePath;
		_originalBitmap = SixLabors.ImageSharp.Image.Load<Rgba32>(_sourcePath);
		_currentBitmap = _originalBitmap.Clone();
		UpdatePreview();
	}

	private void UpdatePreview()
	{
		using MemoryStream memoryStream = new MemoryStream();
		_currentBitmap.SaveAsPng(memoryStream);
		memoryStream.Seek(0L, SeekOrigin.Begin);
		PreviewImage.Source = Voidstrap.Utility.SafeImaging.FromStream(memoryStream);
	}

	private void ApplyRecolor()
	{
		try
		{
			if (!SixLabors.ImageSharp.Color.TryParseHex(HexInput.Text, out SixLabors.ImageSharp.Color parsed))
			{
				return;
			}

			Rgba32 target = parsed.ToPixel<Rgba32>();
			float intensity = (float)IntensitySlider.Value;
			float scaleR = 1f - intensity + target.R / 255f * intensity;
			float scaleG = 1f - intensity + target.G / 255f * intensity;
			float scaleB = 1f - intensity + target.B / 255f * intensity;

			Image<Rgba32> bitmap = _originalBitmap.Clone();
			bitmap.ProcessPixelRows(accessor =>
			{
				for (int y = 0; y < accessor.Height; y++)
				{
					Span<Rgba32> row = accessor.GetRowSpan(y);
					for (int x = 0; x < row.Length; x++)
					{
						Rgba32 pixel = row[x];
						pixel.R = (byte)Math.Clamp(pixel.R * scaleR, 0f, 255f);
						pixel.G = (byte)Math.Clamp(pixel.G * scaleG, 0f, 255f);
						pixel.B = (byte)Math.Clamp(pixel.B * scaleB, 0f, 255f);
						row[x] = pixel;
					}
				}
			});

			_currentBitmap?.Dispose();
			_currentBitmap = bitmap;
			UpdatePreview();
		}
		catch
		{
		}
	}

	private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
	{
		ApplyRecolor();
	}

	private void Apply_Click(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		ApplyRecolor();
	}

	private void PickColor_Click(object sender, RoutedEventArgs e)
	{
		var dlg = new Voidstrap.UI.Elements.Controls.RinColorPickerDialog { Owner = this };
		if (dlg.ShowOwnedDialog() == true)
		{
			HexInput.Text = $"#{dlg.SelectedColor.R:X2}{dlg.SelectedColor.G:X2}{dlg.SelectedColor.B:X2}";
		}
	}

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap?.Dispose();
		_currentBitmap = _originalBitmap.Clone();
		HexInput.Text = "#FFFFFF";
		IntensitySlider.Value = 1.0;
		UpdatePreview();
	}

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		string text = Path.Combine(Paths.Mods, _relativePath);
		string? directoryName = Path.GetDirectoryName(text);
		try
		{
			if (directoryName != null)
			{
				Directory.CreateDirectory(directoryName);
			}
			_currentBitmap.SaveAsPng(text);
			Frontend.ShowMessageBox("Image recolored and saved to Mods!", MessageBoxImage.Asterisk);
			Close();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to save: " + ex.Message, MessageBoxImage.Hand);
		}
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void Cancel_Click(object sender, CancelEventArgs e)
	{
		_originalBitmap?.Dispose();
		_currentBitmap?.Dispose();
	}
}
