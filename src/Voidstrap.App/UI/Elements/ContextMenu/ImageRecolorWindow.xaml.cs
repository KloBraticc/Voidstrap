using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ImageRecolorWindow : UiWindow{
	private readonly string _sourcePath;

	private readonly string _relativePath;

	private Bitmap _originalBitmap;

	private Bitmap _currentBitmap;

	public ImageRecolorWindow(string sourcePath, string relativePath)
	{
		Voidstrap.UI.RoundedWindowChrome.Prepare(this);
		InitializeComponent();
		_sourcePath = sourcePath;
		_relativePath = relativePath;
		_originalBitmap = new Bitmap(_sourcePath);
		_currentBitmap = new Bitmap(_originalBitmap);
		UpdatePreview();
	}

	private void UpdatePreview()
	{
		using MemoryStream memoryStream = new MemoryStream();
		_currentBitmap.Save(memoryStream, ImageFormat.Png);
		memoryStream.Seek(0L, SeekOrigin.Begin);
		BitmapImage bitmapImage = new BitmapImage();
		bitmapImage.BeginInit();
		bitmapImage.StreamSource = memoryStream;
		bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
		bitmapImage.EndInit();
		((Freezable)bitmapImage).Freeze();
		PreviewImage.Source = bitmapImage;
	}

	private void ApplyRecolor()
	{
		try
		{
			Color color = ColorTranslator.FromHtml(HexInput.Text);
			float num = (float)IntensitySlider.Value;
			Bitmap bitmap = new Bitmap(_originalBitmap.Width, _originalBitmap.Height);
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				ColorMatrix colorMatrix = new ColorMatrix(new float[5][]
				{
					new float[5]
					{
						1f - num + (float)(int)color.R / 255f * num,
						0f,
						0f,
						0f,
						0f
					},
					new float[5]
					{
						0f,
						1f - num + (float)(int)color.G / 255f * num,
						0f,
						0f,
						0f
					},
					new float[5]
					{
						0f,
						0f,
						1f - num + (float)(int)color.B / 255f * num,
						0f,
						0f
					},
					new float[5] { 0f, 0f, 0f, 1f, 0f },
					new float[5] { 0f, 0f, 0f, 0f, 1f }
				});
				ImageAttributes imageAttributes = new ImageAttributes();
				imageAttributes.SetColorMatrix(colorMatrix);
				graphics.DrawImage(_originalBitmap, new Rectangle(0, 0, _originalBitmap.Width, _originalBitmap.Height), 0, 0, _originalBitmap.Width, _originalBitmap.Height, GraphicsUnit.Pixel, imageAttributes);
			}
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
		if (dlg.ShowDialog() == true)
		{
			HexInput.Text = $"#{dlg.SelectedColor.R:X2}{dlg.SelectedColor.G:X2}{dlg.SelectedColor.B:X2}";
		}
	}

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap?.Dispose();
		_currentBitmap = new Bitmap(_originalBitmap);
		HexInput.Text = "#FFFFFF";
		IntensitySlider.Value = 1.0;
		UpdatePreview();
	}

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		string text = Path.Combine(Paths.Mods, _relativePath);
		string directoryName = Path.GetDirectoryName(text);
		try
		{
			if (directoryName != null)
			{
				Directory.CreateDirectory(directoryName);
			}
			_currentBitmap.Save(text, ImageFormat.Png);
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
