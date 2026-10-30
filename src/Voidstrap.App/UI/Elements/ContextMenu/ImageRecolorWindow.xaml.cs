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
using Voidstrap.Utility;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ImageRecolorWindow : UiWindow
{
	private readonly string _sourcePath;

	private readonly string _relativePath;

	private readonly Raster _originalBitmap;

	private Raster _currentBitmap;

	public ImageRecolorWindow(string sourcePath, string relativePath)
	{
		Voidstrap.UI.RoundedWindowChrome.Prepare(this);
		InitializeComponent();
		_sourcePath = sourcePath;
		_relativePath = relativePath;
		_originalBitmap = Raster.Load(_sourcePath);
		_currentBitmap = _originalBitmap.Clone();
		UpdatePreview();
	}

	private void UpdatePreview()
	{
		BitmapSource preview = BitmapSource.Create(
			_currentBitmap.Width,
			_currentBitmap.Height,
			96,
			96,
			System.Windows.Media.PixelFormats.Bgra32,
			null,
			_currentBitmap.Pixels,
			_currentBitmap.Width * 4
		);
		preview.Freeze();
		PreviewImage.Source = preview;
	}

	private void ApplyRecolor()
	{
		try
		{
			if (!TryParseHex(HexInput.Text, out byte red, out byte green, out byte blue))
			{
				return;
			}

			float intensity = (float)IntensitySlider.Value;
			float scaleR = 1f - intensity + red / 255f * intensity;
			float scaleG = 1f - intensity + green / 255f * intensity;
			float scaleB = 1f - intensity + blue / 255f * intensity;

			Raster bitmap = _originalBitmap.Clone();
			byte[] pixels = bitmap.Pixels;
			for (int index = 0; index < pixels.Length; index += 4)
			{
				pixels[index] = (byte)Math.Clamp(pixels[index] * scaleB, 0f, 255f);
				pixels[index + 1] = (byte)Math.Clamp(pixels[index + 1] * scaleG, 0f, 255f);
				pixels[index + 2] = (byte)Math.Clamp(pixels[index + 2] * scaleR, 0f, 255f);
			}

			_currentBitmap = bitmap;
			UpdatePreview();
		}
		catch { }
	}

	private static bool TryParseHex(string? text, out byte red, out byte green, out byte blue)
	{
		red = 0;
		green = 0;
		blue = 0;
		string hex = text ?? string.Empty;
		if (hex.StartsWith('#'))
			hex = hex[1..];
		if (hex.Length is 3 or 4)
			hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
		else if (hex.Length is 6 or 8)
			hex = hex[..6];
		else
			return false;
		if (
			!uint.TryParse(
				hex,
				System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture,
				out uint value
			)
		)
			return false;
		red = (byte)(value >> 16);
		green = (byte)(value >> 8);
		blue = (byte)value;
		return true;
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
			_currentBitmap.SavePng(text);
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

	private void Cancel_Click(object sender, CancelEventArgs e) { }
}
