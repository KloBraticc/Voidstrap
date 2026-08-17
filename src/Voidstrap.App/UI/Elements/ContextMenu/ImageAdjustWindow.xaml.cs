using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ImageAdjustWindow : UiWindow{
	private readonly string _sourcePath;

	private readonly string _relativePath;

	private Bitmap _originalBitmap;

	private Bitmap _currentBitmap;

	public ImageAdjustWindow(string sourcePath, string relativePath)
	{
		Voidstrap.UI.RoundedWindowChrome.Prepare(this);
		InitializeComponent();
		_sourcePath = sourcePath;
		_relativePath = relativePath;
		_originalBitmap = new Bitmap(_sourcePath);
		_currentBitmap = new Bitmap(_originalBitmap);
		WidthInput.Text = _originalBitmap.Width.ToString();
		HeightInput.Text = _originalBitmap.Height.ToString();
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

	private void Rotate_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
		UpdatePreview();
	}

	private void FlipH_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap.RotateFlip(RotateFlipType.RotateNoneFlipX);
		UpdatePreview();
	}

	private void FlipV_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap.RotateFlip(RotateFlipType.Rotate180FlipX);
		UpdatePreview();
	}

	private void ApplyResize_Click(object sender, RoutedEventArgs e)
	{
		if (int.TryParse(WidthInput.Text, out var result) && int.TryParse(HeightInput.Text, out var result2))
		{
			Bitmap bitmap = new Bitmap(result, result2);
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.DrawImage(_currentBitmap, 0, 0, result, result2);
			}
			_currentBitmap?.Dispose();
			_currentBitmap = bitmap;
			UpdatePreview();
		}
	}

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap?.Dispose();
		_currentBitmap = new Bitmap(_originalBitmap);
		WidthInput.Text = _originalBitmap.Width.ToString();
		HeightInput.Text = _originalBitmap.Height.ToString();
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
			Frontend.ShowMessageBox("Image adjustments applied and saved to Mods!", MessageBoxImage.Asterisk);
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
