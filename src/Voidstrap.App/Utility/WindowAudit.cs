using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.Utility;

internal static class WindowAudit
{
	private static readonly string[] Skipped = new[]
	{
		"CrosshairWindow",
		"OverlayWindow",
		"NotificationWindow",
		"MenuContainer",
		"FPSOverlayWindow",
		"GameChatOverlay",
		"ImageAdjustWindow",
		"ImageRecolorWindow"
	};

	public static void Run()
	{
		int passed = 0;
		int failed = 0;
		int skipped = 0;

		Type[] allTypes;
		try
		{
			allTypes = Assembly.GetExecutingAssembly().GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			allTypes = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
			Emit($"partial type load: {allTypes.Length} usable, {ex.LoaderExceptions.Length} load errors");
		}

		List<Type> windowTypes = allTypes
			.Where(t => !t.IsAbstract && typeof(Window).IsAssignableFrom(t))
			.OrderBy(t => t.Name)
			.ToList();

		Emit($"window audit: {windowTypes.Count} window types discovered");
		ShutdownMode previousShutdownMode = ShutdownMode.OnLastWindowClose;
		if (Application.Current != null)
		{
			Application.Current.DispatcherUnhandledException += OnProbeDispatcherException;
			AppDomain.CurrentDomain.UnhandledException += OnProbeDomainException;
			previousShutdownMode = Application.Current.ShutdownMode;
			Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		}
		AuditPlacement();
		AuditIcons();
		AuditImaging();
		AuditTransitions();
		PrepareFixtures();

		foreach (Type type in windowTypes)
		{
			if (Skipped.Contains(type.Name))
			{
				skipped++;
				Emit($"SKIP  {type.Name} (windows only feature)");
				continue;
			}
			ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
			object?[]? args = null;
			if (ctor == null)
			{
				ctor = PickConstructor(type, out args);
			}
			if (ctor == null)
			{
				skipped++;
				Emit($"SKIP  {type.Name} (no constructable signature)");
				continue;
			}
			string stage = "construct";
			try
			{
				Window window = (Window)ctor.Invoke(args);
				stage = "show";
				window.ShowInTaskbar = false;
				window.Show();
				stage = "render";
				Pump();
				string placement = (double.IsNaN(window.Left) || double.IsNaN(window.Top))
					? "pos=unset"
					: $"pos={window.Left:F0},{window.Top:F0}";
				if (type.Name == "MainWindow")
				{
					NavigationProbe(window);
				}
				stage = "close";
				window.Close();
				Pump();
				passed++;
				Emit($"PASS  {type.Name} {placement}");
			}
			catch (Exception ex)
			{
				failed++;
				Exception root = ex;
				while (root.InnerException != null)
				{
					root = root.InnerException;
				}
				Emit($"FAIL  {type.Name} [{stage}] {root.GetType().Name}: {root.Message.Split('\n')[0]}");
				string[] frames = (root.StackTrace ?? "").Split('\n');
				int shown = 0;
				for (int f = 0; f < frames.Length && shown < 6; f++)
				{
					string frame = frames[f].Trim();
					if (frame.Contains("Voidstrap", StringComparison.Ordinal))
					{
						Emit($"        {frame}");
						shown++;
					}
				}
				if (shown == 0 && frames.Length > 0)
				{
					Emit($"        {frames[0].Trim()}");
				}
			}
		}

		AuditViewModels(allTypes);

		if (Application.Current != null)
		{
			Application.Current.DispatcherUnhandledException -= OnProbeDispatcherException;
			AppDomain.CurrentDomain.UnhandledException -= OnProbeDomainException;
			Application.Current.ShutdownMode = previousShutdownMode;
		}
		Emit($"window audit complete: {passed} passed, {failed} failed, {skipped} skipped");
	}

	private static void AuditViewModels(Type[] allTypes)
	{
		List<Type> viewModels = allTypes
			.Where(t => !t.IsAbstract && !t.IsInterface && t.Name.EndsWith("ViewModel", StringComparison.Ordinal))
			.OrderBy(t => t.Name)
			.ToList();
		Emit($"view model audit: {viewModels.Count} types");
		int failed = 0;
		foreach (Type type in viewModels)
		{
			ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
			object?[]? args = null;
			if (ctor == null)
			{
				ctor = PickConstructor(type, out args);
			}
			if (ctor == null)
			{
				continue;
			}
			try
			{
				_probeErrors.Clear();
				ctor.Invoke(args);
				Pump();
				if (_probeErrors.Count > 0)
				{
					failed++;
					Emit($"  VM DEFER {type.Name}: {_probeErrors[0]}");
				}
			}
			catch (Exception ex)
			{
				Exception root = ex;
				while (root.InnerException != null)
				{
					root = root.InnerException;
				}
				if (root is DllNotFoundException || root is EntryPointNotFoundException || root is PlatformNotSupportedException || root is TypeInitializationException)
				{
					failed++;
					Emit($"  VM FAIL {type.Name}: {root.GetType().Name} {root.Message.Split('\n')[0]}");
					foreach (string line in (root.StackTrace ?? "").Split('\n'))
					{
						if (line.Contains("Voidstrap", StringComparison.Ordinal))
						{
							Emit($"           {line.Trim()}");
							break;
						}
					}
				}
			}
		}
		Emit($"view model audit complete: {failed} platform failure(s)");
	}

	private static void AuditPlacement()
	{
		Emit("placement audit:");
		try
		{
			Emit($"  wpf reports {System.Windows.SystemParameters.PrimaryScreenWidth}x{System.Windows.SystemParameters.PrimaryScreenHeight}");
			(double realWidth, double realHeight) = Voidstrap.Utility.ScreenMetrics.GetPrimary();
			Emit($"  real screen {realWidth}x{realHeight}");
			System.Windows.Rect work = System.Windows.SystemParameters.WorkArea;
			Emit($"  workarea {work.Width}x{work.Height} at {work.Left},{work.Top}");
		}
		catch (Exception ex)
		{
			Emit($"  screen metrics FAIL: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
		}
		try
		{
			Window probe = new Window
			{
				Width = 400,
				Height = 300,
				WindowStartupLocation = WindowStartupLocation.CenterScreen,
				ShowInTaskbar = false,
				Title = "placement probe"
			};
			probe.Show();
			Pump();
			double expectedLeft = (System.Windows.SystemParameters.PrimaryScreenWidth - 400) / 2.0;
			double expectedTop = (System.Windows.SystemParameters.PrimaryScreenHeight - 300) / 2.0;
			Emit($"  CenterScreen actual {probe.Left:F0},{probe.Top:F0}  expected about {expectedLeft:F0},{expectedTop:F0}");
			probe.Left = 137.0;
			probe.Top = 89.0;
			Pump();
			Emit($"  explicit set Left=137 Top=89 -> reads back {probe.Left:F0},{probe.Top:F0}");
			probe.Close();
			Pump();
		}
		catch (Exception ex)
		{
			Emit($"  placement probe FAIL: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
		}
	}

	private static void AuditIcons()
	{
		Emit("icon audit:");
		foreach (Voidstrap.Enums.BootstrapperIcon icon in Enum.GetValues<Voidstrap.Enums.BootstrapperIcon>())
		{
			try
			{
				System.Windows.Media.ImageSource source = Voidstrap.Extensions.IconEx.GetIconSource(icon);
				if (source is System.Windows.Media.Imaging.BitmapSource bitmap)
				{
					Emit($"  ICON  {icon} -> {bitmap.PixelWidth}x{bitmap.PixelHeight}");
				}
				else
				{
					Emit($"  ICON  {icon} -> loaded ({source.GetType().Name})");
				}
			}
			catch (Exception ex)
			{
				Emit($"  ICON FAIL {icon}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
			}
		}
		foreach (string key in new[] { "FluentSystemIcons", "FluentSystemIconsFilled" })
		{
			try
			{
				if (System.Windows.Application.Current.Resources[key] is System.Windows.Media.FontFamily resolved)
				{
					Emit($"  FONT  resource '{key}' -> {resolved.Source} glyphs={(Voidstrap.Utility.IconFontLoader.HasGlyphs(resolved) ? "PRESENT" : "MISSING")}");
				}
				else
				{
					Emit($"  FONT  resource '{key}' not overridden (using built in pack resource)");
				}
			}
			catch (Exception ex)
			{
				Emit($"  FONT FAIL resource {key}: {ex.Message.Split('\n')[0]}");
			}
		}
		foreach (string spec in new[]
		{
			"pack://application:,,,/Wpf.Ui;component/Fonts/#FluentSystemIcons-Regular",
			"pack://application:,,,/Wpf.Ui;component/Fonts/#FluentSystemIcons-Filled"
		})
		{
			try
			{
				System.Windows.Media.FontFamily family = new System.Windows.Media.FontFamily(spec);
				var typefaces = family.GetTypefaces();
				string label = spec.Substring(spec.IndexOf('#') + 1);
				if (typefaces.Count == 0)
				{
					Emit($"  FONT FAIL {label}: no typefaces resolved");
					continue;
				}
				bool mapped = false;
				string detail = "no glyph typeface";
				foreach (System.Windows.Media.Typeface typeface in typefaces)
				{
					if (typeface.TryGetGlyphTypeface(out System.Windows.Media.GlyphTypeface glyphTypeface))
					{
						bool hasArrow = glyphTypeface.CharacterToGlyphMap.ContainsKey(0xE0EB);
						detail = $"glyphs={glyphTypeface.GlyphCount} arrowRight16={(hasArrow ? "present" : "MISSING")}";
						mapped = hasArrow;
						break;
					}
				}
				Emit($"  FONT  {label}: typefaces={typefaces.Count} {detail} usable={mapped}");
			}
			catch (Exception ex)
			{
				Emit($"  FONT FAIL {spec}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
			}
		}
	}

	private static void AuditTransitions()
	{
		Emit("transition audit:");
		Window? probe = null;
		try
		{
			System.Windows.Controls.Border target = new System.Windows.Controls.Border
			{
				Width = 240,
				Height = 120,
				Background = System.Windows.Media.Brushes.Black
			};
			probe = new Window
			{
				Width = 260,
				Height = 160,
				Content = target,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(40);

			bool started = Wpf.Ui.Animations.Transitions.ApplyTransition(
				target,
				Wpf.Ui.Animations.TransitionType.FadeInWithSlideLeft,
				400);
			Pump(100);
			System.Windows.Media.TranslateTransform? transform = FindTranslateTransform(target.RenderTransform);
			double middleOpacity = target.Opacity;
			double middleOffset = transform?.X ?? 0;

			for (int i = 0; i < 30; i++)
			{
				Wpf.Ui.Animations.Transitions.ApplyTransition(
					target,
					i % 2 == 0
						? Wpf.Ui.Animations.TransitionType.FadeInWithSlideRight
						: Wpf.Ui.Animations.TransitionType.FadeInWithSlideLeft,
					400);
			}
			Pump(500);

			bool moved = Math.Abs(middleOffset) > 0.05 && Math.Abs(middleOffset) < 16;
			bool faded = middleOpacity > 0.05 && middleOpacity < 0.99;
			bool settled = Math.Abs(target.Opacity - 1) < 0.001
				&& transform != null
				&& Math.Abs(transform.X) < 0.001
				&& !target.HasAnimatedProperties
				&& !transform.HasAnimatedProperties;
			Emit(started && moved && faded && settled
				? $"  transition PASS: opacity {middleOpacity:F2}, offset {middleOffset:F2}, rapid navigation settled"
				: $"  transition FAIL: started {started}, opacity {middleOpacity:F2}, offset {middleOffset:F2}, settled {settled}");
		}
		catch (Exception ex)
		{
			Emit($"  transition FAIL: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			probe?.Close();
			Pump(40);
		}
	}

	private static void AuditImaging()
	{
		try
		{
			byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+X8XzAAAAAElFTkSuQmCC");
		System.Windows.Media.Imaging.BitmapSource? image = SafeImaging.FromBytes(png, 1);
		Emit(image?.PixelWidth == 1 && image.PixelHeight == 1 && image.IsFrozen ? "image audit: PASS" : "image audit: FAIL");
		}
		catch (Exception ex)
		{
			Emit($"image audit: FAIL {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
		}
	}

	private static System.Windows.Media.TranslateTransform? FindTranslateTransform(System.Windows.Media.Transform? transform)
	{
		if (transform is System.Windows.Media.TranslateTransform translated)
		{
			return translated;
		}
		if (transform is not System.Windows.Media.TransformGroup group)
		{
			return null;
		}
		foreach (System.Windows.Media.Transform child in group.Children)
		{
			if (FindTranslateTransform(child) is System.Windows.Media.TranslateTransform found)
			{
				return found;
			}
		}
		return null;
	}

	private static readonly List<string> _probeErrors = new List<string>();

	private static void OnProbeDomainException(object? sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception exception)
		{
			RecordProbeFailure(exception);
		}
	}

	private static void OnProbeDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
	{
		RecordProbeFailure(e.Exception);
		e.Handled = true;
	}

	private static void RecordProbeFailure(Exception exception)
	{
		Exception root = exception;
		while (root.InnerException != null)
		{
			root = root.InnerException;
		}
		string frame = "";
		foreach (string line in (root.StackTrace ?? "").Split('\n'))
		{
			if (line.Contains("Voidstrap", StringComparison.Ordinal))
			{
				frame = " at " + line.Trim();
				break;
			}
		}
		_probeErrors.Add($"{root.GetType().Name}: {root.Message.Split('\n')[0]}{frame}");
	}

	private static string ForceRender(Window window)
	{
		try
		{
			window.UpdateLayout();
			object? content = window.GetType().GetProperty("RootFrame")?.GetValue(window) ?? window.FindName("RootFrame");
			if (content is System.Windows.Controls.Frame frame && frame.Content is FrameworkElement page)
			{
				page.UpdateLayout();
				page.Measure(new Size(window.ActualWidth, window.ActualHeight));
				page.Arrange(new Rect(0, 0, window.ActualWidth, window.ActualHeight));
				page.UpdateLayout();
				int scrolled = ExerciseScrolling(page);
				return $" [{page.GetType().Name} {(int)page.ActualWidth}x{(int)page.ActualHeight}{(scrolled > 0 ? $" scrolled:{scrolled}" : "")}]";
			}
			return "";
		}
		catch (Exception ex)
		{
			Exception root = ex;
			while (root.InnerException != null)
			{
				root = root.InnerException;
			}
			_probeErrors.Add($"layout {root.GetType().Name}: {root.Message.Split('\n')[0]}");
			return "";
		}
	}

	private static int ExerciseScrolling(DependencyObject root)
	{
		int exercised = 0;
		foreach (System.Windows.Controls.ScrollViewer viewer in FindScrollViewers(root))
		{
			try
			{
				if (viewer.ScrollableHeight <= 0.0)
				{
					continue;
				}
				exercised++;
				for (int step = 1; step <= 4; step++)
				{
					viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * step / 4.0);
					viewer.UpdateLayout();
				}
				viewer.ScrollToVerticalOffset(0.0);
				viewer.UpdateLayout();
			}
			catch (Exception ex)
			{
				RecordProbeFailure(ex);
			}
		}
		return exercised;
	}

	private static List<System.Windows.Controls.ScrollViewer> FindScrollViewers(DependencyObject root)
	{
		List<System.Windows.Controls.ScrollViewer> found = new List<System.Windows.Controls.ScrollViewer>();
		if (root is System.Windows.Controls.ScrollViewer viewer)
		{
			found.Add(viewer);
		}
		int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			found.AddRange(FindScrollViewers(System.Windows.Media.VisualTreeHelper.GetChild(root, i)));
		}
		return found;
	}

	private static void NavigationProbe(Window window)
	{
		Emit("navigation audit:");
		object? navigation = null;
		try
		{
			navigation = window.GetType().GetProperty("RootNavigation")?.GetValue(window)
				?? window.FindName("RootNavigation");
		}
		catch
		{
		}
		if (navigation is not Wpf.Ui.Controls.Interfaces.INavigation nav)
		{
			Emit("  navigation control not found");
			return;
		}

		List<string> tags = new List<string>();
		foreach (string collectionName in new[] { "Items", "Footer" })
		{
			try
			{
				if (navigation.GetType().GetProperty(collectionName)?.GetValue(navigation) is not System.Collections.IEnumerable entries)
				{
					continue;
				}
				foreach (object entry in entries)
				{
					string? tag = entry.GetType().GetProperty("PageTag")?.GetValue(entry) as string;
					if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag))
					{
						tags.Add(tag);
					}
				}
			}
			catch
			{
			}
		}
		Emit($"  nav targets: {tags.Count}");

		for (int i = 0; i < tags.Count; i++)
		{
			string label = tags[i];
			try
			{
				_probeErrors.Clear();
				nav.Navigate(label);
				Pump();
				string render = ForceRender(window);
				Pump();
				Pump();
				if (_probeErrors.Count > 0)
				{
					Emit($"  NAV DEFER {label}: {_probeErrors.Count} background failure(s)");
					foreach (string deferred in _probeErrors)
					{
						Emit($"           {deferred}");
					}
				}
				else
				{
					Emit($"  NAV OK   {label}{render}");
				}
			}
			catch (Exception ex)
			{
				Exception root = ex;
				while (root.InnerException != null)
				{
					root = root.InnerException;
				}
				Emit($"  NAV FAIL {label}: {root.GetType().Name} {root.Message.Split('\n')[0]}");
				string[] outerFrames = (ex.StackTrace ?? "").Split('\n');
				for (int f = 0; f < Math.Min(8, outerFrames.Length); f++)
				{
					if (!string.IsNullOrWhiteSpace(outerFrames[f]))
					{
						Emit($"   OUTER   {outerFrames[f].Trim()}");
					}
				}
				string[] navFrames = (root.StackTrace ?? "").Split('\n');
				for (int f = 0; f < Math.Min(16, navFrames.Length); f++)
				{
					string frame = navFrames[f].Trim();
					if (string.IsNullOrWhiteSpace(frame))
					{
						continue;
					}
					if (frame.Contains("Voidstrap", StringComparison.Ordinal)
						|| frame.Contains("Baml", StringComparison.Ordinal)
						|| frame.Contains("MediaElement", StringComparison.Ordinal)
						|| frame.Contains("Wpf.Ui", StringComparison.Ordinal)
						|| f < 6)
					{
						Emit($"           {frame}");
					}
				}
			}
		}
	}

	public static void RenderProbe(Window window, string label)
	{
		try
		{
			int w = (int)Math.Max(1.0, window.ActualWidth);
			int h = (int)Math.Max(1.0, window.ActualHeight);
			if (w < 8 || h < 8)
			{
				Emit($"  RENDER {label}: window too small ({w}x{h})");
				return;
			}
			System.Windows.Media.Imaging.RenderTargetBitmap target =
				new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
			target.Render(window);

			int stride = w * 4;
			byte[] pixels = new byte[stride * h];
			target.CopyPixels(pixels, stride, 0);

			long nonBlack = 0;
			long distinct = 0;
			int lastColor = -1;
			for (int i = 0; i < pixels.Length; i += 4)
			{
				int b = pixels[i];
				int g = pixels[i + 1];
				int r = pixels[i + 2];
				if (r > 12 || g > 12 || b > 12)
				{
					nonBlack++;
				}
				int color = (r << 16) | (g << 8) | b;
				if (color != lastColor)
				{
					distinct++;
					lastColor = color;
				}
			}
			long total = pixels.Length / 4;
			double percent = total > 0 ? (nonBlack * 100.0 / total) : 0.0;
			Emit($"  RENDER {label}: {w}x{h} nonBlack={percent:F1}% colorRuns={distinct}");

			string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Voidstrap", "render-" + label + ".png");
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
			System.Windows.Media.Imaging.PngBitmapEncoder encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
			encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
			using System.IO.FileStream fs = System.IO.File.Create(outPath);
			encoder.Save(fs);
			Emit($"  RENDER {label}: saved {outPath}");
		}
		catch (Exception ex)
		{
			Exception root = ex;
			while (root.InnerException != null)
			{
				root = root.InnerException;
			}
			Emit($"  RENDER {label} FAIL: {root.GetType().Name} {root.Message.Split('\n')[0]}");
		}
	}

	private static void PrepareFixtures()
	{
		try
		{
			string themeDirectory = System.IO.Path.Combine(Paths.CustomThemes, "audit");
			System.IO.Directory.CreateDirectory(themeDirectory);
			string themeFile = System.IO.Path.Combine(themeDirectory, "Theme.xml");
			if (!System.IO.File.Exists(themeFile))
			{
				System.IO.File.WriteAllText(themeFile, "<Theme></Theme>");
			}
		}
		catch (Exception ex)
		{
			Emit("fixture setup failed: " + ex.Message);
		}
	}

	private static ConstructorInfo? PickConstructor(Type type, out object?[]? arguments)
	{
		arguments = null;
		foreach (ConstructorInfo candidate in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
		{
			ParameterInfo[] parameters = candidate.GetParameters();
			object?[] values = new object?[parameters.Length];
			bool usable = true;
			for (int i = 0; i < parameters.Length; i++)
			{
				if (!TryDefault(parameters[i], out values[i]))
				{
					usable = false;
					break;
				}
			}
			if (usable)
			{
				arguments = values;
				return candidate;
			}
		}
		return null;
	}

	private static bool TryDefault(ParameterInfo parameter, out object? value)
	{
		Type t = parameter.ParameterType;
		value = null;
		if (parameter.HasDefaultValue)
		{
			value = parameter.DefaultValue;
			return true;
		}
		if (t == typeof(string))
		{
			value = "audit";
			return true;
		}
		if (t.IsEnum)
		{
			value = Enum.GetValues(t).GetValue(0);
			return true;
		}
		if (t.IsValueType)
		{
			value = Activator.CreateInstance(t);
			return true;
		}
		if (!t.IsAbstract && !t.IsInterface && !typeof(Window).IsAssignableFrom(t))
		{
			try
			{
				ConstructorInfo? dependency = PickConstructor(t, out object?[]? dependencyArgs);
				if (dependency != null)
				{
					value = dependency.Invoke(dependencyArgs);
					return true;
				}
			}
			catch
			{
			}
		}
		if (t.IsClass || t.IsInterface)
		{
			value = null;
			return true;
		}
		return false;
	}

	private static void Pump(int milliseconds = 120)
	{
		if (milliseconds <= 0)
		{
			return;
		}
		try
		{
			DispatcherFrame frame = new DispatcherFrame();
			DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
			{
				Interval = TimeSpan.FromMilliseconds(milliseconds)
			};
			EventHandler? elapsed = null;
			elapsed = delegate
			{
				timer.Stop();
				timer.Tick -= elapsed;
				frame.Continue = false;
			};
			timer.Tick += elapsed;
			try
			{
				timer.Start();
				Dispatcher.PushFrame(frame);
			}
			finally
			{
				timer.Stop();
				timer.Tick -= elapsed;
			}
		}
		catch
		{
		}
	}

	private static void Emit(string line)
	{
		App.Logger.WriteLine("WindowAudit", line);
	}
}
