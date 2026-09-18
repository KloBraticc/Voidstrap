using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ImageAdjustWindow : UiWindow{
	private readonly string _sourcePath;

	private readonly string _relativePath;

	private Image<Rgba32> _originalBitmap;

	private Image<Rgba32> _currentBitmap;

	public ImageAdjustWindow(string sourcePath, string relativePath)
	{
		Voidstrap.UI.RoundedWindowChrome.Prepare(this);
		InitializeComponent();
		_sourcePath = sourcePath;
		_relativePath = relativePath;
		_originalBitmap = SixLabors.ImageSharp.Image.Load<Rgba32>(_sourcePath);
		_currentBitmap = _originalBitmap.Clone();
		WidthInput.Text = _originalBitmap.Width.ToString();
		HeightInput.Text = _originalBitmap.Height.ToString();
		UpdatePreview();
	}

	private void UpdatePreview()
	{
		using MemoryStream memoryStream = new MemoryStream();
		_currentBitmap.SaveAsPng(memoryStream);
		memoryStream.Seek(0L, SeekOrigin.Begin);
		PreviewImage.Source = Voidstrap.Utility.SafeImaging.FromStream(memoryStream);
	}

	private void Rotate_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap.Mutate(context => context.Rotate(RotateMode.Rotate90));
		UpdatePreview();
	}

	private void FlipH_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap.Mutate(context => context.Flip(FlipMode.Horizontal));
		UpdatePreview();
	}

	private void FlipV_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap.Mutate(context => context.Flip(FlipMode.Vertical));
		UpdatePreview();
	}

	private void ApplyResize_Click(object sender, RoutedEventArgs e)
	{
		if (int.TryParse(WidthInput.Text, out var result) && int.TryParse(HeightInput.Text, out var result2))
		{
			if (result <= 0 || result2 <= 0)
			{
				return;
			}

			_currentBitmap.Mutate(context => context.Resize(result, result2));
			UpdatePreview();
		}
	}

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		_currentBitmap?.Dispose();
		_currentBitmap = _originalBitmap.Clone();
		WidthInput.Text = _originalBitmap.Width.ToString();
		HeightInput.Text = _originalBitmap.Height.ToString();
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
