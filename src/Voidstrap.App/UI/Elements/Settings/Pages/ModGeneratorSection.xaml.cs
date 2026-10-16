using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ModGeneratorSection : UserControl
{
	private static readonly string[] Presets =
	[
		"#FF3B5C", "#FF8A3D", "#FFD23F", "#3DDC84", "#2EC5FF", "#4F7CFF", "#9B5CFF", "#FF5CD6", "#FFFFFF", "#9AA0A6"
	];

	private readonly DispatcherTimer _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500.0) };

	private readonly List<Button> _presetButtons = [];

	private const long MaxThemeBytes = 64 * 1024;

	private static readonly System.Text.Json.JsonSerializerOptions ThemeJson = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };

	private CancellationTokenSource? _generateCts;

	private string? _fillImage;

	private string? _logoImage;

	private bool _loading;

	private int _previewVersion;

	private bool _initialized;

	private bool _previewRunning;

	private bool _previewPending;

	private bool _generating;

	public ModGeneratorSection()
	{
		InitializeComponent();
		foreach (string preset in Presets)
		{
			Button button = new Button
			{
				Style = (Style)Resources["SwatchButton"],
				Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset)),
				Tag = preset,
				ToolTip = preset
			};
			button.Click += Preset_Click;
			_presetButtons.Add(button);
			PresetPanel.Children.Add(button);
		}
	}

	private void Section_Loaded(object sender, RoutedEventArgs e)
	{
		_previewTimer.Tick += PreviewTimer_Tick;
		if (!_initialized)
		{
			_initialized = true;
			LoadSettings();
		}
		SchedulePreview();
	}

	private void Section_Unloaded(object sender, RoutedEventArgs e)
	{
		_previewTimer.Stop();
		_previewTimer.Tick -= PreviewTimer_Tick;
		_previewPending = false;
	}

	private void LoadSettings()
	{
		_loading = true;
		try
		{
			string? modId = ModGenerator.FindModId();
			ModGeneratorSettings settings = (modId != null ? ModGenerator.LoadSettings(modId) : null) ?? new ModGeneratorSettings();
			ApplySettings(settings);
			if (modId != null && settings.GeneratedUtc != default)
			{
				StatusText.Text = string.Format(CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_ModGen_LastGenerated, settings.GeneratedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
				GenerateButton.Content = Voidstrap.Resources.Strings.Ui_ModGen_RegenerateMod;
			}
		}
		finally
		{
			_loading = false;
		}
		UpdateVisibility();
		UpdateSwatches();
		UpdateImageLabels();
	}

	private void ApplySettings(ModGeneratorSettings settings)
	{
		bool wasLoading = _loading;
		_loading = true;
		try
		{
			FillPicker.SelectedIndex = (int)settings.Fill;
			SolidColorBox.Text = settings.SolidColor;
			GradientStartBox.Text = settings.GradientColors.Count > 0 ? settings.GradientColors[0] : "#FF3B5C";
			GradientMiddleBox.Text = settings.GradientColors.Count > 2 ? settings.GradientColors[1] : "";
			GradientEndBox.Text = settings.GradientColors.Count > 1 ? settings.GradientColors[^1] : "#7B5CFF";
			AngleSlider.Value = settings.GradientAngle;
			ScalePicker.SelectedIndex = Math.Clamp(settings.IconScale, 0, 3);
			KeepColoredToggle.IsChecked = settings.KeepColoredIcons;
			KeepShadingToggle.IsChecked = settings.KeepShading;
			RecolorShapesToggle.IsChecked = settings.RecolorShapes;
			IconFontToggle.IsChecked = settings.RecolorIconFont;
			UiTexturesToggle.IsChecked = settings.IncludeUiTextures;
			ModNameBox.Text = settings.ModName;
			OpacitySlider.Value = Math.Round(Math.Clamp(double.IsFinite(settings.IconOpacity) ? settings.IconOpacity : 1.0, 0.1, 1.0) * 100.0);
			OpacityText.Text = Math.Round(OpacitySlider.Value) + "%";
			_fillImage = settings.FillImage;
			_logoImage = settings.LogoImage;
		}
		finally
		{
			_loading = wasLoading;
		}
	}

	private void RefreshAfterApply()
	{
		UpdateVisibility();
		UpdateSwatches();
		UpdateImageLabels();
		SchedulePreview();
	}

	private ModGeneratorSettings ReadSettings()
	{
		List<string> gradient = [GradientStartBox.Text.Trim()];
		if (ModGenerator.TryParseColor(GradientMiddleBox.Text, out _))
		{
			gradient.Add(GradientMiddleBox.Text.Trim());
		}
		gradient.Add(GradientEndBox.Text.Trim());
		return new ModGeneratorSettings
		{
			Fill = (ModGeneratorFill)Math.Max(0, FillPicker.SelectedIndex),
			SolidColor = SolidColorBox.Text.Trim(),
			GradientColors = gradient,
			GradientAngle = AngleSlider.Value,
			FillImage = _fillImage,
			IconScale = Math.Max(0, ScalePicker.SelectedIndex),
			KeepColoredIcons = KeepColoredToggle.IsChecked == true,
			KeepShading = KeepShadingToggle.IsChecked == true,
			RecolorShapes = RecolorShapesToggle.IsChecked == true,
			RecolorIconFont = IconFontToggle.IsChecked == true,
			IncludeUiTextures = UiTexturesToggle.IsChecked == true,
			IconOpacity = Math.Clamp(OpacitySlider.Value / 100.0, 0.1, 1.0),
			LogoImage = _logoImage,
			ModName = string.IsNullOrWhiteSpace(ModNameBox.Text) ? "Generated UI theme" : ModNameBox.Text.Trim()
		};
	}

	private string? Validate(ModGeneratorSettings settings)
	{
		switch (settings.Fill)
		{
			case ModGeneratorFill.Solid when !ModGenerator.TryParseColor(settings.SolidColor, out _):
				return Voidstrap.Resources.Strings.Ui_ModGen_EnterHexColour;
			case ModGeneratorFill.Gradient when !ModGenerator.TryParseColor(GradientStartBox.Text, out _) || !ModGenerator.TryParseColor(GradientEndBox.Text, out _):
				return Voidstrap.Resources.Strings.Ui_ModGen_EnterGradientColours;
			case ModGeneratorFill.Image when string.IsNullOrWhiteSpace(settings.FillImage) || !File.Exists(settings.FillImage):
				return Voidstrap.Resources.Strings.Ui_ModGen_ChooseFillImage;
			default:
				return null;
		}
	}

	private void Input_Changed(object sender, RoutedEventArgs e)
	{
		if (_loading)
		{
			return;
		}
		UpdateVisibility();
		UpdateSwatches();
		SchedulePreview();
	}

	private void Angle_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		AngleText.Text = Math.Round(e.NewValue) + "°";
		if (!_loading)
		{
			SchedulePreview();
		}
	}

	private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		OpacityText.Text = Math.Round(e.NewValue) + "%";
		if (!_loading)
		{
			SchedulePreview();
		}
	}

	private void Preset_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string color })
		{
			SolidColorBox.Text = color;
		}
	}

	private void Swatch_Click(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
		TextBox? box = ReferenceEquals(sender, SolidSwatch) ? SolidColorBox
			: ReferenceEquals(sender, GradientStartSwatch) ? GradientStartBox
			: ReferenceEquals(sender, GradientMiddleSwatch) ? GradientMiddleBox
			: ReferenceEquals(sender, GradientEndSwatch) ? GradientEndBox
			: null;
		if (box == null)
		{
			return;
		}
		e.Handled = true;
		if (!ModGenerator.TryParseColor(box.Text, out Color initial))
		{
			initial = ReferenceEquals(box, GradientMiddleBox) ? Blend(GradientStartBox.Text, GradientEndBox.Text) : Colors.White;
		}
		Voidstrap.UI.Elements.Controls.RinColorPickerDialog dialog = new Voidstrap.UI.Elements.Controls.RinColorPickerDialog(initial);
		if (dialog.ShowOwnedDialog() != true)
		{
			return;
		}
		Color picked = dialog.SelectedColor;
		box.Text = "#" + picked.R.ToString("X2", CultureInfo.InvariantCulture) + picked.G.ToString("X2", CultureInfo.InvariantCulture) + picked.B.ToString("X2", CultureInfo.InvariantCulture);
	}

	private static Color Blend(string start, string end)
	{
		if (!ModGenerator.TryParseColor(start, out Color first) || !ModGenerator.TryParseColor(end, out Color last))
		{
			return Colors.White;
		}
		return Color.FromRgb((byte)((first.R + last.R) / 2), (byte)((first.G + last.G) / 2), (byte)((first.B + last.B) / 2));
	}

	private void UpdateVisibility()
	{
		ModGeneratorFill fill = (ModGeneratorFill)Math.Max(0, FillPicker.SelectedIndex);
		SolidPanel.Visibility = fill == ModGeneratorFill.Solid ? Visibility.Visible : Visibility.Collapsed;
		GradientPanel.Visibility = fill == ModGeneratorFill.Gradient ? Visibility.Visible : Visibility.Collapsed;
		ImagePanel.Visibility = fill == ModGeneratorFill.Image ? Visibility.Visible : Visibility.Collapsed;
	}

	private void UpdateSwatches()
	{
		SetSwatch(SolidSwatch, SolidColorBox.Text);
		SetSwatch(GradientStartSwatch, GradientStartBox.Text);
		SetSwatch(GradientMiddleSwatch, GradientMiddleBox.Text);
		SetSwatch(GradientEndSwatch, GradientEndBox.Text);
	}

	private static void SetSwatch(Border swatch, string text)
	{
		swatch.Background = ModGenerator.TryParseColor(text, out Color color) ? new SolidColorBrush(color) : Brushes.Transparent;
	}

	private void UpdateImageLabels()
	{
		FillImageText.Text = string.IsNullOrWhiteSpace(_fillImage) ? Voidstrap.Resources.Strings.Ui_ModGen_NoImageChosen : Path.GetFileName(_fillImage);
		bool hasLogo = !string.IsNullOrWhiteSpace(_logoImage);
		LogoText.Text = hasLogo ? Path.GetFileName(_logoImage) : Voidstrap.Resources.Strings.Ui_ModGen_ReplacesTheRobloxLogoInMenus;
		ClearLogoButton.Visibility = hasLogo ? Visibility.Visible : Visibility.Collapsed;
	}

	private static string? PickImage()
	{
		OpenFileDialog dialog = new OpenFileDialog
		{
			Filter = Voidstrap.Resources.Strings.Ui_ModGen_ImagesFilter + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
			Multiselect = false
		};
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}

	private void ChooseFill_Click(object sender, RoutedEventArgs e)
	{
		string? path = PickImage();
		if (path == null)
		{
			return;
		}
		_fillImage = path;
		UpdateImageLabels();
		SchedulePreview();
	}

	private void ChooseLogo_Click(object sender, RoutedEventArgs e)
	{
		string? path = PickImage();
		if (path == null)
		{
			return;
		}
		_logoImage = path;
		UpdateImageLabels();
	}

	private void ClearLogo_Click(object sender, RoutedEventArgs e)
	{
		_logoImage = null;
		UpdateImageLabels();
	}

	private void SchedulePreview()
	{
		_previewTimer.Stop();
		_previewTimer.Start();
	}

	private async void PreviewTimer_Tick(object? sender, EventArgs e)
	{
		_previewTimer.Stop();
		if (_previewRunning)
		{
			_previewPending = true;
			return;
		}
		ModGeneratorSettings settings = ReadSettings();
		string? problem = Validate(settings);
		if (problem != null)
		{
			PreviewNote.Text = problem;
			return;
		}
		int version = ++_previewVersion;
		_previewRunning = true;
		PreviewRing.Visibility = Visibility.Visible;
		ModGeneratorPreview? preview = null;
		try
		{
			preview = await Task.Run(() => ModGenerator.RenderPreview(settings));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModGeneratorSection", "The preview could not be rendered: " + ex.Message);
		}
		finally
		{
			_previewRunning = false;
			PreviewRing.Visibility = Visibility.Collapsed;
		}
		if (_previewPending)
		{
			_previewPending = false;
			if (IsLoaded)
			{
				SchedulePreview();
			}
			return;
		}
		if (version != _previewVersion || !IsLoaded)
		{
			return;
		}
		if (preview == null)
		{
			PreviewNote.Text = "";
			BeforeImage.Source = null;
			AfterImage.Source = null;
			FontPreviewCard.Visibility = Visibility.Collapsed;
			return;
		}
		BeforeImage.Source = preview.Before;
		AfterImage.Source = preview.After;
		PreviewCaption.Text = preview.NamedIcons > 0 ? Voidstrap.Resources.Strings.Ui_ModGen_RealIconsFromYourRobloxBuild : Voidstrap.Resources.Strings.Ui_ModGen_SliceOfSheet;
		PreviewNote.Text = preview.NamedIcons > 0
			? string.Format(CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_ModGen_NamedIconsRead, preview.NamedIcons.ToString("N0", CultureInfo.CurrentCulture))
			: Voidstrap.Resources.Strings.Ui_ModGen_LayoutUnreadable;
		ShowFontPreview(preview, settings.RecolorIconFont);
	}

	private void ShowFontPreview(ModGeneratorPreview preview, bool recolor)
	{
		FontPreviewCard.Visibility = Visibility.Collapsed;
		if (string.IsNullOrWhiteSpace(preview.IconFont) || !File.Exists(preview.IconFont))
		{
			return;
		}
		try
		{
			GlyphTypeface typeface = new GlyphTypeface(new Uri(preview.IconFont));
			const double size = 24.0;
			List<ushort> glyphs = [];
			List<double> advances = [];
			int count = typeface.GlyphCount;
			for (int step = 0; step < 40 && glyphs.Count < 9 && count > 1; step++)
			{
				ushort glyph = (ushort)(1 + (long)step * (count - 2) / 40);
				if (glyphs.Contains(glyph) || typeface.AdvanceWidths[glyph] - typeface.LeftSideBearings[glyph] - typeface.RightSideBearings[glyph] <= 0.0)
				{
					continue;
				}
				glyphs.Add(glyph);
				advances.Add(typeface.AdvanceWidths[glyph] * size + 10.0);
			}
			if (glyphs.Count == 0)
			{
				return;
			}
			float pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
			Brush brush = recolor ? preview.FontBrush ?? new SolidColorBrush(preview.FontColor) : Brushes.White;
			brush.Freeze();
			DrawingGroup group = new DrawingGroup();
			double x = 0;
			for (int i = 0; i < glyphs.Count; i++)
			{
				GlyphRun run = new GlyphRun(typeface, 0, false, size, pixelsPerDip, [glyphs[i]], new Point(x, size), [advances[i]], null, null, null, null, null, null);
				group.Children.Add(new GlyphRunDrawing(brush, run));
				x += advances[i];
			}
			DrawingImage image = new DrawingImage(group);
			image.Freeze();
			FontPreviewImage.Source = image;
			FontPreviewCard.Visibility = Visibility.Visible;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModGeneratorSection", "The icon font preview could not be drawn: " + ex.Message);
		}
	}

	private async void Generate_Click(object sender, RoutedEventArgs e)
	{
		if (_generating)
		{
			return;
		}
		ModGeneratorSettings settings = ReadSettings();
		string? problem = Validate(settings);
		if (problem != null)
		{
			StatusText.Text = problem;
			return;
		}
		_generating = true;
		_generateCts?.Dispose();
		_generateCts = new CancellationTokenSource();
		CancellationToken token = _generateCts.Token;
		bool waiting = ModGenerator.IsGenerating;
		SetToolsEnabled(false);
		CancelButton.Visibility = Visibility.Visible;
		GenerateProgress.Visibility = Visibility.Visible;
		GenerateProgress.IsIndeterminate = waiting;
		GenerateProgress.Value = 0;
		if (waiting)
		{
			StatusText.Text = Voidstrap.Resources.Strings.Ui_ModGen_WaitingForOther;
		}
		Progress<ModGeneratorProgress> progress = new Progress<ModGeneratorProgress>(OnProgress);
		try
		{
			await ModGenerator.GenerateAsync(settings, progress, token);
			StatusText.Text = "";
			GenerateButton.Content = Voidstrap.Resources.Strings.Ui_ModGen_RegenerateMod;
			if (DataContext is Voidstrap.UI.ViewModels.Settings.ModsViewModel mods && mods.RefreshManagedModsCommand.CanExecute(null))
			{
				mods.RefreshManagedModsCommand.Execute(null);
			}
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = Voidstrap.Resources.Strings.Ui_ModGen_Cancelled;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModGeneratorSection", "Generation failed: " + ex.Message);
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_ModGen_Failed, ex.Message);
		}
		finally
		{
			_generating = false;
			SetToolsEnabled(true);
			CancelButton.Visibility = Visibility.Collapsed;
			GenerateProgress.Visibility = Visibility.Collapsed;
			GenerateProgress.IsIndeterminate = false;
		}
	}

	private void SetToolsEnabled(bool enabled)
	{
		GenerateButton.IsEnabled = enabled;
		ResetButton.IsEnabled = enabled;
		ImportButton.IsEnabled = enabled;
		RandomizeButton.IsEnabled = enabled;
	}

	private void Randomize_Click(object sender, RoutedEventArgs e)
	{
		double hue = Random.Shared.NextDouble() * 360.0;
		string first = FromHsv(hue, 0.72, 1.0);
		string second = FromHsv((hue + 40.0 + Random.Shared.NextDouble() * 140.0) % 360.0, 0.68, 1.0);
		_loading = true;
		try
		{
			if (FillPicker.SelectedIndex == (int)ModGeneratorFill.Image)
			{
				FillPicker.SelectedIndex = (int)ModGeneratorFill.Gradient;
			}
			SolidColorBox.Text = first;
			GradientStartBox.Text = first;
			GradientMiddleBox.Text = "";
			GradientEndBox.Text = second;
			AngleSlider.Value = Random.Shared.Next(0, 24) * 15;
		}
		finally
		{
			_loading = false;
		}
		RefreshAfterApply();
	}

	private static string FromHsv(double hue, double saturation, double value)
	{
		double chroma = value * saturation;
		double x = chroma * (1 - Math.Abs(hue / 60.0 % 2 - 1));
		double m = value - chroma;
		(double r, double g, double b) = (hue % 360) switch
		{
			< 60 => (chroma, x, 0.0),
			< 120 => (x, chroma, 0.0),
			< 180 => (0.0, chroma, x),
			< 240 => (0.0, x, chroma),
			< 300 => (x, 0.0, chroma),
			_ => (chroma, 0.0, x)
		};
		return "#" + ((byte)Math.Round((r + m) * 255)).ToString("X2", CultureInfo.InvariantCulture)
			+ ((byte)Math.Round((g + m) * 255)).ToString("X2", CultureInfo.InvariantCulture)
			+ ((byte)Math.Round((b + m) * 255)).ToString("X2", CultureInfo.InvariantCulture);
	}

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		ApplySettings(new ModGeneratorSettings { ModName = ModNameBox.Text });
		RefreshAfterApply();
	}

	private void Export_Click(object sender, RoutedEventArgs e)
	{
		SaveFileDialog dialog = new SaveFileDialog
		{
			Filter = Voidstrap.Resources.Strings.Ui_ModGen_ThemeFiles + "|*.json",
			FileName = "theme.json"
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			ModGeneratorSettings settings = ReadSettings();
			settings.FillImage = null;
			settings.LogoImage = null;
			File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(settings, ThemeJson));
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_ModGen_ThemeSaved, Path.GetFileName(dialog.FileName));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModGeneratorSection", "The theme could not be exported: " + ex.Message);
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_ModGen_ExportFailed, ex.Message);
		}
	}

	private void Import_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog dialog = new OpenFileDialog
		{
			Filter = Voidstrap.Resources.Strings.Ui_ModGen_ThemeFiles + "|*.json",
			Multiselect = false
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			if (new FileInfo(dialog.FileName).Length > MaxThemeBytes)
			{
				throw new InvalidDataException(Voidstrap.Resources.Strings.Ui_ModGen_NotATheme);
			}
			ModGeneratorSettings settings = System.Text.Json.JsonSerializer.Deserialize<ModGeneratorSettings>(File.ReadAllText(dialog.FileName), ThemeJson)
				?? throw new InvalidDataException(Voidstrap.Resources.Strings.Ui_ModGen_NotATheme);
			if (!Enum.IsDefined(settings.Fill) || settings.Fill == ModGeneratorFill.Image)
			{
				settings.Fill = ModGeneratorFill.Solid;
			}
			settings.GradientColors ??= [];
			settings.FillImage = _fillImage;
			settings.LogoImage = _logoImage;
			if (string.IsNullOrWhiteSpace(settings.ModName))
			{
				settings.ModName = ModNameBox.Text;
			}
			ApplySettings(settings);
			RefreshAfterApply();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModGeneratorSection", "The theme could not be imported: " + ex.Message);
			StatusText.Text = string.Format(CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Ui_ModGen_ImportFailed, ex.Message);
		}
	}

	private void OnProgress(ModGeneratorProgress value)
	{
		GenerateProgress.Value = value.Fraction;
		StatusText.Text = value.Message;
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		_generateCts?.Cancel();
	}
}
