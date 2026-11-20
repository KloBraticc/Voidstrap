using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Voidstrap.UI.Elements.Base;
using FontFamily = System.Windows.Media.FontFamily;

namespace Voidstrap.UI;

public static class AppFont
{
    private static bool _registered;

    private static readonly object[] SystemFontKeys =
    [
        SystemFonts.MessageFontFamilyKey,
        SystemFonts.StatusFontFamilyKey,
        SystemFonts.MenuFontFamilyKey,
        SystemFonts.CaptionFontFamilyKey,
        SystemFonts.SmallCaptionFontFamilyKey,
        SystemFonts.IconFontFamilyKey,
    ];

    private static FontFamily? _current;

    private static readonly System.Collections.Generic.HashSet<FontFamily> AppliedFamilies = [];

    private static readonly FontFamily LinuxWpfUiFontFamily = Voidstrap.Utility.Platform.IsMacOS
        ? new(
            new Uri(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts")
                    + Path.DirectorySeparatorChar
            ),
            "./#Inter 18pt, ./#Selawik, Helvetica Neue"
        )
        : new("Inter 18pt, Selawik, Segoe UI Variable, Segoe UI, Ubuntu, Cantarell, Noto Sans, DejaVu Sans, Liberation Sans");

    public static bool HasCustomFont => _current != null;

    public static FontFamily CurrentFontFamily => _current ?? DefaultFontFamily;

    private static FontFamily DefaultFontFamily
    {
        get
        {
            if (Voidstrap.Utility.Platform.UsesPortableUi)
            {
                return LinuxWpfUiFontFamily;
            }

            if (Application.Current?.TryFindResource("ContentControlThemeFontFamily") is FontFamily fontFamily)
            {
                return fontFamily;
            }

            return new FontFamily("Segoe UI");
        }
    }

    public static string CurrentFontName
    {
        get
        {
            try
            {
                if (_current == null)
                {
                    return "";
                }
                string text = string.Join(" ", _current.FamilyNames.Values);
                return string.IsNullOrWhiteSpace(text) ? "" : text;
            }
            catch
            {
                return "";
            }
        }
    }

    public static void Initialize()
    {
        Load();
        Register();
        ApplyToAllWindows();
    }

    public static void Register()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            (RoutedEventHandler)
                delegate (object sender, RoutedEventArgs _)
                {
                    if (sender is Window window)
                    {
                        Apply(window);
                    }
                }
        );
        EventManager.RegisterClassHandler(typeof(Page), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnControlShown));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnControlShown));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnControlShown));
    }

    private static void OnControlShown(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement control)
        {
            ApplyTo(control);
        }
    }

    private static void Load()
    {
        if (_current != null)
            AppliedFamilies.Add(_current);
        _current = null;
        if (Voidstrap.Utility.Platform.UsesPortableUi)
            SetRendererFonts(null);
        try
        {
            string appFontPath = App.Settings.Prop.AppFontPath;
            if (!string.IsNullOrEmpty(appFontPath) && File.Exists(appFontPath))
            {
                _current = Voidstrap.Utility.Platform.UsesPortableUi
                    ? LoadPortable(appFontPath)
                    : Fonts.GetFontFamilies(new Uri(appFontPath, UriKind.Absolute)).FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("AppFont", "The custom font could not be loaded: " + ex.Message);
            _current = null;
        }
    }

#if CROSSPLAT
	private static System.Collections.Generic.List<ProGPU.Text.FontInfo>? _rendererFonts;

	private static FontFamily? LoadPortable(string path)
	{
		System.Collections.Generic.List<ProGPU.Text.FontInfo> faces = ProGPU.Text.FontApi.ParseFontInfos(path);
		if (faces.Count == 0)
			return null;
		ProGPU.Text.FontInfo primary = faces.FirstOrDefault(face => face.Weight == 400 && !face.IsItalic) ?? faces[0];
		if (string.IsNullOrWhiteSpace(primary.FamilyName))
			return null;
		SetRendererFonts(faces);
		string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
		App.Logger?.WriteLine("AppFont", "Loaded the custom font " + primary.FamilyName + " with " + faces.Count + " faces");
		return new FontFamily(new Uri(directory + Path.DirectorySeparatorChar), "./#" + primary.FamilyName);
	}

	private static void SetRendererFonts(System.Collections.Generic.List<ProGPU.Text.FontInfo>? faces)
	{
		try
		{
			System.Reflection.FieldInfo? fontsField = typeof(ProGPU.Text.FontApi).GetField(
				"s_cachedSystemFonts",
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
			);
			System.Reflection.FieldInfo? lockField = typeof(ProGPU.Text.FontApi).GetField(
				"s_cachedSystemFontsLock",
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
			);
			if (fontsField is null || lockField?.GetValue(null) is not object gate)
			{
				App.Logger?.WriteLine(
					"AppFont",
					"The renderer font catalog was not found, the custom font only applies where it is drawn by name"
				);
				return;
			}
			ProGPU.Text.FontApi.GetSystemFonts();
			lock (gate)
			{
				if (fontsField.GetValue(null) is not System.Collections.Generic.List<ProGPU.Text.FontInfo> catalog)
					return;
				if (_rendererFonts != null)
					catalog.RemoveAll(_rendererFonts.Contains);
				_rendererFonts = faces;
				if (faces != null)
					catalog.InsertRange(0, faces.OrderBy(face => face.Weight == 400 && !face.IsItalic ? 0 : 1));
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AppFont", "The custom font could not be registered with the renderer: " + ex.Message);
		}
	}
#else
    private static FontFamily? LoadPortable(string path) => Fonts.GetFontFamilies(new Uri(path, UriKind.Absolute)).FirstOrDefault();

    private static void SetRendererFonts(object? faces) { }
#endif

    private static void ApplyTo(FrameworkElement control)
    {
        try
        {
            object local = control.ReadLocalValue(TextElement.FontFamilyProperty);
            bool ours = local == DependencyProperty.UnsetValue || local is FontFamily family && AppliedFamilies.Contains(family);
            if (!ours)
            {
                return;
            }
            if (_current != null || Voidstrap.Utility.Platform.UsesPortableUi)
            {
                FontFamily target = CurrentFontFamily;
                if (!ReferenceEquals(local, target))
                {
                    AppliedFamilies.Add(target);
                    control.SetValue(TextElement.FontFamilyProperty, target);
                }
            }
            else if (local != DependencyProperty.UnsetValue)
            {
                control.ClearValue(TextElement.FontFamilyProperty);
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("AppFont", "The font could not be applied: " + ex.Message);
        }
    }

    public static void Apply(Window window)
    {
        if (window == null || !Voidstrap.Utility.Platform.UsesPortableUi && window is not WpfUiWindow)
        {
            return;
        }
        ApplyTo(window);
    }

    public static void ApplyToAllWindows()
    {
        Application current = Application.Current;
        if (current == null)
        {
            return;
        }
        if (Voidstrap.Utility.Platform.UsesPortableUi)
        {
            current.Resources["ContentControlThemeFontFamily"] = CurrentFontFamily;
        }
        foreach (object key in SystemFontKeys)
        {
            if (_current != null || Voidstrap.Utility.Platform.UsesPortableUi)
                current.Resources[key] = CurrentFontFamily;
            else
                current.Resources.Remove(key);
        }
        foreach (Window window in current.Windows)
        {
            Apply(window);
            ApplyToPages(window);
        }
    }

    private static void ApplyToPages(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Page page)
            {
                ApplyTo(page);
            }
            ApplyToPages(child);
        }
    }

    public static bool SetFromFile(string sourcePath)
    {
        App.Settings.Prop.AppFontPath = sourcePath;
        App.Settings.Save();
        Load();
        ApplyToAllWindows();
        return _current != null;
    }

    public static void Clear()
    {
        App.Settings.Prop.AppFontPath = "";
        App.Settings.Save();
        if (_current != null)
            AppliedFamilies.Add(_current);
        _current = null;
        if (Voidstrap.Utility.Platform.UsesPortableUi)
            SetRendererFonts(null);
        ApplyToAllWindows();
    }
}
