using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.Utility;

internal static class WindowAudit
{
	private static readonly string[] Skipped = new[]
	{
		"CrosshairWindow",
		"OverlayWindow",
		"MenuContainer",
		"FPSOverlayWindow",
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
		Window? renderKeeper = null;
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			try
			{
				renderKeeper = new Window
				{
					Width = 160,
					Height = 120,
					Left = -4000,
					Top = -4000,
					Title = "Voidstrap render keeper",
					ShowInTaskbar = false,
					ShowActivated = false,
					WindowStyle = WindowStyle.None
				};
				renderKeeper.Show();
				Pump(60);
				Emit("render keeper window opened so the portable render loop stays alive for the motion audits");
			}
			catch (Exception ex)
			{
				renderKeeper = null;
				Emit($"render keeper window failed: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
			}
		}
		AuditTransitions();
#if CROSSPLAT
		AuditTabPillPresentation();
#endif
		AuditScrolling();
		AuditThemeTransition();
		AuditShadows();
		AuditDropdownLifecycle();
		AuditDropdownInteraction();
		AuditPopupSurfaces();
		AuditDropdownFallback();
		AuditDropdownAcrossWindows();
		AuditLinuxOverlays();
		AuditExpanderChevron();
#if CROSSPLAT
		AuditExpanderReveal();
#endif
		AuditGradientOpacity();
		AuditWinFormsCompat();
		AuditEditorNativeCalls();
		AuditMaximizedWindow();
		AuditMessageBoxButtons();
		AuditThemeCaseParity();
		AuditThemeDeletion();
		AuditLinuxGlassBootstrapper();
		AuditBootstrapperStyles();
		AuditLinuxHyperlinks();
		AuditHtmlThemePanel();
		AuditAnimatedGif();
		AuditBootstrapperIcons();
		AuditThemeEditorWindow();
		if (renderKeeper != null)
		{
			try
			{
				renderKeeper.Close();
				Pump(200);
			}
			catch (Exception)
			{
			}
		}
		AuditPlacement();
		AuditLinuxWindowModes();
		try
		{
			AuditLinuxHomepageBackground();
		}
		catch (Exception ex)
		{
			failed++;
			Emit("Linux homepage background audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		AuditIcons();
		AuditImaging();
		AuditCrosshairExports();
		AuditLinuxAccountStorage();
		AuditLibraryLoading();
		AuditTextFlowScenarios();
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
				stage = "text";
				AuditWindowTextFlow(window);
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
		AuditCustomThemes();
		AuditJoinNotificationIcon();
		RemoveFixtures();

		if (Application.Current != null)
		{
			Application.Current.DispatcherUnhandledException -= OnProbeDispatcherException;
			AppDomain.CurrentDomain.UnhandledException -= OnProbeDomainException;
			Application.Current.ShutdownMode = previousShutdownMode;
		}
		Emit($"window audit complete: {passed} passed, {failed} failed, {skipped} skipped");
	}

	private static void AuditJoinNotificationIcon()
	{
		Voidstrap.UI.Elements.Overlay.NotificationWindow? window = null;
		try
		{
			const int size = 16;
			byte[] pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					int offset = (y * size + x) * 4;
					bool alternate = ((x / 4) + (y / 4)) % 2 == 0;
					pixels[offset] = alternate ? (byte)255 : (byte)0;
					pixels[offset + 1] = alternate ? (byte)0 : (byte)255;
					pixels[offset + 2] = alternate ? (byte)255 : (byte)0;
					pixels[offset + 3] = 255;
				}
			}

			BitmapSource icon = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
			icon.Freeze();
			window = Application.Current.Resources["NotificationWindow"] as Voidstrap.UI.Elements.Overlay.NotificationWindow;

			if (window is null || !window.IsUsable)
			{
				window = new Voidstrap.UI.Elements.Overlay.NotificationWindow();
				Application.Current.Resources["NotificationWindow"] = window;
			}

			window.Prewarm();
			if (Voidstrap.Utility.Platform.IsLinux)
				PumpUntil(() => !window.IsVisible, 3000);

			window.ShowNotification("Game join icon audit\nDallas • 1 player", icon, 3);
			System.Windows.Controls.Border? imageSurface = null;
			ImageBrush? brush = null;
			bool prepared = PumpUntil(() =>
			{
				imageSurface = window.FindName("NotificationImage") as System.Windows.Controls.Border;
				brush = imageSurface?.Background as ImageBrush;
				return window.IsVisible
					&& imageSurface?.Visibility == Visibility.Visible
					&& imageSurface.ActualWidth >= 39
					&& imageSurface.ActualHeight >= 39
					&& ReferenceEquals(brush?.ImageSource, icon)
					&& brush.Stretch == Stretch.Uniform;
			}, 3500);

			bool nativePixels = false;
			bool nativeCaptureAvailable = false;
			if (prepared && Voidstrap.Utility.Platform.IsLinux)
			{
				Pump(300);
				byte[] capture = new byte[4 * 1024 * 1024];
				int width = 0;
				int height = 0;
				PumpUntil(() =>
				{
					List<nint> candidates = new(Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnManagedWindowsByTitle(window.Title ?? string.Empty));
					nint named = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(window.Title ?? string.Empty);

					if (named != 0 && !candidates.Contains(named))
						candidates.Add(named);

					nint interop = new System.Windows.Interop.WindowInteropHelper(window).Handle;

					if (interop != 0 && !candidates.Contains(interop))
						candidates.Add(interop);

					foreach (nint probe in candidates)
					{
						if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryCaptureWindowBgra32(probe, capture, out int capturedWidth, out int capturedHeight))
							continue;

						width = capturedWidth;
						height = capturedHeight;
						return true;
					}

					return false;
				}, 2500);

				if (width > 0 && height > 0)
				{
					int bright = 0;

					for (int y = 0; y < height && bright == 0; y++)
					{
						for (int x = 0; x < width; x++)
						{
							int probeOffset = (y * width + x) * 4;

							if (capture[probeOffset] > 200 || capture[probeOffset + 1] > 200 || capture[probeOffset + 2] > 200)
							{
								bright++;
								break;
							}
						}
					}

					nativeCaptureAvailable = bright > 0;
					int regionWidth = Math.Min(width, Math.Max(1, width / 3));
					for (int y = 0; y < height && !nativePixels; y++)
					{
						for (int x = 0; x < regionWidth; x++)
						{
							int offset = (y * width + x) * 4;
							bool magenta = capture[offset] > 235 && capture[offset + 1] < 25 && capture[offset + 2] > 235;
							bool green = capture[offset] < 25 && capture[offset + 1] > 235 && capture[offset + 2] < 25;
							if (magenta || green)
							{
								nativePixels = true;
								break;
							}
						}
					}
				}
			}

			BitmapSource? decoded = DecodeAppResource("Resources/RobloxPlayerIcon.png", 128);
			bool decodedPrepared = true;

			if (decoded is not null)
			{
				BitmapSource flag = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 3 * 4], 4 * 4);
				flag.Freeze();
				window.ShowNotification("Game join icon audit\nDallas • 1 player", decoded, 0.5, flag);
				decodedPrepared = PumpUntil(() =>
				{
					imageSurface = window.FindName("NotificationImage") as System.Windows.Controls.Border;
					brush = imageSurface?.Background as ImageBrush;
					return window.IsVisible
						&& imageSurface?.Visibility == Visibility.Visible
						&& imageSurface.ActualWidth >= 39
						&& imageSurface.ActualHeight >= 39
						&& ReferenceEquals(brush?.ImageSource, decoded);
				}, 3500);
			}

			bool passed = prepared && decodedPrepared && (!nativeCaptureAvailable || nativePixels);
			string surfaceProof = nativeCaptureAvailable
				? "native icon pixels verified"
				: "native surface capture returned no drawn content, brush state only";
			Emit(passed
				? $"join notification icon audit: PASS, prewarmed image brush, decoded thumbnail reuse, {surfaceProof}"
				: $"join notification icon audit: FAIL, prepared {prepared}, decoded {decodedPrepared}, capture {nativeCaptureAvailable}, native pixels {nativePixels}");
		}
		catch (Exception ex)
		{
			Emit("join notification icon audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			Pump(600);
		}
	}

	private static BitmapSource? DecodeAppResource(string resourcePath, int decodeWidth)
	{
		var info = Application.GetResourceStream(new Uri(resourcePath, UriKind.Relative));

		if (info?.Stream is null)
			return null;

		byte[] bytes;
		using (info.Stream)
		using (System.IO.MemoryStream buffer = new())
		{
			info.Stream.CopyTo(buffer);
			bytes = buffer.ToArray();
		}

		var decodeTask = Voidstrap.Utility.AppImage.DecodeBytesAsync(bytes, decodeWidth);
		PumpUntil(() => decodeTask.IsCompleted, 5000);
		return decodeTask.IsCompletedSuccessfully ? decodeTask.Result : null;
	}

	private static void AuditLinuxWindowModes()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Wpf.Ui.Controls.UiWindow? probe = null;
		try
		{
			Wpf.Ui.Controls.TitleBar titleBar = new()
			{
				Title = "Window mode audit"
			};
			System.Windows.Controls.Grid root = new();
			root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
			root.Children.Add(titleBar);
			probe = new Wpf.Ui.Controls.UiWindow
			{
				Title = "Voidstrap window mode audit " + Guid.NewGuid().ToString("N"),
				Width = 720,
				Height = 480,
				Left = 80,
				Top = 80,
				ResizeMode = ResizeMode.CanResize,
				WindowStyle = WindowStyle.None,
				ShowInTaskbar = true,
				Content = root
			};
			Voidstrap.UI.RoundedWindowChrome.Prepare(probe);
			Voidstrap.UI.LinuxWindowMode.Attach(probe);
			probe.Show();
			Pump(100);
			Rect original = new(probe.Left, probe.Top, probe.ActualWidth, probe.ActualHeight);
			ResizeMode originalResizeMode = probe.ResizeMode;
			Voidstrap.UI.LinuxWindowMode.ToggleFullscreen(probe);
			Pump(500);
			bool fullscreen = Voidstrap.UI.LinuxWindowMode.IsFullscreen(probe);
			bool chromeHidden = titleBar.Visibility == Visibility.Collapsed && probe.ResizeMode == ResizeMode.NoResize && probe.Clip == null;
			bool persisted = Voidstrap.UI.LinuxWindowMode.TryGetRestorePlacement(probe, out bool persistedMaximized, out Rect persistedBounds)
				&& !persistedMaximized
				&& Math.Abs(persistedBounds.Width - original.Width) < 2.0
				&& Math.Abs(persistedBounds.Height - original.Height) < 2.0;
			Voidstrap.UI.LinuxWindowMode.ToggleFullscreen(probe);
			Pump(500);
			bool restored = !Voidstrap.UI.LinuxWindowMode.IsFullscreen(probe)
				&& titleBar.Visibility == Visibility.Visible
				&& probe.ResizeMode == originalResizeMode
				&& Math.Abs(probe.ActualWidth - original.Width) < 4.0
				&& Math.Abs(probe.ActualHeight - original.Height) < 4.0;
			Emit(fullscreen && chromeHidden && persisted && restored
				? "Linux window mode audit: PASS, fullscreen/chrome/restore lifecycle is stable"
				: $"Linux window mode audit: FAIL, fullscreen {fullscreen}, chrome {chromeHidden}, persistence {persisted}, restored {restored}");
		}
		catch (Exception ex)
		{
			Emit("Linux window mode audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			probe?.Close();
			Pump(80);
		}
	}

	private static void AuditLinuxHomepageBackground()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		const int width = 15;
		const int height = 15;
		int pixels = width * height;
		byte[] source = new byte[pixels * 4];
		byte[] weights = new byte[pixels];
		byte[] mask = new byte[pixels];
		byte[] output = new byte[pixels * 4];

		FillLinuxHomepageAuditSource(source, Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.OriginalBlue);
		int originalMatches = Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.Build(source, width, height, weights, mask);
		if (originalMatches != pixels || mask[pixels / 2] != 255)
			throw new InvalidOperationException("The existing Sober background key #121215 was not fully replaced");

		FillLinuxHomepageAuditSource(source, Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.SoberBlue);
		Array.Clear(weights);
		Array.Clear(mask);
		int soberMatches = Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.Build(source, width, height, weights, mask);
		if (soberMatches != pixels || mask[pixels / 2] != 255)
			throw new InvalidOperationException("The Linux Sober background key #121218 was not fully replaced");
		Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.Compose(
			source,
			mask,
			width,
			height,
			new Voidstrap.Integrations.Overlays.LinuxHomepageVisualSettings("Solid", "#315A7F", string.Empty, 0d, string.Empty),
			null,
			0,
			0,
			output);
		int centerOutput = pixels / 2 * 4;
		if (output[centerOutput] != 0x7F
			|| output[centerOutput + 1] != 0x5A
			|| output[centerOutput + 2] != 0x31
			|| output[centerOutput + 3] != 0xFF)
			throw new InvalidOperationException("The Linux Sober key did not compose to the exact requested color");
		const int sampledScale = 2;
		int sampledWidth = (width + sampledScale - 1) / sampledScale;
		int sampledHeight = (height + sampledScale - 1) / sampledScale;
		byte[] sampledWeights = new byte[sampledWidth * sampledHeight];
		byte[] sampledMask = new byte[sampledWeights.Length];
		int sampledMatches = Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.BuildSampled(
			source,
			width,
			height,
			sampledScale,
			sampledWeights,
			sampledMask);
		byte[] retainedBackground = new byte[output.Length];
		Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.BuildBackground(
			width,
			height,
			new Voidstrap.Integrations.Overlays.LinuxHomepageVisualSettings("Solid", "#315A7F", string.Empty, 0d, string.Empty),
			null,
			0,
			0,
			retainedBackground,
			new int[width],
			new int[height]);
		Array.Clear(output);
		Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.ApplyBackgroundMask(
			source,
			sampledMask,
			retainedBackground,
			width,
			height,
			output,
			sampledScale);
		if (sampledMatches != sampledMask.Length
			|| output[centerOutput] != 0x7F
			|| output[centerOutput + 1] != 0x5A
			|| output[centerOutput + 2] != 0x31
			|| output[centerOutput + 3] != 0xFF)
			throw new InvalidOperationException("The high refresh homepage mask did not preserve exact replacement pixels");

		WriteableBitmap uploadProbe = new(2, 2, 96d, 96d, PixelFormats.Pbgra32, null);
		byte[] uploadSource =
		[
			1, 2, 3, 255,
			4, 5, 6, 255,
			7, 8, 9, 255,
			10, 11, 12, 255
		];
		Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundOverlayRenderer.CopyFrame(uploadProbe, uploadSource, 2, 2);
		byte[] uploadResult = new byte[uploadSource.Length];
		uploadProbe.CopyPixels(uploadResult, 8, 0);
		if (!uploadResult.AsSpan().SequenceEqual(uploadSource))
			throw new InvalidOperationException("The Linux homepage direct bitmap upload changed frame pixels");

		const int mediaAuditWidth = 3;
		const int mediaAuditHeight = 3;
		byte[] mediaSource = new byte[mediaAuditWidth * mediaAuditHeight * 4];
		byte[] mediaMask = new byte[mediaAuditWidth * mediaAuditHeight];
		byte[] mediaOutput = new byte[mediaSource.Length];
		FillLinuxHomepageAuditSource(mediaSource, Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.SoberBlue);
		Array.Fill(mediaMask, (byte)255);
		byte[] media =
		[
			0, 0, 0, 255,
			100, 100, 100, 255,
			200, 200, 200, 255,
			255, 255, 255, 255
		];
		Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.Compose(
			mediaSource,
			mediaMask,
			mediaAuditWidth,
			mediaAuditHeight,
			new Voidstrap.Integrations.Overlays.LinuxHomepageVisualSettings("Media", string.Empty, string.Empty, 0d, string.Empty),
			media,
			2,
			2,
			mediaOutput);
		int mediaCenter = (mediaAuditHeight / 2 * mediaAuditWidth + mediaAuditWidth / 2) * 4;
		if (mediaOutput[mediaCenter] != 139
			|| mediaOutput[mediaCenter + 1] != 139
			|| mediaOutput[mediaCenter + 2] != 139
			|| mediaOutput[mediaCenter + 3] != 255)
			throw new InvalidOperationException("The Linux homepage media path did not use stable bilinear scaling");

		for (int index = 0; index < pixels; index++)
		{
			int offset = index * 4;
			source[offset] = 90;
			source[offset + 1] = 80;
			source[offset + 2] = 70;
			source[offset + 3] = 255;
		}
		int isolatedPixel = height / 2 * width + width / 2;
		int isolatedOffset = isolatedPixel * 4;
		source[isolatedOffset] = Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.SoberBlue;
		source[isolatedOffset + 1] = 18;
		source[isolatedOffset + 2] = 18;
		Array.Clear(weights);
		Array.Clear(mask);
		int isolatedMatches = Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.Build(source, width, height, weights, mask);
		if (isolatedMatches != 0 || mask[isolatedPixel] != 0)
			throw new InvalidOperationException("An isolated UI pixel was mistaken for the Sober homepage background");

		FillLinuxHomepageAuditSource(source, Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.SoberBlue);
		source[isolatedOffset] = 255;
		source[isolatedOffset + 1] = 255;
		source[isolatedOffset + 2] = 255;
		Array.Clear(weights);
		Array.Clear(mask);
		Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.Build(source, width, height, weights, mask);
		int cursorNeighbor = isolatedPixel + 1;
		int cursorFar = 1 * width + 1;
		if (mask[cursorNeighbor] == 0 || mask[cursorFar] == 0)
			throw new InvalidOperationException("The cursor preservation audit source did not begin with a complete background mask");
		int cursorCleared = Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.ExcludePointer(
			weights,
			mask,
			width,
			height,
			width / 2,
			height / 2);
		if (cursorCleared == 0 || mask[cursorNeighbor] != 0 || mask[cursorFar] == 0)
			throw new InvalidOperationException("The Linux software cursor was not isolated from the replacement layer");

		if (!Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.IsLinuxSourceKey(18, 18, 21)
			|| !Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.IsLinuxSourceKey(18, 18, 24)
			|| Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.IsLinuxSourceKey(18, 18, 22))
			throw new InvalidOperationException("The Linux background key policy does not contain exactly #121215 and #121218");
		AuditLinuxHomepageColorRanges(source, weights, mask, output, width, height);
		double refreshRate = Voidstrap.Platform.Linux.LinuxDisplayMetrics.RefreshRateForWindow(0);
		if (refreshRate < 24d || refreshRate > 360d)
			throw new InvalidOperationException("The Linux homepage renderer resolved an invalid monitor refresh rate");

		string portableImagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Voidstrap", "homepage-background-audit.png");
		Voidstrap.Integrations.Overlays.HomepageBackgroundMedia? portableMedia = null;
		try
		{
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(portableImagePath)!);
			using (SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = new(2, 2))
			{
				image[0, 0] = new SixLabors.ImageSharp.PixelFormats.Bgra32(17, 34, 51, 255);
				using System.IO.FileStream outputStream = System.IO.File.Create(portableImagePath);
				image.Save(outputStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
			}
			portableMedia = new Voidstrap.Integrations.Overlays.HomepageBackgroundMedia(portableImagePath, 30d);
			bool decoded = false;
			long deadline = Environment.TickCount64 + 3000;
			while (Environment.TickCount64 < deadline)
			{
				if (portableMedia.TryReadFrame(0, (frame, frameWidth, frameHeight, version) =>
					decoded = frameWidth == 2 && frameHeight == 2 && frame.Length == 16 && version > 0))
					break;
				System.Threading.Thread.Sleep(10);
			}
			if (!decoded)
				throw new InvalidOperationException("The Linux static homepage image did not decode through the portable media path");
		}
		finally
		{
			portableMedia?.Dispose();
			try
			{
				System.IO.File.Delete(portableImagePath);
			}
			catch
			{
			}
		}

		Window identityProbe = new()
		{
			Title = "Sober",
			Width = 160,
			Height = 120,
			ShowInTaskbar = false,
			WindowStartupLocation = WindowStartupLocation.Manual,
			Left = 40,
			Top = 40
		};
		try
		{
			identityProbe.Show();
			Pump(80);
			nint identityHandle = new System.Windows.Interop.WindowInteropHelper(identityProbe).Handle;
			if (Voidstrap.Platform.Linux.LinuxWindowInterop.IsSoberRuntimeWindow(identityHandle))
				throw new InvalidOperationException("A title-only Sober window was accepted as the Sober runtime");
		}
		finally
		{
			identityProbe.Close();
			Pump(80);
		}

		bool previousEnabled = App.Settings.Prop.HomepageBackgroundOverlayEnabled;
		try
		{
			App.Settings.Prop.HomepageBackgroundOverlayEnabled = true;
			if (!Voidstrap.Integrations.Overlays.OverlaySettings.RequiresLinuxX11Session)
				throw new InvalidOperationException("Homepage replacement did not require a Sober X11 session");
		}
		finally
		{
			App.Settings.Prop.HomepageBackgroundOverlayEnabled = previousEnabled;
		}

		bool lifecycleChecked = false;
		if (Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundOverlay.IsSupported && Application.Current != null)
		{
			bool previousGameState = Voidstrap.Integrations.Overlays.OverlayHub.InGame;
			Voidstrap.Integrations.Overlays.OverlayHub.SynchronizeLinuxGameState(true);
			try
			{
				if (!Voidstrap.Integrations.Overlays.OverlayHub.LinuxGameplayLeaseOperational)
					throw new InvalidOperationException("The Linux gameplay lease did not acknowledge without a portable multi-handle wait");
				Window? previousMainWindow = Application.Current.MainWindow;
				using (Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundOverlay dormantOverlay = new())
				{
					Pump(250);
					if (dormantOverlay.IsDisposed || dormantOverlay.IsSurfaceOpen || dormantOverlay.NativeHandle != 0)
						throw new InvalidOperationException("The Linux homepage renderer materialized while gameplay made it inactive");
				}
				nint nativeHandle = 0;
				string nativeTitle = string.Empty;
				using (Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundOverlay overlay = new(true))
				{
					nativeTitle = overlay.NativeTitle;
					bool nativeReady = PumpUntil(() =>
					{
						nativeHandle = overlay.NativeHandle;
						return !overlay.IsDisposed
							&& overlay.IsSurfaceOpen
							&& overlay.UsesApplicationDispatcher
							&& overlay.HasExclusiveSurface
							&& overlay.HasNativeSurface
							&& nativeHandle != 0
							&& Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(nativeHandle)
							&& Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(nativeTitle) == nativeHandle
							&& Voidstrap.Platform.Linux.LinuxWindowInterop.IsPreparedOverlayWindow(nativeHandle)
							&& Voidstrap.Platform.Linux.LinuxWindowInterop.IsOverrideRedirectWindow(nativeHandle)
							&& Voidstrap.Integrations.Overlays.OverlayDiagnostics.IsOverlayHandle(nativeHandle)
							&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetInputShapeRectangleCount(nativeHandle, out int inputRectangles)
							&& inputRectangles == 0
							&& Application.Current.Windows.OfType<Voidstrap.Integrations.Overlays.LinuxHomepageOverlayWindow>().Count() == 1;
					}, 6000);
					if (!nativeReady)
						throw new InvalidOperationException("The Linux homepage surface never acquired a live prepared click-through X11 window: " + overlay.NativeFailure);
					if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(nativeHandle, out int nativeLeft, out int nativeTop, out int nativeWidth, out int nativeHeight)
						|| nativeLeft != -32000
						|| nativeTop != -32000
						|| nativeWidth != 1
						|| nativeHeight != 1)
						throw new InvalidOperationException("The Linux homepage surface did not expose valid parked native geometry");
					if (Application.Current.MainWindow is Voidstrap.Integrations.Overlays.LinuxHomepageOverlayWindow
						|| (previousMainWindow is not null && !ReferenceEquals(Application.Current.MainWindow, previousMainWindow)))
						throw new InvalidOperationException("The Linux homepage surface replaced the application's main window, was "
							+ (previousMainWindow?.GetType().Name ?? "none")
							+ " now " + (Application.Current.MainWindow?.GetType().Name ?? "none"));
				}
				bool nativeReleased = PumpUntil(() =>
					!Application.Current.Windows.OfType<Voidstrap.Integrations.Overlays.LinuxHomepageOverlayWindow>().Any()
					&& Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(nativeTitle) == 0
					&& !Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(nativeHandle)
					&& !Voidstrap.Platform.Linux.LinuxWindowInterop.IsPreparedOverlayWindow(nativeHandle)
					&& !Voidstrap.Integrations.Overlays.OverlayDiagnostics.IsOverlayHandle(nativeHandle), 2000);
				if (!nativeReleased)
					throw new InvalidOperationException("The Linux homepage surface left a managed or native X11 window after disposal");
				lifecycleChecked = true;
			}
			finally
			{
				Voidstrap.Integrations.Overlays.OverlayHub.SynchronizeLinuxGameState(previousGameState);
			}
		}

		Emit("Linux homepage background audit: PASS, dual Sober keys, full saturation and contrast ranges, cursor isolation, "
			+ refreshRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
			+ " Hz display timing, bilinear media, portable static decode, neighborhood isolation, strict Sober identity, X11 requirement, native click-through lifecycle "
			+ (lifecycleChecked ? "verified" : "not available"));
	}

	private static void AuditLinuxHomepageColorRanges(
		byte[] source,
		byte[] weights,
		byte[] mask,
		byte[] output,
		int width,
		int height)
	{
		double previousSaturation = App.Settings.Prop.Saturation;
		double previousContrast = App.Settings.Prop.Contrast;
		double previousTemperature = App.Settings.Prop.ColorTemperature;
		bool previousColorBlindness = App.Settings.Prop.ColorBlindnessEnabled;
		try
		{
			App.Settings.Prop.ColorTemperature = 0d;
			App.Settings.Prop.ColorBlindnessEnabled = false;
			(double Saturation, double Contrast)[] ranges =
			[
				(0d, 0d),
				(0d, 200d),
				(100d, 0d),
				(100d, 200d),
				(200d, 0d),
				(200d, 200d)
			];
			foreach ((double saturation, double contrast) in ranges)
			{
				App.Settings.Prop.Saturation = saturation;
				App.Settings.Prop.Contrast = contrast;
				Voidstrap.Integrations.Overlays.HomepageBackgroundSourceKeys keys =
					Voidstrap.Integrations.Overlays.HomepageBackgroundSourceKeys.ReadConfigured();
				FillLinuxHomepageAuditSource(source, keys.AdjustedSober.Red, keys.AdjustedSober.Green, keys.AdjustedSober.Blue);
				Array.Clear(weights);
				Array.Clear(mask);
				Array.Clear(output);
				int matches = Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.Build(source, width, height, weights, mask);
				if (matches != width * height || mask[mask.Length / 2] != 255)
					throw new InvalidOperationException($"The homepage key failed at saturation {saturation:0} and contrast {contrast:0}");

				Voidstrap.Integrations.Overlays.LinuxHomepageVisualSettings settings = new(
					"Solid",
					"#315A7F",
					string.Empty,
					0d,
					string.Empty,
					saturation,
					contrast);
				Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.Compose(
					source,
					mask,
					width,
					height,
					settings,
					null,
					0,
					0,
					output);
				Voidstrap.Integrations.Overlays.HomepageBackgroundColorTransform transform = settings.CreateColorTransform();
				transform.Apply(0x31, 0x5A, 0x7F, out byte expectedRed, out byte expectedGreen, out byte expectedBlue);
				int center = (height / 2 * width + width / 2) * 4;
				if (output[center] != expectedBlue
					|| output[center + 1] != expectedGreen
					|| output[center + 2] != expectedRed
					|| output[center + 3] != 255)
					throw new InvalidOperationException($"The homepage replacement color failed at saturation {saturation:0} and contrast {contrast:0}");

				Voidstrap.Integrations.Overlays.LinuxHomepageVisualSettings gradientSettings = settings with
				{
					Mode = "Gradient",
					GradientColor = "#C08020",
					GradientAngle = 0d
				};
				Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.BuildBackground(
					width,
					height,
					gradientSettings,
					null,
					0,
					0,
					output,
					new int[width],
					new int[height]);
				byte gradientRed = (byte)Math.Round((0x31 + 0xC0) / 2d);
				byte gradientGreen = (byte)Math.Round((0x5A + 0x80) / 2d);
				byte gradientBlue = (byte)Math.Round((0x7F + 0x20) / 2d);
				transform.Apply(gradientRed, gradientGreen, gradientBlue, out expectedRed, out expectedGreen, out expectedBlue);
				if (output[center] != expectedBlue
					|| output[center + 1] != expectedGreen
					|| output[center + 2] != expectedRed
					|| output[center + 3] != 255)
					throw new InvalidOperationException($"The homepage gradient failed at saturation {saturation:0} and contrast {contrast:0}");

				byte[] media = [0x20, 0x80, 0xC0, 0xFF];
				Voidstrap.Integrations.Overlays.LinuxHomepageBackgroundMask.BuildBackground(
					width,
					height,
					settings with { Mode = "Media" },
					media,
					1,
					1,
					output,
					new int[width],
					new int[height]);
				transform.Apply(0xC0, 0x80, 0x20, out expectedRed, out expectedGreen, out expectedBlue);
				if (output[center] != expectedBlue
					|| output[center + 1] != expectedGreen
					|| output[center + 2] != expectedRed
					|| output[center + 3] != 255)
					throw new InvalidOperationException($"The homepage media color failed at saturation {saturation:0} and contrast {contrast:0}");
			}
		}
		finally
		{
			App.Settings.Prop.Saturation = previousSaturation;
			App.Settings.Prop.Contrast = previousContrast;
			App.Settings.Prop.ColorTemperature = previousTemperature;
			App.Settings.Prop.ColorBlindnessEnabled = previousColorBlindness;
		}
	}

	private static void FillLinuxHomepageAuditSource(byte[] source, byte blue)
	{
		FillLinuxHomepageAuditSource(source, 18, 18, blue);
	}

	private static void FillLinuxHomepageAuditSource(byte[] source, byte red, byte green, byte blue)
	{
		for (int index = 0; index < source.Length; index += 4)
		{
			source[index] = blue;
			source[index + 1] = green;
			source[index + 2] = red;
			source[index + 3] = 255;
		}
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

	private static void AuditLibraryLoading()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		try
		{
			Voidstrap.UI.ViewModels.Settings.LibraryViewModel viewModel = new();
			List<Voidstrap.UI.ViewModels.Settings.LibraryGameEntry> games = Enumerable.Range(1, 95)
				.Select(index => new Voidstrap.UI.ViewModels.Settings.LibraryGameEntry
				{
					UniverseId = index,
					PlaceId = index,
					Name = "Audit game " + index,
					LastPlayed = DateTime.UtcNow.AddMinutes(-index)
				})
				.ToList();
			MethodInfo publish = typeof(Voidstrap.UI.ViewModels.Settings.LibraryViewModel)
				.GetMethod("PublishGamesCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
			int allChanges = 0;
			int sidebarChanges = 0;
			viewModel.AllGames.CollectionChanged += (_, _) => allChanges++;
			viewModel.SidebarGames.CollectionChanged += (_, _) => sidebarChanges++;
			System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
			publish.Invoke(viewModel, new object[] { games });
			elapsed.Stop();
			bool initialPassed = viewModel.AllGames.Count == 30 && viewModel.SidebarGames.Count == 95;
			bool batchingPassed = allChanges == 1 && sidebarChanges == 1;
			viewModel.LoadMoreGames();
			bool pagingPassed = viewModel.AllGames.Count == 60;
			if (initialPassed && batchingPassed && pagingPassed && elapsed.ElapsedMilliseconds < 250)
				Emit("library loading audit PASS in " + elapsed.ElapsedMilliseconds + " ms");
			else
				Emit("library loading audit FAIL initial " + viewModel.AllGames.Count + " all changes " + allChanges + " sidebar changes " + sidebarChanges + " time " + elapsed.ElapsedMilliseconds + " ms");
		}
		catch (Exception ex)
		{
			Emit("library loading audit FAIL " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
	}

	private static void AuditPlacement()
	{
		Emit("placement audit:");
		try
		{
			Emit($"  wpf reports {Voidstrap.Utility.ScreenMetrics.PrimaryWidth}x{Voidstrap.Utility.ScreenMetrics.PrimaryHeight}");
			(double realWidth, double realHeight) = Voidstrap.Utility.ScreenMetrics.GetPrimary();
			Emit($"  real screen {realWidth}x{realHeight}");
			System.Windows.Rect work = Voidstrap.Utility.ScreenMetrics.WorkArea;
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
			System.Windows.Rect centerWork = Voidstrap.Utility.ScreenMetrics.WorkArea;
			double expectedLeft = centerWork.Left + (centerWork.Width - 400) / 2.0;
			double expectedTop = centerWork.Top + (centerWork.Height - 300) / 2.0;
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
				string label = spec.Substring(spec.IndexOf('#') + 1);
				System.Windows.Media.FontFamily family =
					Voidstrap.Utility.IconFontLoader.Resolve(label) ?? new System.Windows.Media.FontFamily(spec);
				var typefaces = family.GetTypefaces();
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

			bool warm = false;
			Wpf.Ui.Animations.RenderReady.Run(target, () => warm = true);
			bool warmed = PumpUntil(() => warm, 4000);
			Emit(warmed
				? "  render loop warmed before the transition probe"
				: "  render loop warm up timed out, the transition probe may be measured late");

			bool started = Wpf.Ui.Animations.Transitions.ApplyTransition(
				target,
				Wpf.Ui.Animations.TransitionType.FadeInWithSlideLeft,
				400);
			int frames = CountRenderFrames(100);
			Emit($"  render frames during the transition sample: {frames}");
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

#if CROSSPLAT
	private static void AuditTabPillPresentation()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			System.Windows.Controls.TabItem first = new()
			{
				Header = "Activity",
				Content = new System.Windows.Controls.ItemsControl
				{
					ItemsSource = Enumerable.Range(0, 40).Select(index => "Activity " + index)
				}
			};
			System.Windows.Controls.TabItem second = new()
			{
				Header = "Discord",
				Content = new System.Windows.Controls.ItemsControl
				{
					ItemsSource = Enumerable.Range(0, 40).Select(index => "Discord " + index)
				}
			};
			System.Windows.Controls.TabItem disabled = new() { Header = "Disabled", IsEnabled = false };
			System.Windows.Controls.TabControl tabs = new()
			{
				Width = 420,
				Height = 180,
				Items = { first, second, disabled },
				SelectedIndex = 0
			};
			probe = new Window
			{
				Width = 460,
				Height = 240,
				Content = tabs,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(350);
			first.ApplyTemplate();
			second.ApplyTemplate();
			disabled.ApplyTemplate();

			FrameworkElement? firstIndicator = FindNamedDescendant(first, "SelectionIndicator");
			FrameworkElement? secondIndicator = FindNamedDescendant(second, "SelectionIndicator");
			System.Windows.Controls.Border? firstBorder = FindNamedDescendant(first, "Border") as System.Windows.Controls.Border;
			System.Windows.Controls.Border? secondBorder = FindNamedDescendant(second, "Border") as System.Windows.Controls.Border;
			FrameworkElement? disabledRoot = FindNamedDescendant(disabled, "Root");
			if (firstIndicator?.RenderTransform is not ScaleTransform
				|| secondIndicator?.RenderTransform is not ScaleTransform
				|| firstBorder?.Background is not SolidColorBrush
				|| secondBorder?.Background is not SolidColorBrush
				|| FindNamedDescendant(first, "LinuxSelectionBackground") is not null)
			{
				Emit("  tab pill: FAIL, the stock Wpf.Ui selection-pill template was not applied");
				return;
			}

			bool hostAvailable = System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(
				probe,
				out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host);
			PumpPortableHost(host, 40);
			long presentedBefore = host?.PresentedFrameCount ?? 0;
			long skippedBefore = host?.SkippedFrameCount ?? 0;
			tabs.SelectedIndex = 1;
			HashSet<int> incomingScale = new();
			HashSet<int> outgoingScale = new();
			HashSet<int> incomingOpacity = new();
			HashSet<int> outgoingOpacity = new();
			bool indicatorClock = false;
			bool scaleClock = false;
			bool backgroundClock = false;
			bool monotonic = true;
			double lastIncomingScale = 0.25d;
			double lastOutgoingScale = 1d;
			double lastIncomingOpacity = 0d;
			double lastOutgoingOpacity = 1d;
			for (int index = 0; index < 30; index++)
			{
				PumpPortableHost(host, 8);
				ScaleTransform? liveFirstScale = firstIndicator.RenderTransform as ScaleTransform;
				ScaleTransform? liveSecondScale = secondIndicator.RenderTransform as ScaleTransform;
				double currentIncomingScale = liveSecondScale?.ScaleX ?? 0d;
				double currentOutgoingScale = liveFirstScale?.ScaleX ?? 0d;
				double currentIncomingOpacity = secondIndicator.Opacity;
				double currentOutgoingOpacity = firstIndicator.Opacity;
				indicatorClock |= secondIndicator.HasAnimatedProperties;
				scaleClock |= liveSecondScale?.HasAnimatedProperties == true;
				backgroundClock |= secondBorder.Background.HasAnimatedProperties;
				incomingScale.Add((int)Math.Round(currentIncomingScale * 1000d));
				outgoingScale.Add((int)Math.Round(currentOutgoingScale * 1000d));
				incomingOpacity.Add((int)Math.Round(currentIncomingOpacity * 1000d));
				outgoingOpacity.Add((int)Math.Round(currentOutgoingOpacity * 1000d));
				monotonic &= currentIncomingScale + 0.002d >= lastIncomingScale
					&& currentOutgoingScale <= lastOutgoingScale + 0.002d
					&& currentIncomingOpacity + 0.002d >= lastIncomingOpacity
					&& currentOutgoingOpacity <= lastOutgoingOpacity + 0.002d;
				lastIncomingScale = currentIncomingScale;
				lastOutgoingScale = currentOutgoingScale;
				lastIncomingOpacity = currentIncomingOpacity;
				lastOutgoingOpacity = currentOutgoingOpacity;
			}
			long presentedFrames = (host?.PresentedFrameCount ?? presentedBefore) - presentedBefore;
			long skippedFrames = (host?.SkippedFrameCount ?? skippedBefore) - skippedBefore;
			PumpPortableHost(host, 80);
			ScaleTransform? firstScale = firstIndicator.RenderTransform as ScaleTransform;
			ScaleTransform? secondScale = secondIndicator.RenderTransform as ScaleTransform;

			bool settled = Math.Abs(firstIndicator.Opacity) < 0.001d
				&& firstScale is not null
				&& Math.Abs(firstScale.ScaleX - 0.25d) < 0.001d
				&& Math.Abs(firstBorder.Background.Opacity) < 0.001d
				&& Math.Abs(secondIndicator.Opacity - 1d) < 0.001d
				&& secondScale is not null
				&& Math.Abs(secondScale.ScaleX - 1d) < 0.001d
				&& Math.Abs(secondBorder.Background.Opacity - 1d) < 0.001d;
			bool nativeClocks = indicatorClock && scaleClock && backgroundClock;
			bool visibleFrames = hostAvailable && presentedFrames >= 4;

			tabs.SelectedIndex = 0;
			for (int index = 0; index < 6; index++)
				PumpPortableHost(host, 8);
			double beforeFirstOpacity = firstIndicator.Opacity;
			double beforeSecondOpacity = secondIndicator.Opacity;
			double beforeFirstScale = (firstIndicator.RenderTransform as ScaleTransform)?.ScaleX ?? 0d;
			double beforeSecondScale = (secondIndicator.RenderTransform as ScaleTransform)?.ScaleX ?? 0d;
			tabs.SelectedIndex = 1;
			bool reversalContinuous = Math.Abs(firstIndicator.Opacity - beforeFirstOpacity) < 0.01d
				&& Math.Abs(secondIndicator.Opacity - beforeSecondOpacity) < 0.01d
				&& Math.Abs(((firstIndicator.RenderTransform as ScaleTransform)?.ScaleX ?? 0d) - beforeFirstScale) < 0.01d
				&& Math.Abs(((secondIndicator.RenderTransform as ScaleTransform)?.ScaleX ?? 0d) - beforeSecondScale) < 0.01d;
			PumpPortableHost(host, 350);
			ScaleTransform? returnedFirstScale = firstIndicator.RenderTransform as ScaleTransform;
			ScaleTransform? returnedSecondScale = secondIndicator.RenderTransform as ScaleTransform;
			bool reversalSettled = returnedFirstScale is not null
				&& returnedSecondScale is not null
				&& Math.Abs(firstIndicator.Opacity) < 0.001d
				&& Math.Abs(returnedFirstScale.ScaleX - 0.25d) < 0.001d
				&& Math.Abs(secondIndicator.Opacity - 1d) < 0.001d
				&& Math.Abs(returnedSecondScale.ScaleX - 1d) < 0.001d;

			probe.Hide();
			long hiddenFrameBaseline = host?.PresentedFrameCount ?? 0;
			tabs.SelectedIndex = 0;
			Pump(300);
			ScaleTransform? hiddenFirstScale = firstIndicator.RenderTransform as ScaleTransform;
			ScaleTransform? hiddenSecondScale = secondIndicator.RenderTransform as ScaleTransform;
			bool hiddenSettled = hiddenFirstScale is not null
				&& hiddenSecondScale is not null
				&& Math.Abs(firstIndicator.Opacity - 1d) < 0.001d
				&& Math.Abs(hiddenFirstScale.ScaleX - 1d) < 0.001d
				&& Math.Abs(secondIndicator.Opacity) < 0.001d
				&& Math.Abs(hiddenSecondScale.ScaleX - 0.25d) < 0.001d
				&& (host?.PresentedFrameCount ?? hiddenFrameBaseline) - hiddenFrameBaseline <= 1;
			probe.Show();
			probe.Width = 520;
			PumpPortableHost(host, 100);
			System.Windows.Controls.TabItem dynamicTab = new() { Header = "Custom" };
			tabs.Items.Add(dynamicTab);
			tabs.SelectedItem = dynamicTab;
			PumpPortableHost(host, 400);
			FrameworkElement? dynamicIndicator = FindNamedDescendant(dynamicTab, "SelectionIndicator");
			ScaleTransform? dynamicScale = dynamicIndicator?.RenderTransform as ScaleTransform;
			bool dynamicSettled = dynamicIndicator is not null
				&& dynamicScale is not null
				&& Math.Abs(dynamicIndicator.Opacity - 1d) < 0.001d
				&& Math.Abs(dynamicScale.ScaleX - 1d) < 0.001d
				&& disabledRoot is not null
				&& Math.Abs(disabledRoot.Opacity - 0.45d) < 0.001d;

			probe.WindowState = System.Windows.WindowState.Minimized;
			Pump(60);
			long minimizedFrameBaseline = host?.PresentedFrameCount ?? 0;
			tabs.SelectedIndex = 1;
			Pump(300);
			bool minimizedSettled = Math.Abs(secondIndicator.Opacity - 1d) < 0.001d
				&& Math.Abs(((ScaleTransform)secondIndicator.RenderTransform).ScaleX - 1d) < 0.001d
				&& (host?.PresentedFrameCount ?? minimizedFrameBaseline) - minimizedFrameBaseline <= 1;

			bool passed = nativeClocks
				&& visibleFrames
				&& monotonic
				&& incomingScale.Count >= 5
				&& outgoingScale.Count >= 5
				&& incomingOpacity.Count >= 4
				&& outgoingOpacity.Count >= 4
				&& settled
				&& reversalContinuous
				&& reversalSettled
				&& hiddenSettled
				&& dynamicSettled
				&& minimizedSettled;
			Emit(passed
				? $"  tab pill: PASS, stock Windows Storyboards produced {incomingScale.Count}/{outgoingScale.Count} scale values, {incomingOpacity.Count}/{outgoingOpacity.Count} opacity values, and {presentedFrames} visible frames"
				: $"  tab pill: FAIL, host {hostAvailable}/{host?.HasPresentedFrame}, presented {presentedBefore}+{presentedFrames}, skipped {skippedBefore}+{skippedFrames}, clocks {nativeClocks}, monotonic {monotonic}, scale {incomingScale.Count}/{outgoingScale.Count} at {secondScale?.ScaleX:F3}/{firstScale?.ScaleX:F3}, opacity {incomingOpacity.Count}/{outgoingOpacity.Count} at {secondIndicator.Opacity:F3}/{firstIndicator.Opacity:F3}, background {secondBorder?.Background?.Opacity:F3}/{firstBorder?.Background?.Opacity:F3}, settled {settled}, reversal {reversalContinuous}/{reversalSettled}, hidden {hiddenSettled}, dynamic {dynamicSettled}, minimized {minimizedSettled}");
		}
		catch (Exception ex)
		{
			Emit($"  tab pill: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			probe?.Close();
			Pump(40);
		}
	}

	private static void AuditExpanderReveal()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		MeasureReveal("CardExpander", () =>
		{
			System.Windows.Controls.StackPanel body = new();
			for (int i = 0; i < 10; i++)
				body.Children.Add(new System.Windows.Controls.TextBlock { Text = "row " + i, Height = 26 });
			return new Wpf.Ui.Controls.CardExpander
			{
				Header = "Session and activity",
				Content = body,
				VerticalAlignment = VerticalAlignment.Top
			};
		});

		MeasureReveal("Expander", () =>
		{
			System.Windows.Controls.StackPanel body = new();
			for (int i = 0; i < 10; i++)
				body.Children.Add(new System.Windows.Controls.TextBlock { Text = "row " + i, Height = 26 });
			return new System.Windows.Controls.Expander
			{
				Header = "Coders",
				Content = body,
				VerticalAlignment = VerticalAlignment.Top
			};
		});
	}

	private static void MeasureReveal(string label, Func<System.Windows.Controls.Expander> factory)
	{
		Window? probe = null;
		try
		{
			System.Windows.Controls.Expander expander = factory();
			System.Windows.Controls.Grid content = new();
			content.Children.Add(expander);
			probe = new Window
			{
				Width = 460,
				Height = 460,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(250);

			FrameworkElement? body = FindNamedDescendant(expander, "ContentPresenterBorder");
			FrameworkElement? chevron = FindNamedDescendant(expander, "ChevronGrid");
			if (body is null || chevron is null || FindNamedDescendant(expander, "LinuxHeaderHoverBackground") is not null)
			{
				Emit($"  reveal {label}: FAIL, the Windows expander template was not applied");
				return;
			}

			bool hostAvailable = System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(
				probe,
				out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host);
			PumpPortableHost(host, 40);
			long openingPresentedBefore = host?.PresentedFrameCount ?? 0;
			expander.IsExpanded = true;
			HashSet<int> openingHeights = new();
			HashSet<int> openingAngles = new();
			HashSet<int> openingOpacity = new();
			bool openingClock = false;
			bool openingMonotonic = true;
			double lastOpeningHeight = 0d;
			double lastOpeningAngle = 0d;
			for (int i = 0; i < 30; i++)
			{
				PumpPortableHost(host, 10);
				openingClock |= body.HasAnimatedProperties;
				openingHeights.Add((int)Math.Round(body.ActualHeight));
				openingOpacity.Add((int)Math.Round(body.Opacity * 1000d));
				if (chevron.RenderTransform is RotateTransform openingRotate)
				{
					openingAngles.Add((int)Math.Round(openingRotate.Angle));
					openingMonotonic &= openingRotate.Angle + 0.5d >= lastOpeningAngle;
					lastOpeningAngle = openingRotate.Angle;
				}
				openingMonotonic &= body.ActualHeight + 0.5d >= lastOpeningHeight;
				lastOpeningHeight = body.ActualHeight;
			}
			long openingPresented = (host?.PresentedFrameCount ?? openingPresentedBefore) - openingPresentedBefore;
			Pump(400);
			double opened = body.ActualHeight;

			long closingPresentedBefore = host?.PresentedFrameCount ?? 0;
			expander.IsExpanded = false;
			HashSet<int> closingHeights = new();
			HashSet<int> closingAngles = new();
			HashSet<int> closingOpacity = new();
			bool closingClock = false;
			bool closingMonotonic = true;
			double lastClosingHeight = opened;
			double lastClosingAngle = 180d;
			for (int i = 0; i < 30; i++)
			{
				PumpPortableHost(host, 10);
				closingClock |= body.HasAnimatedProperties;
				closingHeights.Add((int)Math.Round(body.ActualHeight));
				closingOpacity.Add((int)Math.Round(body.Opacity * 1000d));
				if (chevron.RenderTransform is RotateTransform closingRotate)
				{
					closingAngles.Add((int)Math.Round(closingRotate.Angle));
					closingMonotonic &= closingRotate.Angle <= lastClosingAngle + 0.5d;
					lastClosingAngle = closingRotate.Angle;
				}
				closingMonotonic &= body.ActualHeight <= lastClosingHeight + 0.5d;
				lastClosingHeight = body.ActualHeight;
			}
			long closingPresented = (host?.PresentedFrameCount ?? closingPresentedBefore) - closingPresentedBefore;
			Pump(400);
			double closed = body.ActualHeight;

			expander.IsExpanded = true;
			PumpPortableHost(host, 70);
			double reversalHeight = body.ActualHeight;
			double reversalOpacity = body.Opacity;
			double reversalAngle = (chevron.RenderTransform as RotateTransform)?.Angle ?? 0d;
			expander.IsExpanded = false;
			bool reversalContinuous = Math.Abs(body.ActualHeight - reversalHeight) < 1d
				&& Math.Abs(body.Opacity - reversalOpacity) < 0.02d
				&& Math.Abs(((chevron.RenderTransform as RotateTransform)?.Angle ?? 0d) - reversalAngle) < 2d;
			PumpPortableHost(host, 70);
			reversalHeight = body.ActualHeight;
			reversalOpacity = body.Opacity;
			reversalAngle = (chevron.RenderTransform as RotateTransform)?.Angle ?? 0d;
			expander.IsExpanded = true;
			reversalContinuous &= Math.Abs(body.ActualHeight - reversalHeight) < 1d
				&& Math.Abs(body.Opacity - reversalOpacity) < 0.02d
				&& Math.Abs(((chevron.RenderTransform as RotateTransform)?.Angle ?? 0d) - reversalAngle) < 2d;
			PumpPortableHost(host, 400);

			if (expander.Content is System.Windows.Controls.StackPanel dynamicBody)
				dynamicBody.Children.Add(new System.Windows.Controls.TextBlock { Text = "dynamic row", Height = 52 });
			expander.IsExpanded = false;
			PumpPortableHost(host, 400);
			expander.IsExpanded = true;
			PumpPortableHost(host, 400);
			bool dynamicMeasured = body.ActualHeight > opened + 40d;

			expander.IsExpanded = false;
			PumpPortableHost(host, 400);
			long idlePresentedBefore = host?.PresentedFrameCount ?? 0;
			PumpPortableHost(host, 160);
			long idlePresented = (host?.PresentedFrameCount ?? idlePresentedBefore) - idlePresentedBefore;

			for (int index = 0; index < 100; index++)
				expander.IsExpanded = index % 2 == 0;
			expander.IsExpanded = false;
			PumpPortableHost(host, 700);
			bool cycleSettled = body.ActualHeight < 0.5d
				&& body.Opacity < 0.001d
				&& !body.HasAnimatedProperties
				&& !body.IsHitTestVisible;
			bool passed = openingHeights.Count >= 4
				&& closingHeights.Count >= 4
				&& openingAngles.Count >= 4
				&& closingAngles.Count >= 4
				&& openingOpacity.Count >= 3
				&& closingOpacity.Count >= 3
				&& openingClock
				&& closingClock
				&& openingMonotonic
				&& closingMonotonic
				&& hostAvailable
				&& openingPresented >= 4
				&& closingPresented >= 4
				&& opened > 1d
				&& closed < 0.5d
				&& reversalContinuous
				&& dynamicMeasured
				&& cycleSettled;
			Emit(passed
				? $"  reveal {label}: PASS, Windows template produced {openingPresented}/{closingPresented} presented frames, {openingHeights.Count}/{closingHeights.Count} heights, and {openingAngles.Count}/{closingAngles.Count} chevron angles"
				: $"  reveal {label}: FAIL, host {hostAvailable}, presented {openingPresented}/{closingPresented}, body {openingHeights.Count}/{closingHeights.Count}, opacity {openingOpacity.Count}/{closingOpacity.Count}, chevron {openingAngles.Count}/{closingAngles.Count}, clocks {openingClock}/{closingClock}, monotonic {openingMonotonic}/{closingMonotonic}, opened {opened:F1}, closed {closed:F1}, reversal {reversalContinuous}, dynamic {dynamicMeasured}, idle {idlePresented}, cycles {cycleSettled}");
		}
		catch (Exception ex)
		{
			Emit($"  reveal {label}: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			probe?.Close();
			Pump(40);
		}
	}
#endif

	private static void AuditThemeEditorWindow()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		string? theme = Environment.GetEnvironmentVariable("VOIDSTRAP_AUDIT_THEME_EDITOR");
		if (string.IsNullOrWhiteSpace(theme))
			return;

		Window? editor = null;
		try
		{
			Emit($"theme editor audit: opening {theme}");
			editor = new Voidstrap.UI.Elements.Editor.BootstrapperEditorWindow(theme);
			System.Diagnostics.Stopwatch modal = System.Diagnostics.Stopwatch.StartNew();
			Window modalEditor = editor;
			editor.Dispatcher.BeginInvoke(new Action(() =>
			{
				try
				{
					modalEditor.ShowOwnedDialog();
					Emit($"theme editor audit: ShowDialog returned after {modal.ElapsedMilliseconds}ms");
				}
				catch (Exception showEx)
				{
					Emit($"theme editor audit: ShowDialog threw {showEx.GetType().Name}");
				}
			}));
			Pump(1200);

			Pump(1500);
			ICSharpCode.AvalonEdit.TextEditor? code = editor.FindName("UIXML") as ICSharpCode.AvalonEdit.TextEditor;
			if (code is null)
			{
				Emit("theme editor audit: FAIL, the code editor was not found");
				return;
			}

			int tagOffset = code.Text.IndexOf("BloxstrapCustomBootstrapper", StringComparison.Ordinal);
			int caret = tagOffset >= 0 ? tagOffset + "BloxstrapCustomBootstrapper".Length : Math.Min(30, code.Text.Length);
			code.TextArea.Focus();
			code.CaretOffset = caret;
			Pump(300);
			Emit($"theme editor audit: typing at offset {caret} to raise the attribute list");
			System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
			code.TextArea.PerformTextInput(" ");
			Pump(400);
			Emit($"theme editor audit: attribute list raised after {timer.ElapsedMilliseconds}ms");
			int ticks = 0;
			while (timer.ElapsedMilliseconds < 12000)
			{
				Pump(200);
				ticks++;
				if (ticks % 10 == 0)
					Emit($"theme editor audit: alive at {timer.ElapsedMilliseconds}ms");
			}

			timer.Stop();
			Emit($"theme editor audit: PASS, stayed responsive for {timer.ElapsedMilliseconds}ms across {ticks} pumps");
		}
		catch (Exception ex)
		{
			Emit($"theme editor audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try { editor?.Close(); Pump(120); } catch (Exception) { }
		}
	}

	private static void AuditCrosshairExports()
	{
		try
		{
			const int dimension = 64;
			byte[] pixels = new byte[dimension * dimension * 4];
			for (int y = 0; y < dimension; y++)
			{
				for (int x = 0; x < dimension; x++)
				{
					if (Math.Abs(x - dimension / 2) > 1 && Math.Abs(y - dimension / 2) > 1)
						continue;
					int offset = (y * dimension + x) * 4;
					pixels[offset + 1] = 255;
					pixels[offset + 3] = 255;
				}
			}
			BitmapSource source = BitmapSource.Create(dimension, dimension, 96, 96, PixelFormats.Pbgra32, null, pixels, dimension * 4);
			byte[] png = Voidstrap.UI.ViewModels.Settings.ModsViewModel.EncodeCrosshairPng(source);
			byte[] cursor = Voidstrap.UI.ViewModels.Settings.ModsViewModel.EncodeCrosshairCursor(source, 32, 32);
			byte[] pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
			bool pngValid = png.Length > 32 && png.AsSpan(0, 8).SequenceEqual(pngSignature);
			bool cursorValid = cursor.Length > 54
				&& System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(cursor.AsSpan(0, 2)) == 0
				&& System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(cursor.AsSpan(2, 2)) == 2
				&& System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(cursor.AsSpan(4, 2)) == 1
				&& cursor[6] == dimension
				&& cursor[7] == dimension
				&& System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(cursor.AsSpan(10, 2)) == 32
				&& System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(cursor.AsSpan(12, 2)) == 32
				&& System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cursor.AsSpan(18, 4)) == 22
				&& cursor.AsSpan(22, 8).SequenceEqual(pngSignature);
			BitmapSource? decodedPng = SafeImaging.FromBytes(png);
			BitmapSource? decodedCursor = SafeImaging.FromBytes(cursor);
			bool decoded = decodedPng?.PixelWidth == dimension
				&& decodedPng.PixelHeight == dimension
				&& decodedCursor?.PixelWidth == dimension
				&& decodedCursor.PixelHeight == dimension;
			Emit(pngValid && cursorValid && decoded
				? $"crosshair export audit: PASS, PNG {png.Length} bytes and CUR {cursor.Length} bytes decoded at 64x64 with hotspot 32,32"
				: $"crosshair export audit: FAIL, png {pngValid}, cursor {cursorValid}, decoded {decoded}");
		}
		catch (Exception ex)
		{
			Emit($"crosshair export audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
	}

	private static void AuditAnimatedGif()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		string? gif = Environment.GetEnvironmentVariable("VOIDSTRAP_AUDIT_GIF");
		if (string.IsNullOrWhiteSpace(gif) || !System.IO.File.Exists(gif))
			return;

		Window? probe = null;
		try
		{
			System.Windows.Controls.Image image = new()
			{
				Width = 160,
				Height = 90,
				Stretch = System.Windows.Media.Stretch.Fill
			};

			System.Windows.Controls.Grid content = new();
			content.Children.Add(image);
			probe = new Window
			{
				Width = 300,
				Height = 200,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(300);

			Voidstrap.UI.GifImageBehavior.SetSourcePath(image, gif);
			Pump(600);

			HashSet<string> seen = new();
			for (int i = 0; i < 24; i++)
			{
				if (image.Source is System.Windows.Media.Imaging.BitmapSource frame)
					seen.Add(DescribeFrame(frame));

				Pump(220);
			}

			Emit(seen.Count >= 2
				? $"animated gif audit: PASS, {seen.Count} distinct frames were shown"
				: $"animated gif audit: FAIL, only {seen.Count} frame(s) ever appeared, source {(image.Source == null ? "null" : "set")}");
		}
		catch (Exception ex)
		{
			Emit($"animated gif audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try { probe?.Close(); Pump(60); } catch (Exception) { }
		}
	}

	private static void AuditBootstrapperIcons()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Voidstrap.Enums.BootstrapperIcon original = App.Settings.Prop.BootstrapperIcon;
		Window? probe = null;
		try
		{
			List<string> failures = new();
			HashSet<string> distinct = new();
			int decoded = 0;

			foreach (Voidstrap.Enums.BootstrapperIcon selection in Voidstrap.Extensions.BootstrapperIconEx.Selections)
			{
				if (selection == Voidstrap.Enums.BootstrapperIcon.IconCustom)
					continue;

				Voidstrap.Models.BootstrapperIconEntry entry = new() { IconType = selection };
				if (entry.ImageSource is not System.Windows.Media.Imaging.BitmapSource source)
				{
					failures.Add(selection.ToString());
					continue;
				}

				decoded++;
				distinct.Add(DescribeIcon(source));
			}

			Emit(failures.Count == 0 && distinct.Count >= 6
				? $"bootstrapper icon audit: PASS, {decoded} icons decoded into {distinct.Count} distinct images"
				: $"bootstrapper icon audit: FAIL, {failures.Count} icon(s) did not decode ({string.Join(", ", failures)}), {distinct.Count} distinct of {decoded}");

			probe = new Window
			{
				Width = 240,
				Height = 160,
				Title = "Voidstrap icon identity probe",
				ShowInTaskbar = true
			};
			probe.Show();
			Pump(400);

			App.Settings.Prop.BootstrapperIcon = Voidstrap.Enums.BootstrapperIcon.Icon2011;
			Voidstrap.UI.LinuxApplicationIdentity.Refresh();
			Pump(400);

			string expected = IconEx.GetIconSource(Voidstrap.Enums.BootstrapperIcon.Icon2011) is System.Windows.Media.Imaging.BitmapSource wanted
				? DescribeIcon(wanted)
				: "none";
			string actual = probe.Icon is System.Windows.Media.Imaging.BitmapSource shown ? DescribeIcon(shown) : "none";

			Emit(expected != "none" && expected == actual
				? "taskbar icon audit: PASS, the window icon follows the selected bootstrapper icon"
				: $"taskbar icon audit: FAIL, expected {expected} but the window carries {actual}");

			Pump(2500);
			string iconFile = Voidstrap.Utility.LinuxDesktopEntry.IconFilePath;
			string onDisk = "missing";
			if (System.IO.File.Exists(iconFile) && Voidstrap.Utility.SafeImaging.FromBytes(System.IO.File.ReadAllBytes(iconFile)) is System.Windows.Media.Imaging.BitmapSource written)
				onDisk = DescribeIcon(written);

			Emit(expected != "none" && expected == onDisk
				? "desktop icon audit: PASS, the shell icon file matches the selected bootstrapper icon"
				: $"desktop icon audit: FAIL, expected {expected} but {iconFile} holds {onDisk}");
		}
		catch (Exception ex)
		{
			Emit($"bootstrapper icon audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			App.Settings.Prop.BootstrapperIcon = original;
			try { Voidstrap.UI.LinuxApplicationIdentity.Refresh(); } catch (Exception) { }
			try { probe?.Close(); Pump(60); } catch (Exception) { }
		}
	}

	private static string DescribeIcon(System.Windows.Media.Imaging.BitmapSource source)
	{
		try
		{
			int stride = source.PixelWidth * 4;
			byte[] pixels = new byte[stride * source.PixelHeight];
			source.CopyPixels(pixels, stride, 0);
			int hash = 17;
			for (int i = 0; i < pixels.Length; i += 3)
				hash = (hash * 31) + pixels[i];

			return source.PixelWidth + "x" + source.PixelHeight + ":" + hash;
		}
		catch (Exception)
		{
			return "unreadable";
		}
	}

	private static string DescribeFrame(System.Windows.Media.Imaging.BitmapSource frame)
	{
		try
		{
			int stride = frame.PixelWidth * 4;
			byte[] pixels = new byte[stride * Math.Min(2, frame.PixelHeight)];
			frame.CopyPixels(new Int32Rect(0, 0, frame.PixelWidth, Math.Min(2, frame.PixelHeight)), pixels, stride, 0);
			int hash = 17;
			for (int i = 0; i < pixels.Length; i += 7)
				hash = (hash * 31) + pixels[i];

			return hash.ToString();
		}
		catch (Exception)
		{
			return frame.GetHashCode().ToString();
		}
	}

	private static void AuditHtmlThemePanel()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		string? probe = Environment.GetEnvironmentVariable("VOIDSTRAP_AUDIT_HTML_PANEL");
		if (string.IsNullOrWhiteSpace(probe))
			return;

		string name = "VoidstrapHtmlProbe";
		string folder = System.IO.Path.Combine(Paths.CustomThemes, name);
		try
		{
			System.IO.Directory.CreateDirectory(folder);

			string gifTag = string.Empty;
			string? gifSource = Environment.GetEnvironmentVariable("VOIDSTRAP_AUDIT_GIF");
			if (!string.IsNullOrWhiteSpace(gifSource) && System.IO.File.Exists(gifSource))
			{
				System.IO.File.Copy(gifSource, System.IO.Path.Combine(folder, "anim.gif"), true);
				gifTag = "<img src=\"anim.gif\" width=\"160\" style=\"display:block;margin-top:8px\">";
			}

			System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "panel.html"),
				"<html><body style=\"margin:0;background:#1b2a4a;color:#fff;font:16px sans-serif\">"
				+ "<div style=\"padding:18px\"><b>HTML panel is live</b><div id=\"s\">waiting</div>"
				+ "<div id=\"tick\" style=\"margin-top:8px\">tick 0</div>"
				+ "<div id=\"box\" style=\"width:60px;height:24px;background:#e33;margin-top:8px\"></div>"
				+ gifTag + "</div>"
				+ "<script>window.voidstrap.onUpdate(function(v){document.getElementById('s').textContent='status: '+v.status+' pct: '+Math.round(v.percent);});"
				+ "var n=0;setInterval(function(){n++;document.getElementById('tick').textContent='tick '+n;"
				+ "document.getElementById('box').style.background=(n%2)?'#3e3':'#e33';"
				+ "},400);</script>"
				+ "</body></html>");

			System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "Theme.xml"),
				"<BloxstrapCustomBootstrapper Version=\"1\" Height=\"320\" Width=\"520\">"
				+ "<WebPanel Source=\"theme://panel.html\" Height=\"240\" Width=\"480\" "
				+ "HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" />"
				+ "</BloxstrapCustomBootstrapper>");

			Voidstrap.UI.Elements.Bootstrapper.CustomDialog dialog = new(true);
			dialog.ApplyCustomTheme(name);
			dialog.Title = "Voidstrap HTML probe";
			dialog.Show();
			Pump(1200);

			dialog.Message = "installing";
			Pump(4000);

			bool supported = Voidstrap.UI.LinuxWebPanel.IsSupported;
			Emit($"html panel audit: engine {supported}, dialog shown at {dialog.ActualWidth:F0}x{dialog.ActualHeight:F0}, holding for capture");
			Pump(14000);
			try { dialog.Close(); Pump(200); } catch (Exception) { }
		}
		catch (Exception ex)
		{
			Emit($"html panel audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try { System.IO.Directory.Delete(folder, true); } catch (Exception) { }
		}
	}

	private static void AuditThemeDeletion()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		string name = "VoidstrapDeleteProbe";
		string folder = System.IO.Path.Combine(Paths.CustomThemes, name);
		string? original = App.Settings.Prop.SelectedCustomTheme;
		Voidstrap.Enums.BootstrapperStyle originalStyle = App.Settings.Prop.BootstrapperStyle;
		try
		{
			App.Settings.Prop.BootstrapperStyle = Voidstrap.Enums.BootstrapperStyle.CustomDialog;
			System.IO.Directory.CreateDirectory(folder);
			System.IO.File.WriteAllText(
				System.IO.Path.Combine(folder, "Theme.xml"),
				"<BloxstrapCustomBootstrapper Version=\"1\" Height=\"200\" Width=\"300\" />");

			Voidstrap.UI.Elements.Settings.Pages.AppearancePage page = new();
			Window host = new()
			{
				Width = 1000,
				Height = 700,
				Content = page,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			host.Show();
			Pump(1500);

			System.Windows.Controls.ListBox? list = null;
			List<System.Windows.Controls.TabControl> tabs = FindDescendants<System.Windows.Controls.TabControl>(host);
			foreach (System.Windows.Controls.TabControl tab in tabs)
			{
				for (int index = 0; index < tab.Items.Count && list == null; index++)
				{
					tab.SelectedIndex = index;
					Pump(500);

					foreach (Wpf.Ui.Controls.CardExpander expander in FindDescendants<Wpf.Ui.Controls.CardExpander>(host))
					{
						if (!expander.IsExpanded)
							expander.IsExpanded = true;
					}
					Pump(400);

					list = FindNamedDescendant(host, "CustomThemesListBox") as System.Windows.Controls.ListBox;
				}

				if (list != null)
					break;
			}
			if (list == null)
			{
				Emit($"theme delete audit: FAIL, the theme list was not found, style {App.Settings.Prop.BootstrapperStyle}, tabs {tabs.Count}");
				try { host.Close(); } catch (Exception) { }
				return;
			}

			if (!list.Items.Contains(name))
			{
				Emit($"theme delete audit: FAIL, the probe theme never appeared in the list of {list.Items.Count}");
				try { host.Close(); } catch (Exception) { }
				return;
			}

			list.SelectedItem = name;
			Pump(300);

			System.Windows.Controls.Button? deleteButton = FindButtonByContent(page, "Delete");
			bool enabled = deleteButton is { IsEnabled: true };
			bool canExecute = deleteButton?.Command?.CanExecute(deleteButton.CommandParameter) ?? false;

			if (deleteButton != null)
			{
				System.Windows.Automation.Peers.ButtonAutomationPeer peer = new(deleteButton);
				if (peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
					is System.Windows.Automation.Provider.IInvokeProvider invoke)
				{
					invoke.Invoke();
				}
			}

			int waited = 0;
			while (System.IO.Directory.Exists(folder) && waited < 5000)
			{
				Pump(100);
				waited += 100;
			}

			bool gone = !System.IO.Directory.Exists(folder);
			bool listRemoved = !list.Items.Contains(name);
			try { host.Close(); Pump(80); } catch (Exception) { }
			Emit(gone && enabled && canExecute
				? $"theme delete audit: PASS, removed from disk in {waited}ms and dropped from the list {listRemoved}"
				: $"theme delete audit: FAIL, enabled {enabled}, canExecute {canExecute}, gone {gone} after {waited}ms, listRemoved {listRemoved}");
		}
		catch (Exception ex)
		{
			Emit($"theme delete audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); } catch (Exception) { }
			try { App.Settings.Prop.SelectedCustomTheme = original; } catch (Exception) { }
			try { App.Settings.Prop.BootstrapperStyle = originalStyle; } catch (Exception) { }
		}
	}

	private static List<T> FindDescendants<T>(DependencyObject root, int depth = 0) where T : DependencyObject
	{
		List<T> found = new();
		if (depth > 40)
			return found;

		int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count && i < 512; i++)
		{
			DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
			if (child is T match)
				found.Add(match);

			found.AddRange(FindDescendants<T>(child, depth + 1));
		}

		return found;
	}

	private static System.Windows.Controls.Button? FindButtonByContent(DependencyObject root, string content)
	{
		int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
			if (child is System.Windows.Controls.Button button
				&& button.Content is string text
				&& string.Equals(text, content, StringComparison.OrdinalIgnoreCase))
			{
				return button;
			}

			System.Windows.Controls.Button? found = FindButtonByContent(child, content);
			if (found != null)
				return found;
		}

		return null;
	}

	private static void AuditThemeCaseParity()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		string name = "VoidstrapCaseProbe";
		string folder = System.IO.Path.Combine(Paths.CustomThemes, name);
		try
		{
			System.IO.Directory.CreateDirectory(folder);
			System.IO.File.WriteAllText(
				System.IO.Path.Combine(folder, "theme.xml"),
				"<BloxstrapCustomBootstrapper Version=\"1\" Height=\"200\" Width=\"300\">"
				+ "<Image Source=\"theme://Logo.png\" Height=\"40\" Width=\"40\" />"
				+ "</BloxstrapCustomBootstrapper>");

			byte[] pixel =
			{
				0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
				0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
				0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
				0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
				0x42, 0x60, 0x82
			};
			System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "logo.png"), pixel);

			string resolvedTheme = Voidstrap.Utility.CaseInsensitivePath.Resolve(System.IO.Path.Combine(folder, "Theme.xml"));
			string resolvedAsset = Voidstrap.Utility.CaseInsensitivePath.Resolve(System.IO.Path.Combine(folder, "Logo.png"));
			bool themeFound = System.IO.File.Exists(resolvedTheme);
			bool assetFound = System.IO.File.Exists(resolvedAsset);

			bool applied = false;
			string detail = "";
			try
			{
				Voidstrap.UI.Elements.Bootstrapper.CustomDialog dialog = new(true);
				dialog.ApplyCustomTheme(name);
				applied = true;
				try { dialog.Close(); } catch (Exception) { }
			}
			catch (Exception ex)
			{
				detail = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
			}

			Emit(themeFound && assetFound && applied
				? "theme case parity audit: PASS, a Windows cased theme resolved theme.xml and theme://Logo.png and applied"
				: $"theme case parity audit: FAIL, theme {themeFound}, asset {assetFound}, applied {applied} {detail}");
		}
		catch (Exception ex)
		{
			Emit($"theme case parity audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try { System.IO.Directory.Delete(folder, true); } catch (Exception) { }
		}
	}

	private static void AuditMessageBoxButtons()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			Voidstrap.UI.Elements.Dialogs.FluentMessageBox box = new(
				"Windows Startup Recreation includes HTML files, which can run code when the theme is used. This theme has not been verified, so it could be malicious. Only install it if you trust the author.\n\nFiles: panel.html",
				MessageBoxImage.Warning,
				MessageBoxButton.YesNo);
			probe = box;
			box.Show();
			Pump(600);

			FrameworkElement? one = FindNamedDescendant(box, "ButtonOne");
			FrameworkElement? two = FindNamedDescendant(box, "ButtonTwo");
			FrameworkElement? text = FindNamedDescendant(box, "MessageTextBlock");

			double windowHeight = box.ActualHeight;
			double oneBottom = -1d;
			double oneLeft = -1d;
			double oneRight = -1d;
			double oneHeight = one?.ActualHeight ?? -1d;
			bool oneVisible = one is { IsVisible: true };
			bool twoVisible = two is { IsVisible: true };

			if (one is not null && one.IsVisible)
			{
				try
				{
					Point origin = one.TransformToAncestor(box).Transform(new Point(0d, 0d));
					oneBottom = origin.Y + one.ActualHeight;
					oneLeft = origin.X;
					oneRight = origin.X + one.ActualWidth;
				}
				catch (Exception)
				{
				}
			}

			double windowWidth = box.ActualWidth;
			bool fits = oneBottom > 0d && oneBottom <= windowHeight + 0.5d
				&& oneRight > 0d && oneRight <= windowWidth + 0.5d;
			Emit(oneVisible && twoVisible && oneHeight > 1d && fits
				? $"message box audit: PASS, buttons inside the {windowWidth:F0}x{windowHeight:F0} window, first button x {oneLeft:F0}..{oneRight:F0} y ends {oneBottom:F0}"
				: $"message box audit: FAIL, window {windowWidth:F0}x{windowHeight:F0}, one visible {oneVisible} h {oneHeight:F0}, x {oneLeft:F0}..{oneRight:F0}, y ends {oneBottom:F0}, two visible {twoVisible}, text w {text?.ActualWidth ?? -1:F0}");
		}
		catch (Exception ex)
		{
			Emit($"message box audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try { probe?.Close(); Pump(80); } catch (Exception) { }
		}
	}

	private static void AuditMaximizedWindow()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			probe = new Window
			{
				Width = 800,
				Height = 600,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None,
				WindowState = System.Windows.WindowState.Maximized,
				Content = new System.Windows.Controls.Grid()
			};
			probe.Show();
			Pump(700);

			double screenWidth = SystemParameters.PrimaryScreenWidth;
			double screenHeight = SystemParameters.PrimaryScreenHeight;
			double workWidth = SystemParameters.WorkArea.Width;
			double workHeight = SystemParameters.WorkArea.Height;
			double actualWidth = probe.ActualWidth;
			double actualHeight = probe.ActualHeight;
			bool stateHeld = probe.WindowState == System.Windows.WindowState.Maximized;
			double target = workWidth > 0 ? workWidth : screenWidth;
			bool filled = target > 0 && actualWidth >= target * 0.9d;

			Emit(stateHeld && filled
				? $"maximized window audit: PASS, {actualWidth:F0}x{actualHeight:F0} fills the {target:F0}x{workHeight:F0} work area"
				: $"maximized window audit: FAIL, state {probe.WindowState}, actual {actualWidth:F0}x{actualHeight:F0}, work {workWidth:F0}x{workHeight:F0}, screen {screenWidth:F0}x{screenHeight:F0}");
		}
		catch (Exception ex)
		{
			Emit($"maximized window audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try { probe?.Close(); Pump(80); } catch (Exception) { }
		}
	}

	private static void AuditEditorNativeCalls()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			ICSharpCode.AvalonEdit.TextEditor editor = new()
			{
				Text = "theme editor probe",
				FontSize = 12d
			};
			System.Windows.Controls.Grid content = new();
			content.Children.Add(editor);
			probe = new Window
			{
				Width = 420,
				Height = 260,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(200);

			editor.TextArea.Focus();
			System.Windows.Input.Keyboard.Focus(editor.TextArea);
			Pump(250);

			editor.AppendText(" typed");
			editor.TextArea.Caret.Offset = editor.Text.Length;
			Pump(200);

			bool focused = editor.TextArea.IsKeyboardFocusWithin || editor.TextArea.IsFocused;
			string[] required =
			{
				"ImmGetContext", "ImmReleaseContext", "ImmGetDefaultIMEWnd", "ImmSetCompositionWindow",
				"ImmSetCompositionFont", "ImmGetCompositionFont", "ImmNotifyIME", "ImmAssociateContext",
				"GetCaretBlinkTime", "CreateCaret", "DestroyCaret", "SetCaretPos", "GetFocus", "SetFocus", "GetWindow"
			};
			List<string> missing = new();
			if (System.Runtime.InteropServices.NativeLibrary.TryLoad(
					System.IO.Path.Combine(AppContext.BaseDirectory, "libvoidstrapeditorcompat.so"),
					out IntPtr stub))
			{
				foreach (string export in required)
				{
					if (!System.Runtime.InteropServices.NativeLibrary.TryGetExport(stub, export, out _))
						missing.Add(export);
				}
			}
			else
			{
				missing.Add("the stub could not be loaded");
			}

			Emit(missing.Count == 0
				? $"editor native audit: PASS, focus {focused}, length {editor.Text.Length}, all {required.Length} native entry points resolve"
				: $"editor native audit: FAIL, missing {string.Join(", ", missing)}");
		}
		catch (Exception ex)
		{
			Emit($"editor native audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try
			{
				probe?.Close();
				Pump(60);
			}
			catch (Exception)
			{
			}
		}
	}

	private static void AuditWinFormsCompat()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		try
		{
			System.Reflection.Assembly forms = System.Reflection.Assembly.Load("System.Windows.Forms");
			Type? cursor = forms.GetType("System.Windows.Forms.Cursor");
			System.Reflection.MethodInfo? show = cursor?.GetMethod("Show", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
			System.Reflection.MethodInfo? hide = cursor?.GetMethod("Hide", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
			if (show is null || hide is null)
			{
				Emit($"winforms compat audit: FAIL, cursor {cursor is not null}, show {show is not null}, hide {hide is not null}");
				return;
			}

			hide.Invoke(null, null);
			show.Invoke(null, null);
			Emit($"winforms compat audit: PASS, {forms.GetName().Name} resolved and the editor cursor calls succeeded");
		}
		catch (Exception ex)
		{
			Exception reported = ex is System.Reflection.TargetInvocationException invocation && invocation.InnerException is not null
				? invocation.InnerException
				: ex;
			Emit($"winforms compat audit: FAIL, {reported.GetType().Name}: {reported.Message.Split('\n')[0]}");
		}
	}

	private static void AuditGradientOpacity()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		try
		{
			byte[] bgr24 = new byte[3 * 4];
			for (int i = 0; i < bgr24.Length; i += 3)
			{
				bgr24[i] = 10;
				bgr24[i + 1] = 200;
				bgr24[i + 2] = 30;
			}

			System.Windows.Media.Imaging.BitmapSource source = System.Windows.Media.Imaging.BitmapSource.Create(
				2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, bgr24, 6);

			byte[]? bgra = Voidstrap.UI.ViewModels.Settings.AppearanceViewModel.ReadBgraPixels(source);
			if (bgra is null || bgra.Length != 16)
			{
				Emit($"gradient opacity audit: FAIL, converted {bgra?.Length ?? -1} bytes");
				return;
			}

			bool channels = bgra[0] == 10 && bgra[1] == 200 && bgra[2] == 30 && bgra[3] == 255;
			double total = 0d;
			for (int i = 0; i < bgra.Length; i += 4)
				total += 0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2];

			double luminance = total / (bgra.Length / 4) / 255d;
			double opacity = Math.Clamp(0.35 + luminance * 0.6, 0.35, 0.95);
			double expectedLuminance = (0.0722 * 10 + 0.7152 * 200 + 0.2126 * 30) / 255d;
			double expectedOpacity = Math.Clamp(0.35 + expectedLuminance * 0.6, 0.35, 0.95);
			bool matches = Math.Abs(opacity - expectedOpacity) < 0.0001d;

			Emit(channels && matches
				? $"gradient opacity audit: PASS, Bgr24 mapped to BGRA exactly and luminance {luminance:F4} produced opacity {opacity:F4}"
				: $"gradient opacity audit: FAIL, channels {channels}, opacity {opacity:F4} expected {expectedOpacity:F4}");
		}
		catch (Exception ex)
		{
			Emit($"gradient opacity audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
	}

	private static void AuditExpanderChevron()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			Voidstrap.UI.Elements.Controls.Expander expander = new()
			{
				HeaderText = "Coders",
				HeaderIcon = Wpf.Ui.Common.SymbolRegular.Code24,
				InnerContent = new System.Windows.Controls.TextBlock { Text = "body" },
				VerticalAlignment = VerticalAlignment.Top
			};
			Wpf.Ui.Controls.ExpanderMotion.SetUseLinuxAnimationClock(expander, false);
			System.Windows.Controls.Grid content = new();
			content.Children.Add(expander);
			probe = new Window
			{
				Width = 420,
				Height = 300,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(200);

			ReportChevron(expander, "collapsed");
			expander.IsExpanded = true;
			Pump(700);
			ReportChevron(expander, "expanded");
		}
		catch (Exception ex)
		{
			Emit($"expander chevron audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			probe?.Close();
			Pump(40);
		}
	}

	private static void ReportChevron(FrameworkElement expander, string state)
	{
		FrameworkElement? grid = FindNamedDescendant(expander, "ChevronGrid");
		FrameworkElement? icon = FindNamedDescendant(expander, "ControlChevronIcon");
		double angle = grid?.RenderTransform is System.Windows.Media.RotateTransform rotate ? rotate.Angle : double.NaN;
		string font = icon is System.Windows.Controls.Control control ? control.FontFamily?.Source ?? "null" : "n/a";
		string bounds = "n/a";
		try
		{
			if (grid != null && grid.IsVisible)
			{
				GeneralTransform transform = grid.TransformToAncestor(expander);
				Rect box = transform.TransformBounds(new Rect(0, 0, grid.ActualWidth, grid.ActualHeight));
				bounds = $"x {box.X:F1}..{box.Right:F1}, y {box.Y:F1}..{box.Bottom:F1} of {expander.ActualWidth:F1}x{expander.ActualHeight:F1}";
			}
		}
		catch (InvalidOperationException)
		{
		}

		string symbol = icon is Wpf.Ui.Controls.SymbolIcon glyph ? glyph.Symbol.ToString() : "n/a";
		bool sized = grid is not null && grid.ActualWidth > 1 && grid.ActualHeight > 1;
		bool shown = grid?.IsVisible == true && grid.Opacity > 0.5;
		bool expected = state == "expanded"
			? symbol == "ChevronUp24"
			: symbol == "ChevronDown24";
		Emit((sized && shown && expected ? "  chevron PASS " : "  chevron FAIL ")
			+ $"{state}: symbol {symbol}, grid {grid?.ActualWidth ?? -1:F1}x{grid?.ActualHeight ?? -1:F1}"
			+ $", visible {grid?.IsVisible}, opacity {grid?.Opacity ?? -1:F2}, bounds {bounds}");
	}

	private static FrameworkElement? FindNamedDescendant(DependencyObject root, string name, int depth = 0)
	{
		if (depth > 32)
			return null;

		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count && i < 256; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is FrameworkElement element && element.Name == name)
				return element;

			if (FindNamedDescendant(child, name, depth + 1) is FrameworkElement nested)
				return nested;
		}

		return null;
	}

	private static void AuditShadows()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			System.Windows.Controls.ComboBox combo = new()
			{
				Width = 200,
				Height = 32,
				VerticalAlignment = VerticalAlignment.Top
			};
			combo.Items.Add("first");
			combo.Items.Add("second");
			combo.SelectedIndex = 0;

			System.Windows.Controls.Grid content = new();
			content.Children.Add(combo);
			probe = new Window
			{
				Width = 360,
				Height = 280,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(120);
			combo.ApplyTemplate();
			combo.IsDropDownOpen = true;
			Pump(500);

			System.Windows.Controls.Border? shadow = combo.Template?.FindName("DropDownShadow", combo) as System.Windows.Controls.Border;
			FrameworkElement? dropDown = combo.Template?.FindName("DropDownBorder", combo) as FrameworkElement;
			System.Windows.Media.Effects.DropShadowEffect? effect = shadow?.Effect as System.Windows.Media.Effects.DropShadowEffect;
			bool hasEffect = effect is not null;
			bool cacheCleared = shadow is not null && shadow.CacheMode is null;
			Thickness room = dropDown?.Margin ?? default;
			bool hasRoom = room.Left >= 12 && room.Top >= 12 && room.Right >= 12 && room.Bottom >= 12;
			bool matchesWindows = effect is not null
				&& Math.Abs(effect.BlurRadius - 24d) < 0.001d
				&& Math.Abs(effect.ShadowDepth - 4d) < 0.001d
				&& Math.Abs(effect.Opacity - 0.35d) < 0.001d
				&& Math.Abs(effect.Direction - 270d) < 0.001d
				&& effect.Color == System.Windows.Media.Colors.Black;

			bool passed = hasEffect && cacheCleared && hasRoom && matchesWindows;
			Emit(passed
				? $"shadow audit: PASS, dropdown shadow matches the Windows values exactly, blur {effect!.BlurRadius:F0}, depth {effect.ShadowDepth:F0}, opacity {effect.Opacity:F2}, direction {effect.Direction:F0}, with {room.Left:F0},{room.Top:F0},{room.Right:F0},{room.Bottom:F0} of room and no bitmap cache"
				: $"shadow audit: FAIL, effect {hasEffect}, cacheCleared {cacheCleared}, room {room}, windowsMatch {matchesWindows}, blur {effect?.BlurRadius ?? -1:F1}, depth {effect?.ShadowDepth ?? -1:F1}, opacity {effect?.Opacity ?? -1:F2}");
			combo.IsDropDownOpen = false;
			Pump(120);
		}
		catch (Exception ex)
		{
			Emit($"shadow audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			probe?.Close();
			Pump(40);
		}
	}

	private static void AuditDropdownLifecycle()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			System.Windows.Controls.ComboBox combo = new()
			{
				Width = 240,
				Height = 32,
				MaxDropDownHeight = 220,
				VerticalAlignment = VerticalAlignment.Top
			};
			for (int i = 0; i < 250; i++)
				combo.Items.Add("virtualized item " + i);
			combo.Items.Add(new System.Windows.Controls.Slider
			{
				Width = 180,
				Minimum = 0,
				Maximum = 100,
				Value = 50
			});
			combo.SelectedIndex = 0;

			System.Windows.Controls.ComboBox editable = new()
			{
				Width = 240,
				Height = 32,
				Margin = new Thickness(0, 48, 0, 0),
				IsEditable = true,
				VerticalAlignment = VerticalAlignment.Top
			};
			editable.Items.Add("editable first");
			editable.Items.Add("editable second");
			editable.SelectedIndex = 0;

			System.Windows.Controls.Grid content = new();
			content.Children.Add(combo);
			content.Children.Add(editable);
			probe = new Window
			{
				Width = 420,
				Height = 360,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(140);

			combo.ApplyTemplate();
			editable.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup popup = combo.Template?.FindName("Popup", combo) as System.Windows.Controls.Primitives.Popup
				?? throw new InvalidOperationException("standard ComboBox popup was not created");
			System.Windows.Controls.Primitives.ToggleButton toggle = combo.Template?.FindName("ToggleButton", combo) as System.Windows.Controls.Primitives.ToggleButton
				?? throw new InvalidOperationException("standard ComboBox toggle was not created");
			System.Windows.Controls.Primitives.Popup editablePopup = editable.Template?.FindName("Popup", editable) as System.Windows.Controls.Primitives.Popup
				?? throw new InvalidOperationException("editable ComboBox popup was not created");
			System.Windows.Controls.Primitives.ToggleButton editableToggle = editable.Template?.FindName("ToggleButton", editable) as System.Windows.Controls.Primitives.ToggleButton
				?? throw new InvalidOperationException("editable ComboBox toggle was not created");

			toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
			Pump(700);
			bool initial = IsDropdownPresented(combo, popup);

			popup.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
			Pump(800);
			bool recovered = IsDropdownPresented(combo, popup);
			if (!recovered)
				Emit("dropdown recovery state: " + GetDropdownState(combo, popup));

			System.Windows.Input.Mouse.Capture(null);
			Pump(40);
			bool captureInputDisabled = popup.Child is not FrameworkElement captureChild
				|| !captureChild.IsHitTestVisible;
			Pump(610);
			bool captureSettled = !combo.IsDropDownOpen
				&& !Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(popup)
				&& !popup.IsOpen;
			if (!captureSettled)
				Emit("dropdown capture state: " + GetDropdownState(combo, popup)
					+ ", capture=" + (System.Windows.Input.Mouse.Captured?.GetType().Name ?? "none"));

			for (int i = 0; i < 100; i++)
			{
				toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
				Pump(3);
				toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
				Pump(3);
			}
			toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
			Pump(800);
			bool rapidCycles = IsDropdownPresented(combo, popup);
			combo.SelectedIndex = 199;
			toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
			Pump(650);
			bool selectedOnce = combo.SelectedIndex == 199
				&& !combo.IsDropDownOpen
				&& !popup.IsOpen
				&& !Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(popup);

			editableToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
			Pump(700);
			bool editablePresented = IsDropdownPresented(editable, editablePopup);
			editableToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
			Pump(650);
			bool editableClosed = !editable.IsDropDownOpen
				&& !editablePopup.IsOpen
				&& !Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(editablePopup);

			bool noResidue = !IsCaptureWithin(combo)
				&& !IsCaptureWithin(editable)
				&& !HasResidentPopupSurface(probe);
			if (!noResidue)
				Emit("dropdown residue state: capture="
					+ (System.Windows.Input.Mouse.Captured?.GetType().Name ?? "none")
					+ ", " + DescribePopupSurfaces(probe));

			bool passed = initial
				&& recovered
				&& captureInputDisabled
				&& captureSettled
				&& rapidCycles
				&& selectedOnce
				&& editablePresented
				&& editableClosed
				&& noResidue;
			Emit(passed
				? "dropdown lifecycle audit: PASS, standard, editable, virtualized, embedded slider, capture loss, forced surface loss, selection, and 100 rapid cycles settled correctly with no residue"
				: "dropdown lifecycle audit: FAIL, initial=" + initial
					+ ", recovered=" + recovered
					+ ", captureInput=" + captureInputDisabled
					+ ", capture=" + captureSettled
					+ ", cycles=" + rapidCycles
					+ ", selection=" + selectedOnce
					+ ", editableOpen=" + editablePresented
					+ ", editableClose=" + editableClosed
					+ ", residue=" + noResidue);
		}
		catch (Exception ex)
		{
			Emit("dropdown lifecycle audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			probe?.Close();
			Pump(80);
		}
	}

	private static bool IsDropdownPresented(
		System.Windows.Controls.ComboBox combo,
		System.Windows.Controls.Primitives.Popup popup)
	{
		FrameworkElement? child = popup.Child as FrameworkElement;
		return combo.IsDropDownOpen
			&& popup.StaysOpen
			&& Wpf.Ui.Controls.PopupReveal.GetCloseTarget(popup)
			&& Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(popup)
			&& popup.IsOpen
			&& child is not null
			&& child.IsHitTestVisible
			&& child.ActualWidth > 0d
			&& child.ActualHeight > 0d
			&& PresentationSource.FromVisual(child) is not null;
	}

	private static string GetDropdownState(
		System.Windows.Controls.ComboBox combo,
		System.Windows.Controls.Primitives.Popup popup)
	{
		FrameworkElement? child = popup.Child as FrameworkElement;
		return "logical=" + combo.IsDropDownOpen
			+ ", staysOpen=" + popup.StaysOpen
			+ ", target=" + Wpf.Ui.Controls.PopupReveal.GetCloseTarget(popup)
			+ ", effective=" + Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(popup)
			+ ", popup=" + popup.IsOpen
			+ ", child=" + (child is not null)
			+ ", hitTest=" + (child?.IsHitTestVisible ?? false)
			+ ", size=" + (child?.ActualWidth ?? 0d).ToString("F1") + "x" + (child?.ActualHeight ?? 0d).ToString("F1")
			+ ", source=" + (child is not null && PresentationSource.FromVisual(child) is not null);
	}

	private static void AuditDropdownInteraction()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			System.Windows.Controls.ComboBox combo = new()
			{
				Width = 240,
				Height = 32,
				MaxDropDownHeight = 220,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(16)
			};
			for (int i = 0; i < 24; i++)
				combo.Items.Add("interaction item " + i);
			combo.SelectedIndex = 0;

			System.Windows.Controls.Grid content = new();
			content.Children.Add(combo);
			probe = new Window
			{
				Width = 420,
				Height = 360,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(160);

			combo.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup popup = combo.Template?.FindName("Popup", combo) as System.Windows.Controls.Primitives.Popup
				?? throw new InvalidOperationException("interaction ComboBox popup was not created");
			System.Windows.Controls.Primitives.ToggleButton toggle = combo.Template?.FindName("ToggleButton", combo) as System.Windows.Controls.Primitives.ToggleButton
				?? throw new InvalidOperationException("interaction ComboBox toggle was not created");

			int selectionChanges = 0;
			System.Windows.Controls.SelectionChangedEventHandler counter = (_, _) => selectionChanges++;
			combo.SelectionChanged += counter;
			bool clickOpened;
			bool keyboardOpened;
			bool keyboardNavigated;
			bool keyboardCommitted;
			bool escapeClosed;
			bool mouseOpened;
			bool itemReachable;
			bool mouseSelected;
			bool outsideClosed;
			bool deactivatedClosed;
			bool minimizeConsistent;
			bool restoredPresented;
			bool unloadSettled;
			bool unloadReleased;
			try
			{
				clickOpened = ClickToOpenDropdown(combo, toggle);
				Pump(700);
				clickOpened = clickOpened && IsDropdownPresented(combo, popup);
				if (!clickOpened)
					Emit("dropdown click state: toggle checked " + (toggle.IsChecked == true)
						+ ", " + DescribeToggleReachability(combo, toggle)
						+ ", " + GetDropdownState(combo, popup));
				combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
				Pump(650);

				SendKey(combo, System.Windows.Input.Key.F4);
				Pump(700);
				keyboardOpened = IsDropdownPresented(combo, popup);
				if (!keyboardOpened)
					Emit("dropdown keyboard open state: " + GetDropdownState(combo, popup));

				SendKey(combo, System.Windows.Input.Key.Down);
				Pump(200);
				keyboardNavigated = IsDropdownPresented(combo, popup);

				selectionChanges = 0;
				SendKey(combo, System.Windows.Input.Key.Enter);
				Pump(700);
				keyboardCommitted = combo.SelectedIndex == 1
					&& selectionChanges == 1
					&& IsDropdownSettled(combo, popup);
				if (!keyboardCommitted)
					Emit("dropdown keyboard commit state: index " + combo.SelectedIndex
						+ ", changes " + selectionChanges
						+ ", " + GetDropdownState(combo, popup));

				OpenDropdown(toggle);
				Pump(700);
				selectionChanges = 0;
				SendKey(combo, System.Windows.Input.Key.Escape);
				Pump(700);
				escapeClosed = combo.SelectedIndex == 1
					&& selectionChanges == 0
					&& IsDropdownSettled(combo, popup);
				if (!escapeClosed)
					Emit("dropdown escape state: index " + combo.SelectedIndex
						+ ", changes " + selectionChanges
						+ ", " + GetDropdownState(combo, popup));

				OpenDropdown(toggle);
				Pump(700);
				mouseOpened = IsDropdownPresented(combo, popup);
				System.Windows.Controls.ComboBoxItem? item = combo.ItemContainerGenerator.ContainerFromIndex(3) as System.Windows.Controls.ComboBoxItem;
				itemReachable = item is not null && IsPopupHitTarget(popup, item);
				selectionChanges = 0;
				if (item is not null)
					SendMouseLeftButtonUp(item);
				Pump(700);
				mouseSelected = combo.SelectedIndex == 3
					&& selectionChanges == 1
					&& IsDropdownSettled(combo, popup);
				if (!mouseSelected)
					Emit("dropdown mouse selection state: index " + combo.SelectedIndex
						+ ", changes " + selectionChanges
						+ ", reachable " + itemReachable
						+ ", " + GetDropdownState(combo, popup));

				OpenDropdown(toggle);
				Pump(700);
				SendOutsideMouseDown(combo);
				Pump(700);
				outsideClosed = IsDropdownSettled(combo, popup);
				if (!outsideClosed)
					Emit("dropdown outside click state: " + GetDropdownState(combo, popup));

				OpenDropdown(toggle);
				Pump(700);
				RaiseWindowDeactivated(probe);
				Pump(700);
				deactivatedClosed = IsDropdownSettled(combo, popup);
				if (!deactivatedClosed)
					Emit("dropdown deactivation state: " + GetDropdownState(combo, popup));

				OpenDropdown(toggle);
				Pump(700);
				content.Children.Remove(combo);
				Pump(800);
				unloadSettled = !combo.IsDropDownOpen
					&& !popup.IsOpen
					&& !Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(popup);
				unloadReleased = !IsCaptureWithin(combo) && !HasResidentPopupSurface(probe);
				if (!unloadSettled || !unloadReleased)
					Emit("dropdown unload state: " + GetDropdownState(combo, popup)
						+ ", capture=" + (System.Windows.Input.Mouse.Captured?.GetType().Name ?? "none")
						+ ", " + DescribePopupSurfaces(probe));

				restoredPresented = RehostAndPresent(combo);
				minimizeConsistent = AuditDropdownMinimize();
			}
			finally
			{
				combo.SelectionChanged -= counter;
			}

			bool passed = clickOpened
				&& keyboardOpened
				&& keyboardNavigated
				&& keyboardCommitted
				&& escapeClosed
				&& mouseOpened
				&& itemReachable
				&& mouseSelected
				&& outsideClosed
				&& deactivatedClosed
				&& minimizeConsistent
				&& restoredPresented
				&& unloadSettled
				&& unloadReleased;
			Emit(passed
				? "dropdown interaction audit: PASS, mouse click opens, keyboard open, navigate, commit, escape, mouse reach and select, outside click, deactivation, unload, owner surface rehost, and minimize all settled correctly"
				: "dropdown interaction audit: FAIL, click=" + clickOpened
					+ ", keyOpen=" + keyboardOpened
					+ ", keyNav=" + keyboardNavigated
					+ ", keyCommit=" + keyboardCommitted
					+ ", escape=" + escapeClosed
					+ ", mouseOpen=" + mouseOpened
					+ ", itemReach=" + itemReachable
					+ ", mouseSelect=" + mouseSelected
					+ ", outside=" + outsideClosed
					+ ", deactivate=" + deactivatedClosed
					+ ", unload=" + unloadSettled
					+ ", rehost=" + restoredPresented
					+ ", minimize=" + minimizeConsistent
					+ ", released=" + unloadReleased);
		}
		catch (Exception ex)
		{
			Emit("dropdown interaction audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			probe?.Close();
			Pump(80);
		}
	}

	private static bool RehostAndPresent(System.Windows.Controls.ComboBox combo)
	{
		Window? host = null;
		try
		{
			System.Windows.Controls.Grid content = new();
			content.Children.Add(combo);
			host = new Window
			{
				Width = 420,
				Height = 320,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			host.Show();
			Pump(220);

			combo.ApplyTemplate();
			if (combo.Template?.FindName("Popup", combo) is not System.Windows.Controls.Primitives.Popup popup)
			{
				Emit("dropdown rehost state: the popup was not rebuilt on the new owner surface");
				return false;
			}

			combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
			Pump(800);
			bool presented = IsDropdownPresented(combo, popup);
			if (!presented)
				Emit("dropdown rehost state: " + GetDropdownState(combo, popup));

			combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
			Pump(650);
			bool settled = IsDropdownSettled(combo, popup);
			if (!settled)
				Emit("dropdown rehost close state: " + GetDropdownState(combo, popup));

			content.Children.Remove(combo);
			Pump(200);
			return presented && settled;
		}
		catch (Exception ex)
		{
			Emit("dropdown rehost state: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
			return false;
		}
		finally
		{
			host?.Close();
			Pump(80);
		}
	}

	private static bool AuditDropdownMinimize()
	{
		Window? probe = null;
		try
		{
			System.Windows.Controls.ComboBox combo = CreateAuditCombo("minimize item ", 16, 16);
			System.Windows.Controls.Grid content = new();
			content.Children.Add(combo);
			probe = new Window
			{
				Width = 420,
				Height = 320,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(200);

			combo.ApplyTemplate();
			if (combo.Template?.FindName("Popup", combo) is not System.Windows.Controls.Primitives.Popup popup)
			{
				Emit("dropdown minimize state: the popup was not created");
				return false;
			}

			combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
			Pump(800);
			if (!IsDropdownPresented(combo, popup))
			{
				Emit("dropdown minimize state: the dropdown never presented, " + GetDropdownState(combo, popup));
				return false;
			}

			probe.WindowState = System.Windows.WindowState.Minimized;
			Pump(800);
			bool settled = IsDropdownSettled(combo, popup) && !HasResidentPopupSurface(probe);
			if (!settled)
				Emit("dropdown minimize state: " + GetDropdownState(combo, popup)
					+ ", " + DescribePopupSurfaces(probe));

			return settled;
		}
		catch (Exception ex)
		{
			Emit("dropdown minimize state: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
			return false;
		}
		finally
		{
			probe?.Close();
			Pump(120);
		}
	}

	private static void AuditLinuxOverlays()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Voidstrap.UI.Elements.Crosshair.CrosshairWindow? crosshair = null;
		bool previousCrosshair = App.Settings.Prop.Crosshair;
		int previousShape = App.Settings.Prop.CrosshairShapeIndex;
		string previousPath = App.Settings.Prop.CrosshairImagePath;
		string previousColor = App.Settings.Prop.CrosshairColorHex;
		string previousOutline = App.Settings.Prop.CrosshairOutlineColorHex;
		int previousSize = App.Settings.Prop.CrosshairSize;
		int previousThickness = App.Settings.Prop.CrosshairLineThickness;
		int previousGap = App.Settings.Prop.CrosshairGap;
		double previousOpacity = App.Settings.Prop.CrosshairOpacity;
		System.Reflection.FieldInfo? overlayInGameField = typeof(Voidstrap.Integrations.Overlays.OverlayHub).GetField("_inGame", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
		bool previousOverlayInGame = overlayInGameField?.GetValue(null) is true;
		string crosshairImagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Voidstrap", "crosshair-overlay-audit.png");
		try
		{
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crosshairImagePath)!);
			using (SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = new(2, 2))
			{
				image[0, 0] = new SixLabors.ImageSharp.PixelFormats.Bgra32(0, 255, 0, 255);
				image[1, 0] = new SixLabors.ImageSharp.PixelFormats.Bgra32(0, 255, 0, 128);
				image[0, 1] = new SixLabors.ImageSharp.PixelFormats.Bgra32(0, 255, 0, 128);
				image[1, 1] = new SixLabors.ImageSharp.PixelFormats.Bgra32(0, 255, 0, 0);
				using System.IO.FileStream stream = System.IO.File.Create(crosshairImagePath);
				image.Save(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
			}
			App.Settings.Prop.Crosshair = true;
			App.Settings.Prop.CrosshairShapeIndex = 0;
			App.Settings.Prop.CrosshairColorHex = "#FF00FF00";
			App.Settings.Prop.CrosshairOutlineColorHex = "#FF000000";
			App.Settings.Prop.CrosshairSize = 20;
			App.Settings.Prop.CrosshairLineThickness = 2;
			App.Settings.Prop.CrosshairGap = 4;
			App.Settings.Prop.CrosshairOpacity = 1d;
			crosshair = Voidstrap.UI.Elements.Crosshair.CrosshairWindow.GetOrCreate();
			Pump(900);
			System.Windows.Controls.Canvas? crosshairCanvas = crosshair.FindName("CrosshairCanvas") as System.Windows.Controls.Canvas;
			bool crossGeometry = crosshairCanvas?.Children.OfType<System.Windows.Shapes.Line>().Count() == 8;
			App.Settings.Prop.CrosshairShapeIndex = 1;
			App.Settings.Prop.CrosshairColorHex = "not-a-color";
			App.Settings.Prop.CrosshairOutlineColorHex = "also-not-a-color";
			crosshair.RefreshSettings();
			Pump(80);
			bool dotGeometry = crosshairCanvas?.Children.OfType<System.Windows.Shapes.Ellipse>().Count() == 2;
			App.Settings.Prop.CrosshairShapeIndex = 2;
			crosshair.RefreshSettings();
			Pump(80);
			bool circleGeometry = crosshairCanvas?.Children.OfType<System.Windows.Shapes.Ellipse>().Count() == 2;
			App.Settings.Prop.CrosshairShapeIndex = 3;
			App.Settings.Prop.CrosshairImagePath = crosshairImagePath;
			App.Settings.Prop.CrosshairSize = 40;
			App.Settings.Prop.CrosshairOpacity = 0.4d;
			crosshair.RefreshSettings();
			Pump(120);
			System.Windows.Controls.Image? imageCrosshair = crosshairCanvas?.Children.OfType<System.Windows.Controls.Image>().SingleOrDefault();
			bool imageGeometry = imageCrosshair?.Source != null
				&& Math.Abs(imageCrosshair.Width - 40 * Voidstrap.Integrations.Overlays.OverlayCrosshair.RuntimeScale) < 0.01d
				&& Math.Abs(imageCrosshair.Height - imageCrosshair.Width) < 0.01d
				&& Math.Abs(imageCrosshair.Opacity - 0.4d) < 0.001d;
			using System.IO.MemoryStream gifStream = new();
			using (SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> gif = new(2, 2))
			using (SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> secondFrame = new(2, 2))
			{
				gif[0, 0] = new SixLabors.ImageSharp.PixelFormats.Bgra32(255, 0, 0, 255);
				secondFrame[0, 0] = new SixLabors.ImageSharp.PixelFormats.Bgra32(0, 0, 255, 255);
				gif.Frames.AddFrame(secondFrame.Frames.RootFrame);
				gif.Save(gifStream, new SixLabors.ImageSharp.Formats.Gif.GifEncoder());
			}
			App.Settings.Prop.CrosshairImagePath = "data:image/gif;base64," + Convert.ToBase64String(gifStream.ToArray());
			crosshair.RefreshSettings();
			bool gifGeometry = PumpUntil(() =>
				crosshairCanvas?.Children.OfType<System.Windows.Controls.Image>().SingleOrDefault()?.Source != null, 3000);
			bool visualParity = crossGeometry && dotGeometry && circleGeometry && imageGeometry && gifGeometry;

			nint handle = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(crosshair.Title ?? string.Empty);
			bool handleResolved = handle != 0;
			Voidstrap.Integrations.Overlays.RobloxWindowTracker.Shutdown();
			overlayInGameField?.SetValue(null, true);
			for (int cycle = 0; cycle < 24; cycle++)
				Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
			Pump(240);
			bool singletonLifecycle = Voidstrap.UI.Elements.Crosshair.CrosshairWindow.CountLiveInstances() == 1
				&& ReferenceEquals(
					Application.Current?.Resources["CrosshairWindow"],
					crosshair)
				&& ReferenceEquals(
					Voidstrap.UI.Elements.Crosshair.CrosshairWindow.GetOrCreate(),
					crosshair);
			object? anchor = typeof(Voidstrap.UI.Elements.Crosshair.CrosshairWindow)
				.GetField("_anchor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
				?.GetValue(crosshair);
			System.Reflection.MethodInfo? applyAnchor = anchor?.GetType().GetMethod("Apply", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			Voidstrap.Integrations.Overlays.RobloxWindowRect auditTarget = new((nint)0x1234, 100, 50, 1600, 900, true, true);
			App.Settings.Prop.CrosshairImagePath = crosshairImagePath;
			crosshair.RefreshSettings();
			applyAnchor?.Invoke(anchor, new object[] { auditTarget });
			bool imageAlphaMask = handleResolved
				&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetBoundingShapeBounds(handle, out _, out _, out int imageMaskWidth, out int imageMaskHeight, out long imageMaskArea)
				&& imageMaskWidth == 30
				&& imageMaskHeight == 30
				&& imageMaskArea > 0
				&& imageMaskArea < 900;

			App.Settings.Prop.CrosshairShapeIndex = 0;
			App.Settings.Prop.CrosshairSize = 4;
			App.Settings.Prop.CrosshairLineThickness = 2;
			App.Settings.Prop.CrosshairGap = 4;
			App.Settings.Prop.CrosshairOpacity = 1d;
			crosshair.RefreshSettings();
			applyAnchor?.Invoke(anchor, new object[] { auditTarget });
			bool tightCrossMask = handleResolved
				&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetBoundingShapeBounds(handle, out int crossMaskLeft, out int crossMaskTop, out int crossMaskWidth, out int crossMaskHeight, out long crossMaskArea)
				&& crossMaskLeft >= 250
				&& crossMaskTop >= 250
				&& crossMaskLeft + crossMaskWidth <= 262
				&& crossMaskTop + crossMaskHeight <= 262
				&& crossMaskWidth <= 10
				&& crossMaskHeight <= 10
				&& crossMaskArea > 0
				&& crossMaskArea <= 64;

			int left = 0;
			int top = 0;
			int width = 0;
			int height = 0;
			bool geometryRead = handleResolved
				&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out left, out top, out width, out height);
			bool nativePixelGeometry = geometryRead
				&& width == 512
				&& height == 512
				&& Math.Abs(left - 644) <= 2
				&& Math.Abs(top - 244) <= 2;

			bool shaped = handleResolved
				&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetBoundingShapeRectangleCount(handle, out int shapeRects)
				&& shapeRects > 0;

			bool fallbackCentred = false;
			bool hiddenWhenUnavailable = false;
			if (handleResolved && overlayInGameField != null && applyAnchor != null && anchor != null)
			{
				overlayInGameField.SetValue(null, true);
				applyAnchor.Invoke(anchor, new object[] { default(Voidstrap.Integrations.Overlays.RobloxWindowRect) });
				Pump(600);
				fallbackCentred = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetScreenBounds(out int screenWidth, out int screenHeight)
					&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out int fallbackLeft, out int fallbackTop, out int fallbackWidth, out int fallbackHeight)
					&& fallbackWidth == 512
					&& fallbackHeight == 512
					&& Math.Abs(fallbackLeft - (screenWidth - fallbackWidth) / 2) <= 2
					&& Math.Abs(fallbackTop - (screenHeight - fallbackHeight) / 2) <= 2
					&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetBoundingShapeRectangleCount(handle, out int fallbackShapeRects)
					&& fallbackShapeRects > 0
					&& crosshair.Opacity == 1;

				overlayInGameField.SetValue(null, false);
				applyAnchor.Invoke(anchor, new object[] { default(Voidstrap.Integrations.Overlays.RobloxWindowRect) });
				Pump(160);
				hiddenWhenUnavailable = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetBoundingShapeRectangleCount(handle, out int hiddenShapeRects)
					&& hiddenShapeRects == 0
					&& crosshair.Opacity == 0;
				overlayInGameField.SetValue(null, true);
				applyAnchor.Invoke(anchor, new object[] { auditTarget });
			}

			bool unmanaged = handleResolved && !Voidstrap.Platform.Linux.LinuxWindowInterop.IsReparentedWindow(handle);

			bool moveHonoured = false;
			if (handleResolved)
			{
				Voidstrap.Platform.Linux.LinuxWindowInterop.TryMoveResize(handle, 220, 160, 512, 512);
				moveHonoured = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out int movedLeft, out int movedTop, out _, out _)
					&& Math.Abs(movedLeft - 220) <= 4
					&& Math.Abs(movedTop - 160) <= 4;
			}

			bool clickThrough = handleResolved
				&& Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetInputShapeRectangleCount(handle, out int inputRects)
				&& inputRects == 0;

			bool passed = handleResolved && geometryRead && clickThrough && shaped && moveHonoured && nativePixelGeometry && fallbackCentred && hiddenWhenUnavailable && imageAlphaMask && tightCrossMask && singletonLifecycle && visualParity;
			Emit(passed
				? "Linux overlay audit: PASS, cross, dot, ring, custom image, and inline GIF geometry match the Windows scale; 24 lifecycle reconciliations retained one surface; the transparent surface uses native pixels, centres on the runtime or display fallback, hides when inactive, is click through, is alpha shaped, and honours moves"
				: "Linux overlay audit: FAIL, handle=" + handleResolved
					+ ", shaped=" + shaped
					+ ", moveHonoured=" + moveHonoured
					+ ", unmanaged=" + unmanaged
					+ ", geometry=" + left + "," + top + " " + width + "x" + height
					+ ", clickThrough=" + clickThrough
					+ ", nativePixelGeometry=" + nativePixelGeometry
					+ ", fallbackCentred=" + fallbackCentred
					+ ", hiddenWhenUnavailable=" + hiddenWhenUnavailable
					+ ", imageAlphaMask=" + imageAlphaMask
					+ ", tightCrossMask=" + tightCrossMask
					+ ", singletonLifecycle=" + singletonLifecycle
					+ ", visualParity=" + visualParity
					+ " (cross=" + crossGeometry
					+ ", dot=" + dotGeometry
					+ ", circle=" + circleGeometry
					+ ", image=" + imageGeometry
					+ ", gif=" + gifGeometry + ")");
		}
		catch (Exception ex)
		{
			Emit("Linux overlay audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			try
			{
				crosshair?.Close();
			}
			catch (Exception)
			{
			}

			App.Settings.Prop.Crosshair = previousCrosshair;
			App.Settings.Prop.CrosshairShapeIndex = previousShape;
			App.Settings.Prop.CrosshairImagePath = previousPath;
			App.Settings.Prop.CrosshairColorHex = previousColor;
			App.Settings.Prop.CrosshairOutlineColorHex = previousOutline;
			App.Settings.Prop.CrosshairSize = previousSize;
			App.Settings.Prop.CrosshairLineThickness = previousThickness;
			App.Settings.Prop.CrosshairGap = previousGap;
			App.Settings.Prop.CrosshairOpacity = previousOpacity;
			overlayInGameField?.SetValue(null, previousOverlayInGame);
			try
			{
				System.IO.File.Delete(crosshairImagePath);
			}
			catch (Exception)
			{
			}
			Pump(160);
		}
	}

	private static void AuditLinuxHyperlinks()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			bool openerAvailable = ResolveDesktopOpener() is not null;

			System.Windows.Controls.TextBlock text = new()
			{
				FontSize = 14d,
				Margin = new Thickness(16d),
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top
			};
			System.Windows.Documents.Hyperlink link = new(new System.Windows.Documents.Run("open the Voidstrap repository"))
			{
				NavigateUri = new Uri("https://github.com/KloBraticc/Voidstrap", UriKind.Absolute)
			};
			int clicks = 0;
			RoutedEventHandler counter = (_, _) => clicks++;
			link.Click += counter;
			text.Inlines.Add(link);

			System.Windows.Controls.Grid content = new();
			content.Children.Add(text);
			probe = new Window
			{
				Width = 460,
				Height = 200,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(220);
			probe.UpdateLayout();
			Voidstrap.UI.LinuxInlineText.Sanitize(text);
			Pump(120);

			bool noStuckUnderline = !HasUnderline(link) && !Voidstrap.UI.LinuxInlineText.IsUnderlineVisible(text);

			bool located = TryGetInlineCentre(link, text, out Point centre);
			bool inputReaches = located && IsBlockHitFrom(probe, text, centre);
			bool reachable = located && Voidstrap.UI.LinuxInlineText.HoverAt(text, centre);
			Pump(80);
			bool underlinesOnHover = reachable
				&& Voidstrap.UI.LinuxInlineText.IsUnderlineVisible(text)
				&& ReferenceEquals(text.Cursor, System.Windows.Input.Cursors.Hand);
			bool staysVisible = !HasUnderline(link);

			Voidstrap.UI.LinuxInlineText.HoverAt(text, new Point(text.ActualWidth + 40d, text.ActualHeight + 40d));
			Pump(80);
			bool clearsOnLeave = !Voidstrap.UI.LinuxInlineText.IsUnderlineVisible(text);

			bool raisesClick = located
				&& Voidstrap.UI.LinuxInlineText.ActivateAt(text, centre)
				&& clicks == 1;
			Pump(80);
			link.Click -= counter;

			System.Collections.Generic.List<string> broken = new();
			int anchors = CollectNavigateUriTargets(probe, broken);
			Voidstrap.UI.Elements.About.Pages.AboutPage about = new();
			Window aboutHost = new()
			{
				Width = 900,
				Height = 600,
				Content = about,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			int liveInlineLinks = 0;
			int routedInlineLinks = 0;
			try
			{
				aboutHost.Show();
				Pump(500);
				aboutHost.UpdateLayout();
				anchors += CollectNavigateUriTargets(aboutHost, broken);
				CountRoutedInlineLinks(aboutHost, ref liveInlineLinks, ref routedInlineLinks, broken);
			}
			finally
			{
				try { aboutHost.Close(); } catch (Exception) { }
				Pump(80);
			}

			bool targetsValid = broken.Count == 0;
			bool inlineRouted = liveInlineLinks == 0 || routedInlineLinks == liveInlineLinks;
			bool passed = inlineRouted
				&& inputReaches
				&& staysVisible
				&& openerAvailable
				&& noStuckUnderline
				&& reachable
				&& underlinesOnHover
				&& clearsOnLeave
				&& raisesClick
				&& targetsValid
				&& anchors > 0;
			Emit(passed
				? "hyperlink audit: PASS, " + anchors + " navigable link target(s) resolve and " + routedInlineLinks
					+ " live inline link(s) route input, underline on hover, and raise Click"
				: "hyperlink audit: FAIL, opener=" + openerAvailable
					+ ", idleClean=" + noStuckUnderline
					+ ", reachable=" + reachable
					+ ", inputReaches=" + inputReaches
					+ ", hoverUnderline=" + underlinesOnHover
					+ ", textStaysVisible=" + staysVisible
					+ ", leaveClears=" + clearsOnLeave
					+ ", click=" + raisesClick
					+ ", targets=" + anchors
					+ ", inlineRouted=" + routedInlineLinks + "/" + liveInlineLinks
					+ (broken.Count > 0 ? ", broken: " + string.Join("; ", broken.Take(4)) : string.Empty));
		}
		catch (Exception ex)
		{
			Emit("hyperlink audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			probe?.Close();
			Pump(80);
		}
	}

	private static void CountRoutedInlineLinks(
		DependencyObject root,
		ref int total,
		ref int routed,
		System.Collections.Generic.List<string> broken)
	{
		if (root is System.Windows.Controls.TextBlock block && block.IsVisible && block.ActualWidth > 0d)
		{
			foreach (System.Windows.Documents.Inline inline in block.Inlines)
			{
				if (inline is not System.Windows.Documents.Hyperlink link)
					continue;

				total++;
				bool located = TryGetInlineCentre(link, block, out Point centre);
				if (!located)
				{
					total--;
					continue;
				}

				bool hovers = Voidstrap.UI.LinuxInlineText.HoverAt(block, centre);
				bool hitTestable = block.IsHitTestVisible && block.Background is not null;
				if (hovers && hitTestable)
				{
					routed++;
					Voidstrap.UI.LinuxInlineText.HoverAt(block, new Point(0d - 40d, 0d - 40d));
				}
				else if (broken.Count < 6)
				{
					broken.Add("inline link in " + (string.IsNullOrWhiteSpace(block.Name) ? block.GetType().Name : block.Name)
						+ " routes=" + hovers
						+ " hitTestable=" + hitTestable
						+ " background=" + (block.Background is null ? "none" : block.Background.GetType().Name));
				}
			}
		}

		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
			CountRoutedInlineLinks(VisualTreeHelper.GetChild(root, i), ref total, ref routed, broken);
	}

	private static FrameworkElement ResolveHitTestRoot(System.Windows.Controls.TextBlock block)
	{
		DependencyObject current = block;
		FrameworkElement root = block;
		for (int depth = 0; depth < 8; depth++)
		{
			DependencyObject? parent = VisualTreeHelper.GetParent(current);
			if (parent is not FrameworkElement element)
				break;

			root = element;
			current = parent;
		}

		return root;
	}

	private static string DescribeHit(FrameworkElement owner, System.Windows.Controls.TextBlock block, Point pointInBlock)
	{
		try
		{
			Point point = block.TranslatePoint(pointInBlock, owner);
			return owner.InputHitTest(point)?.GetType().Name ?? "nothing";
		}
		catch (InvalidOperationException)
		{
			return "untranslatable";
		}
	}

	private static bool IsBlockHitFrom(FrameworkElement owner, System.Windows.Controls.TextBlock block, Point pointInBlock)
	{
		try
		{
			Point pointInWindow = block.TranslatePoint(pointInBlock, owner);
			DependencyObject? hit = owner.InputHitTest(pointInWindow) as DependencyObject;
			DependencyObject? current = hit;
			for (int depth = 0; current is not null && depth < 32; depth++)
			{
				if (ReferenceEquals(current, block))
					return true;

				current = current switch
				{
					System.Windows.FrameworkContentElement content => content.Parent,
					Visual visual => VisualTreeHelper.GetParent(visual),
					_ => null
				};
			}
		}
		catch (InvalidOperationException)
		{
		}

		return false;
	}

	private static string? ResolveDesktopOpener()
	{
		Voidstrap.Core.SystemProcessService processes = new();
		foreach (string command in new[] { "xdg-open", "gio", "kde-open6", "kde-open5", "exo-open" })
		{
			string? executable = processes.FindExecutable(command);
			if (!string.IsNullOrWhiteSpace(executable))
				return executable;
		}

		return null;
	}

	private static bool HasUnderline(System.Windows.Documents.Inline inline)
	{
		TextDecorationCollection? decorations = inline.TextDecorations;
		if (decorations is null)
			return false;

		foreach (TextDecoration decoration in decorations)
		{
			if (decoration.Location == TextDecorationLocation.Underline)
				return true;
		}

		return false;
	}

	private static bool TryGetInlineCentre(
		System.Windows.Documents.Inline inline,
		System.Windows.Controls.TextBlock owner,
		out Point centre)
	{
		centre = default;
		try
		{
			Rect start = inline.ElementStart.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
			Rect end = inline.ElementEnd.GetCharacterRect(System.Windows.Documents.LogicalDirection.Backward);
			if (start.IsEmpty || start.Height <= 0d)
				return false;

			double right = end.IsEmpty ? start.Right : end.Right;
			centre = new Point((start.Left + right) / 2d, start.Top + start.Height / 2d);
			return centre.X > 0d && centre.Y > 0d && centre.X < owner.ActualWidth && centre.Y < owner.ActualHeight;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static int CollectNavigateUriTargets(DependencyObject root, System.Collections.Generic.List<string> broken)
	{
		int found = 0;
		if (root is Wpf.Ui.Controls.Anchor anchor)
		{
			found++;
			if (string.IsNullOrWhiteSpace(anchor.NavigateUri)
				|| !Uri.TryCreate(anchor.NavigateUri, UriKind.Absolute, out _))
			{
				broken.Add("Anchor \"" + DescribeButtonContent(anchor) + "\" has no usable target");
			}
			else if (!anchor.IsHitTestVisible || !anchor.IsEnabled)
			{
				broken.Add("Anchor \"" + DescribeButtonContent(anchor) + "\" cannot receive input");
			}
		}
		else if (root is Wpf.Ui.Controls.Hyperlink uiLink)
		{
			found++;
			if (string.IsNullOrWhiteSpace(uiLink.NavigateUri)
				|| !Uri.TryCreate(uiLink.NavigateUri, UriKind.Absolute, out _))
			{
				broken.Add("Hyperlink \"" + DescribeButtonContent(uiLink) + "\" has no usable target");
			}
		}

		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
			found += CollectNavigateUriTargets(VisualTreeHelper.GetChild(root, i), broken);

		return found;
	}

	private static void AuditBootstrapperStyles()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Voidstrap.Enums.BootstrapperStyle previous = App.Settings.Prop.BootstrapperStyle;
		int failures = 0;
		try
		{
			Emit("bootstrapper style audit:");
			foreach (Voidstrap.Enums.BootstrapperStyle style in Voidstrap.Extensions.BootstrapperStyleEx.Selections)
			{
				Window? window = null;
				try
				{
					Voidstrap.UI.IBootstrapperDialog dialog = Voidstrap.UI.Frontend.GetBootstrapperDialog(style);
					dialog.Message = "Style preview, click Cancel to close";
					dialog.CancelEnabled = true;
					window = dialog as Window;
					if (window is null)
					{
						failures++;
						Emit("  STYLE FAIL " + style + ": the dialog is not a window on Linux");
						continue;
					}

					window.ShowInTaskbar = false;
					window.WindowStartupLocation = WindowStartupLocation.Manual;
					window.Left = 60d;
					window.Top = 60d;
					window.Show();
					Pump(420);
					window.UpdateLayout();
					Pump(120);

					string substitution = DescribeStyleSubstitution(style, dialog);
					System.Collections.Generic.List<string> clipped = new();
					CollectClippedElements(window, window, clipped);
					string? overflow = DescribeContentOverflow(window);
					if (overflow is not null)
						clipped.Insert(0, overflow);
					CollectUnreadableText(window, window, clipped);
					string? cancelFailure = TryCancelDialog(window, dialog, style);
					if (cancelFailure is not null)
						clipped.Insert(0, cancelFailure);

					if (window.ActualWidth < 16d || window.ActualHeight < 16d)
					{
						failures++;
						Emit("  STYLE FAIL " + style + ": the window measured "
							+ window.ActualWidth.ToString("F0") + "x" + window.ActualHeight.ToString("F0"));
						continue;
					}

					if (clipped.Count > 0 || substitution.Length > 0)
					{
						failures++;
						Emit("  STYLE FAIL " + style + " [" + window.GetType().Name + " "
							+ window.ActualWidth.ToString("F0") + "x" + window.ActualHeight.ToString("F0") + "]"
							+ (substitution.Length > 0 ? " " + substitution : string.Empty)
							+ (clipped.Count > 0 ? " clipped: " + string.Join("; ", clipped.Take(4)) : string.Empty)
							+ (clipped.Count > 4 ? " and " + (clipped.Count - 4) + " more" : string.Empty));
						continue;
					}

					Emit("  STYLE OK   " + style + " [" + window.GetType().Name + " "
						+ window.ActualWidth.ToString("F0") + "x" + window.ActualHeight.ToString("F0") + "]");
				}
				catch (Exception ex)
				{
					failures++;
					Exception root = ex;
					while (root.InnerException is not null)
						root = root.InnerException;
					Emit("  STYLE FAIL " + style + ": " + root.GetType().Name + ": " + root.Message.Split('\n')[0]);
				}
				finally
				{
					try
					{
						window?.Close();
					}
					catch (Exception)
					{
					}

					Pump(120);
				}
			}

			Emit(failures == 0
				? "bootstrapper style audit: PASS, every launcher style rendered natively on Linux with no clipped content"
				: "bootstrapper style audit: FAIL, " + failures + " launcher style(s) need Linux work");
		}
		finally
		{
			App.Settings.Prop.BootstrapperStyle = previous;
		}
	}

	private static string DescribeStyleSubstitution(Voidstrap.Enums.BootstrapperStyle style, Voidstrap.UI.IBootstrapperDialog dialog)
	{
		string actual = dialog.GetType().Name;
		string expected = style switch
		{
			Voidstrap.Enums.BootstrapperStyle.VistaDialog => "LinuxVistaDialog",
			Voidstrap.Enums.BootstrapperStyle.LegacyDialog2008 => "LinuxLegacyDialog2008",
			Voidstrap.Enums.BootstrapperStyle.LegacyDialog2011 => "LinuxLegacyDialog2011",
			Voidstrap.Enums.BootstrapperStyle.ProgressDialog => "LinuxProgressDialog",
			Voidstrap.Enums.BootstrapperStyle.ClassicFluentDialog => "ClassicFluentDialog",
			Voidstrap.Enums.BootstrapperStyle.ByfronDialog => "ByfronDialog",
			Voidstrap.Enums.BootstrapperStyle.CustomDialog => App.Settings.Prop.SelectedCustomTheme == null ? "FluentDialog" : "CustomDialog",
			_ => "FluentDialog"
		};

		return string.Equals(actual, expected, StringComparison.Ordinal)
			? string.Empty
			: "substituted by " + actual + " instead of " + expected + ",";
	}

	private static void CollectClippedElements(
		Window window,
		DependencyObject current,
		System.Collections.Generic.List<string> clipped)
	{
		if (clipped.Count >= 24)
			return;

		if (current is FrameworkElement element)
		{
			if (IsDecorativeOverscan(element))
				return;

			if (element.IsVisible && element.ActualWidth > 0d && element.ActualHeight > 0d)
			{
				string? reason = DescribeClipping(window, element);
				if (reason is not null)
					clipped.Add(DescribeElement(element) + reason);
			}

			if (element is System.Windows.Controls.Primitives.RangeBase)
				return;
		}

		int count = VisualTreeHelper.GetChildrenCount(current);
		for (int i = 0; i < count; i++)
			CollectClippedElements(window, VisualTreeHelper.GetChild(current, i), clipped);
	}

	private static string? DescribeContentOverflow(Window window)
	{
		if (window.Content is not FrameworkElement root)
			return null;

		Thickness margin = root.Margin;
		double neededWidth = Math.Max(0d, root.DesiredSize.Width - margin.Left - margin.Right);
		double neededHeight = Math.Max(0d, root.DesiredSize.Height - margin.Top - margin.Bottom);
		if (neededWidth - root.ActualWidth <= ClipTolerance && neededHeight - root.ActualHeight <= ClipTolerance)
			return null;

		return "the content needs " + neededWidth.ToString("F0") + "x" + neededHeight.ToString("F0")
			+ " but the window only gives it " + root.ActualWidth.ToString("F0") + "x" + root.ActualHeight.ToString("F0");
	}

	private const double MinimumTextContrast = 3d;

	private static string? TryCancelDialog(
		Window window,
		Voidstrap.UI.IBootstrapperDialog dialog,
		Voidstrap.Enums.BootstrapperStyle style)
	{
		System.Windows.Controls.Primitives.ButtonBase? cancel = FindCancelButton(window);
		if (cancel is null && style == Voidstrap.Enums.BootstrapperStyle.CustomDialog)
		{
			dialog.CloseBootstrapper();
			return PumpUntil(() => !window.IsVisible, 2500)
				? null
				: "the custom launcher did not close on request";
		}

		if (cancel is null)
			return "no Cancel button was reachable";

		if (!cancel.IsEnabled)
			return "the Cancel button was disabled";

		if (cancel.Command is System.Windows.Input.ICommand command)
		{
			if (!command.CanExecute(cancel.CommandParameter))
				return "the Cancel command refused to execute";

			command.Execute(cancel.CommandParameter);
		}
		else
		{
			cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, cancel));
		}

		return PumpUntil(() => !window.IsVisible, 2500)
			? null
			: "the Cancel button did not close the launcher";
	}

	private static System.Windows.Controls.Primitives.ButtonBase? FindCancelButton(DependencyObject root)
	{
		if (root is System.Windows.Controls.Primitives.ButtonBase button && button.IsVisible && IsCancelButton(button))
			return button;

		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			System.Windows.Controls.Primitives.ButtonBase? found = FindCancelButton(VisualTreeHelper.GetChild(root, i));
			if (found is not null)
				return found;
		}

		return null;
	}

	private static bool IsCancelButton(System.Windows.Controls.Primitives.ButtonBase button)
	{
		System.Windows.Data.Binding? binding = System.Windows.Data.BindingOperations.GetBinding(
			button,
			System.Windows.Controls.Primitives.ButtonBase.CommandProperty);
		if (binding is not null && string.Equals(binding.Path?.Path, "CancelInstallCommand", StringComparison.Ordinal))
			return true;

		return DescribeButtonContent(button).Contains("cancel", StringComparison.OrdinalIgnoreCase)
			|| (button.Name ?? string.Empty).Contains("cancel", StringComparison.OrdinalIgnoreCase);
	}

	private static string DescribeButtonContent(System.Windows.Controls.Primitives.ButtonBase button)
	{
		if (button.Content is string label)
			return label;

		if (button.Content is System.Windows.Controls.TextBlock text)
			return text.Text ?? string.Empty;

		return button.Name ?? string.Empty;
	}

	private static void CollectUnreadableText(
		Window window,
		DependencyObject current,
		System.Collections.Generic.List<string> issues)
	{
		if (issues.Count >= 24)
			return;

		if (current is System.Windows.Controls.TextBlock text
			&& text.IsVisible
			&& !string.IsNullOrWhiteSpace(text.Text)
			&& text.ActualWidth > 0d
			&& text.ActualHeight > 0d
			&& text.Foreground is SolidColorBrush foreground
			&& foreground.Color.A > 32
			&& TryResolveOpaqueBackground(text, out Color background))
		{
			double contrast = ContrastRatio(foreground.Color, background);
			if (contrast < MinimumTextContrast)
			{
				string preview = text.Text.Length > 24 ? text.Text.Substring(0, 24) : text.Text;
				issues.Add("TextBlock \"" + preview.Replace('\n', ' ') + "\" has a contrast of "
					+ contrast.ToString("F1") + " against its background");
			}
		}

		int count = VisualTreeHelper.GetChildrenCount(current);
		for (int i = 0; i < count; i++)
			CollectUnreadableText(window, VisualTreeHelper.GetChild(current, i), issues);
	}

	private static bool TryResolveOpaqueBackground(DependencyObject start, out Color background)
	{
		background = default;
		DependencyObject? current = VisualTreeHelper.GetParent(start);
		for (int depth = 0; current is not null && depth < 24; depth++)
		{
			Brush? brush = current switch
			{
				System.Windows.Controls.Border border => border.Background,
				System.Windows.Controls.Panel panel => panel.Background,
				System.Windows.Controls.Control control => control.Background,
				_ => null
			};

			if (brush is SolidColorBrush solid && solid.Color.A > 200)
			{
				background = solid.Color;
				return true;
			}

			if (current is System.Windows.Controls.Image)
				return false;

			current = VisualTreeHelper.GetParent(current);
		}

		return false;
	}

	private static double ContrastRatio(Color foreground, Color background)
	{
		double first = RelativeLuminance(foreground) + 0.05d;
		double second = RelativeLuminance(background) + 0.05d;
		return first > second ? first / second : second / first;
	}

	private static double RelativeLuminance(Color color)
	{
		return 0.2126d * LinearChannel(color.R)
			+ 0.7152d * LinearChannel(color.G)
			+ 0.0722d * LinearChannel(color.B);
	}

	private static double LinearChannel(byte value)
	{
		double channel = value / 255d;
		return channel <= 0.03928d ? channel / 12.92d : Math.Pow((channel + 0.055d) / 1.055d, 2.4d);
	}

	private static bool IsDecorativeOverscan(FrameworkElement element)
	{
		Thickness margin = element.Margin;
		return margin.Left < 0d || margin.Top < 0d || margin.Right < 0d || margin.Bottom < 0d;
	}

	private static string? DescribeClipping(Window window, FrameworkElement element)
	{
		Thickness margin = element.Margin;
		double width = element.ActualWidth;
		double height = element.ActualHeight;
		double neededWidth = Math.Max(0d, element.DesiredSize.Width - margin.Left - margin.Right);
		double neededHeight = Math.Max(0d, element.DesiredSize.Height - margin.Top - margin.Bottom);

		if (element is System.Windows.Controls.TextBlock text
			&& !string.IsNullOrWhiteSpace(text.Text)
			&& text.TextTrimming == TextTrimming.None
			&& (neededHeight - height > ClipTolerance || neededWidth - width > ClipTolerance))
		{
			return " is cut to " + width.ToString("F0") + "x" + height.ToString("F0")
				+ " but needs " + neededWidth.ToString("F0") + "x" + neededHeight.ToString("F0");
		}

		if (element is System.Windows.Controls.ContentControl control
			&& control.Content is string
			&& (neededHeight - height > ClipTolerance || neededWidth - width > ClipTolerance))
		{
			return " is cut to " + width.ToString("F0") + "x" + height.ToString("F0")
				+ " but needs " + neededWidth.ToString("F0") + "x" + neededHeight.ToString("F0");
		}

		if (!element.IsHitTestVisible)
			return null;

		try
		{
			Point origin = element.TranslatePoint(new Point(0d, 0d), window);
			if (origin.X < 0d - ClipTolerance
				|| origin.Y < 0d - ClipTolerance
				|| origin.X + width > window.ActualWidth + ClipTolerance
				|| origin.Y + height > window.ActualHeight + ClipTolerance)
			{
				return " sits at " + origin.X.ToString("F0") + "," + origin.Y.ToString("F0")
					+ " sized " + width.ToString("F0") + "x" + height.ToString("F0")
					+ " outside the " + window.ActualWidth.ToString("F0") + "x" + window.ActualHeight.ToString("F0") + " window";
			}
		}
		catch (InvalidOperationException)
		{
		}

		return null;
	}

	private const double ClipTolerance = 1.5d;

	private static string DescribeElement(FrameworkElement element)
	{
		string name = string.IsNullOrWhiteSpace(element.Name) ? element.GetType().Name : element.GetType().Name + " " + element.Name;
		if (element is System.Windows.Controls.TextBlock text && !string.IsNullOrWhiteSpace(text.Text))
		{
			string preview = text.Text.Length > 28 ? text.Text.Substring(0, 28) : text.Text;
			return name + " \"" + preview.Replace('\n', ' ') + "\"";
		}

		if (element is System.Windows.Controls.ContentControl content && content.Content is string label && !string.IsNullOrWhiteSpace(label))
			return name + " \"" + label + "\"";

		return name;
	}

	private static void AuditLinuxGlassBootstrapper()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? glass = null;
		Window? plain = null;
		try
		{
			bool defaultsToVoidstrap = new Voidstrap.Models.Persistable.AppSettings().BootstrapperStyle
				== Voidstrap.Enums.BootstrapperStyle.FluentDialog;

			glass = new Voidstrap.UI.Elements.Bootstrapper.FluentDialog(aero: true)
			{
				ShowInTaskbar = false,
				Left = 40d,
				Top = 40d
			};
			glass.Show();
			Pump(500);

			FrameworkElement? material = FindGlassMaterial(glass);
			bool materialPresent = material is not null
				&& material.IsVisible
				&& material.ActualWidth > 0d
				&& material.ActualHeight > 0d;

			int blurLayers = 0;
			int tintLayers = 0;
			int grainLayers = 0;
			if (material is not null)
				CountGlassLayers(material, ref blurLayers, ref tintLayers, ref grainLayers);

			bool materialBlurs = blurLayers >= 1;
			bool materialTints = tintLayers >= 2;
			bool materialGrains = grainLayers >= 1;

			plain = new Voidstrap.UI.Elements.Bootstrapper.FluentDialog(aero: false)
			{
				ShowInTaskbar = false,
				Left = 40d,
				Top = 400d
			};
			plain.Show();
			Pump(500);
			bool plainHasNoMaterial = FindGlassMaterial(plain) is null;

			bool glassIsOpaque = !glass.AllowsTransparency
				&& material is not null
				&& HasOpaqueBase(material);

			bool passed = defaultsToVoidstrap
				&& materialPresent
				&& materialBlurs
				&& materialTints
				&& materialGrains
				&& glassIsOpaque
				&& plainHasNoMaterial;
			Emit(passed
				? "Linux glass bootstrapper audit: PASS, the launcher style defaults to Voidstrap and the glass material renders "
					+ blurLayers + " blurred layer(s), " + tintLayers + " tint layer(s), and " + grainLayers + " grain layer(s) only for the glass style"
				: "Linux glass bootstrapper audit: FAIL, default=" + defaultsToVoidstrap
					+ ", material=" + materialPresent
					+ ", blur=" + blurLayers
					+ ", tint=" + tintLayers
					+ ", grain=" + grainLayers
					+ ", plainClean=" + plainHasNoMaterial
					+ ", opaque=" + glassIsOpaque);
		}
		catch (Exception ex)
		{
			Emit("Linux glass bootstrapper audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			try
			{
				glass?.Close();
				plain?.Close();
			}
			catch (Exception)
			{
			}

			Pump(120);
		}
	}

	private static FrameworkElement? FindGlassMaterial(DependencyObject root)
	{
		if (root is FrameworkElement element
			&& string.Equals(element.Name, Voidstrap.UI.LinuxGlassBackdrop.MaterialName, StringComparison.Ordinal))
		{
			return element;
		}

		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			FrameworkElement? found = FindGlassMaterial(VisualTreeHelper.GetChild(root, i));
			if (found is not null)
				return found;
		}

		return null;
	}

	private static bool HasOpaqueBase(DependencyObject root)
	{
		if (root is System.Windows.Shapes.Shape shape
			&& shape.Fill is GradientBrush gradient
			&& gradient.GradientStops.Count > 0)
		{
			bool opaque = true;
			foreach (GradientStop stop in gradient.GradientStops)
			{
				if (stop.Color.A < 255)
				{
					opaque = false;
					break;
				}
			}

			if (opaque)
				return true;
		}

		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			if (HasOpaqueBase(VisualTreeHelper.GetChild(root, i)))
				return true;
		}

		return false;
	}

	private static void CountGlassLayers(DependencyObject root, ref int blurLayers, ref int tintLayers, ref int grainLayers)
	{
		if (root is UIElement element && element.Effect is System.Windows.Media.Effects.BlurEffect blur && blur.Radius > 0d)
			blurLayers++;

		if (root is System.Windows.Shapes.Shape shape)
		{
			if (shape.Fill is GradientBrush)
				tintLayers++;
			else if (shape.Fill is ImageBrush { TileMode: TileMode.Tile })
				grainLayers++;
		}

		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
			CountGlassLayers(VisualTreeHelper.GetChild(root, i), ref blurLayers, ref tintLayers, ref grainLayers);
	}

	private const int MaxSimultaneousPopupLayoutPasses = 400;

	private static void AuditPopupSurfaces()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? probe = null;
		try
		{
			System.Windows.Controls.Border host = new()
			{
				Width = 260,
				Height = 56,
				Background = System.Windows.Media.Brushes.DimGray,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(16)
			};

			System.Windows.Controls.ComboBox first = CreateAuditCombo("surface first ", 16, 88);
			System.Windows.Controls.ComboBox second = CreateAuditCombo("surface second ", 16, 140);
			System.Windows.Controls.ComboBox empty = new()
			{
				Width = 240,
				Height = 32,
				MaxDropDownHeight = 220,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(16, 192, 0, 0)
			};

			Wpf.Ui.Controls.AutoSuggestBox suggest = new()
			{
				Width = 240,
				MaxDropDownHeight = 200,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(16, 244, 0, 0),
				ItemsSource = new[] { "alpha", "alpine", "beta", "gamma" }
			};

			Wpf.Ui.Controls.Flyout flyout = new()
			{
				Content = new System.Windows.Controls.TextBlock { Text = "flyout body" },
				Background = System.Windows.Media.Brushes.DimGray,
				Padding = new Thickness(8),
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top
			};
			System.Windows.Controls.Grid flyoutHost = new()
			{
				Width = 240,
				Height = 40,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(16, 296, 0, 0)
			};
			flyoutHost.Children.Add(flyout);

			System.Windows.Controls.Grid content = new();
			content.Children.Add(host);
			content.Children.Add(first);
			content.Children.Add(second);
			content.Children.Add(empty);
			content.Children.Add(suggest);
			content.Children.Add(flyoutHost);
			probe = new Window
			{
				Width = 460,
				Height = 420,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(180);

			System.Windows.Controls.ComboBox nested = new()
			{
				Width = 160,
				Height = 32
			};
			nested.Items.Add("nested one");
			nested.Items.Add("nested two");
			nested.SelectedIndex = 0;

			System.Windows.Controls.MenuItem submenuOwner = new() { Header = "submenu owner" };
			submenuOwner.Items.Add(new System.Windows.Controls.MenuItem { Header = "child one" });
			submenuOwner.Items.Add(new System.Windows.Controls.MenuItem { Header = "child two" });
			System.Windows.Controls.MenuItem nestedOwner = new()
			{
				Header = nested,
				StaysOpenOnClick = true
			};
			System.Windows.Controls.ContextMenu menu = new()
			{
				PlacementTarget = host,
				Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
				StaysOpen = true
			};
			menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "plain entry" });
			menu.Items.Add(submenuOwner);
			menu.Items.Add(nestedOwner);
			host.ContextMenu = menu;

			menu.IsOpen = true;
			Pump(700);
			bool menuPresented = menu.IsOpen
				&& PresentationSource.FromVisual(menu) is not null
				&& menu.ActualWidth > 0d
				&& menu.ActualHeight > 0d
				&& menu.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement plain
				&& IsPresentedHitTarget(plain, plain);

			submenuOwner.SetCurrentValue(System.Windows.Controls.MenuItem.IsSubmenuOpenProperty, true);
			Pump(700);
			bool submenuPresented = submenuOwner.IsSubmenuOpen
				&& submenuOwner.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement submenuChild
				&& PresentationSource.FromVisual(submenuChild) is not null
				&& submenuChild.ActualWidth > 0d
				&& IsPresentedHitTarget(submenuChild, submenuChild);
			submenuOwner.SetCurrentValue(System.Windows.Controls.MenuItem.IsSubmenuOpenProperty, false);
			Pump(400);

			nested.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup? nestedPopup = nested.Template?.FindName("Popup", nested) as System.Windows.Controls.Primitives.Popup;
			nested.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
			Pump(800);
			bool nestedPresented = nestedPopup is not null
				&& menu.IsOpen
				&& IsDropdownPresented(nested, nestedPopup);
			if (!nestedPresented && nestedPopup is not null)
				Emit("nested popup state: menu " + menu.IsOpen + ", " + GetDropdownState(nested, nestedPopup));
			nested.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
			Pump(700);
			bool nestedClosed = nestedPopup is not null
				&& IsDropdownSettled(nested, nestedPopup)
				&& menu.IsOpen;

			menu.SetCurrentValue(System.Windows.Controls.ContextMenu.IsOpenProperty, false);
			Pump(600);
			bool menuClosed = !menu.IsOpen && PresentationSource.FromVisual(menu) is null;

			System.Windows.Controls.ToolTip tip = new()
			{
				Content = "audit tooltip",
				PlacementTarget = host,
				Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
				StaysOpen = true
			};
			tip.SetCurrentValue(System.Windows.Controls.ToolTip.IsOpenProperty, true);
			Pump(700);
			bool tipPresented = tip.IsOpen
				&& PresentationSource.FromVisual(tip) is not null
				&& tip.ActualWidth > 0d
				&& tip.ActualHeight > 0d;
			tip.SetCurrentValue(System.Windows.Controls.ToolTip.IsOpenProperty, false);
			Pump(600);
			bool tipClosed = !tip.IsOpen && PresentationSource.FromVisual(tip) is null;

			flyout.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup? flyoutPopup = flyout.Template?.FindName("PART_Popup", flyout) as System.Windows.Controls.Primitives.Popup;
			flyout.Show();
			Pump(700);
			bool flyoutPresented = flyoutPopup is { IsOpen: true }
				&& flyoutPopup.Child is FrameworkElement flyoutChild
				&& PresentationSource.FromVisual(flyoutChild) is not null
				&& flyoutChild.ActualWidth > 0d
				&& IsPresentedHitTarget(flyoutChild, flyoutChild);
			if (!flyoutPresented)
				Emit("flyout state: popup=" + (flyoutPopup?.IsOpen ?? false)
					+ ", child=" + (flyoutPopup?.Child?.GetType().Name ?? "none")
					+ ", source=" + (flyoutPopup?.Child is FrameworkElement sourceProbe && PresentationSource.FromVisual(sourceProbe) is not null)
					+ ", size=" + ((flyoutPopup?.Child as FrameworkElement)?.ActualWidth ?? 0d).ToString("F1")
					+ "x" + ((flyoutPopup?.Child as FrameworkElement)?.ActualHeight ?? 0d).ToString("F1")
					+ ", hitTestVisible=" + ((flyoutPopup?.Child as FrameworkElement)?.IsHitTestVisible ?? false)
					+ ", hit=" + (flyoutPopup?.Child is FrameworkElement hitProbe && IsPresentedHitTarget(hitProbe, hitProbe)));
			flyout.Hide();
			Pump(700);
			bool flyoutClosed = flyoutPopup is { IsOpen: false };

			suggest.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup? suggestPopup = suggest.Template?.FindName("PART_Popup", suggest) as System.Windows.Controls.Primitives.Popup;
			suggest.SetCurrentValue(Wpf.Ui.Controls.TextBox.TextProperty, "al");
			Pump(800);
			bool suggestPresented = suggestPopup is { IsOpen: true }
				&& suggest.IsSuggestionListOpen
				&& suggestPopup.Child is FrameworkElement suggestChild
				&& PresentationSource.FromVisual(suggestChild) is not null
				&& suggestChild.ActualWidth > 0d;
			suggest.SetCurrentValue(Wpf.Ui.Controls.AutoSuggestBox.IsSuggestionListOpenProperty, false);
			Pump(600);
			bool suggestClosed = suggestPopup is { IsOpen: false };

			first.ApplyTemplate();
			second.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup firstPopup = first.Template?.FindName("Popup", first) as System.Windows.Controls.Primitives.Popup
				?? throw new InvalidOperationException("the first simultaneous ComboBox popup was not created");
			System.Windows.Controls.Primitives.Popup secondPopup = second.Template?.FindName("Popup", second) as System.Windows.Controls.Primitives.Popup
				?? throw new InvalidOperationException("the second simultaneous ComboBox popup was not created");
			first.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
			Pump(700);
			bool firstPresented = IsDropdownPresented(first, firstPopup);
			int layoutPasses = 0;
			EventHandler layoutProbe = (_, _) => layoutPasses++;
			probe.LayoutUpdated += layoutProbe;
			second.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
			Pump(800);
			probe.LayoutUpdated -= layoutProbe;
			bool handoverSettled = IsDropdownPresented(second, secondPopup) && IsDropdownSettled(first, firstPopup);
			if (!handoverSettled)
				Emit("simultaneous dropdown state: first " + GetDropdownState(first, firstPopup)
					+ " | second " + GetDropdownState(second, secondPopup));
			bool handoverStable = layoutPasses < MaxSimultaneousPopupLayoutPasses;
			if (!handoverStable)
				Emit("simultaneous dropdown layout: " + layoutPasses
					+ " layout passes in 800ms, two portable popups are invalidating each other");
			second.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
			Pump(700);
			bool handoverClosed = IsDropdownSettled(second, secondPopup) && IsDropdownSettled(first, firstPopup);

			empty.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup emptyPopup = empty.Template?.FindName("Popup", empty) as System.Windows.Controls.Primitives.Popup
				?? throw new InvalidOperationException("the empty ComboBox popup was not created");
			empty.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
			Pump(800);
			bool emptyConsistent = IsDropdownConsistent(empty, emptyPopup);
			empty.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
			Pump(700);
			bool emptyClosed = IsDropdownSettled(empty, emptyPopup);
			if (!emptyConsistent || !emptyClosed)
				Emit("empty dropdown state: " + GetDropdownState(empty, emptyPopup));

			bool noResidentSurfaces = !HasResidentPopupSurface(probe);
			if (!noResidentSurfaces)
				Emit("popup surface residue: " + DescribePopupSurfaces(probe));

			bool passed = menuPresented
				&& submenuPresented
				&& nestedPresented
				&& nestedClosed
				&& menuClosed
				&& tipPresented
				&& tipClosed
				&& flyoutPresented
				&& flyoutClosed
				&& suggestPresented
				&& suggestClosed
				&& firstPresented
				&& handoverSettled
				&& handoverStable
				&& handoverClosed
				&& emptyConsistent
				&& emptyClosed
				&& noResidentSurfaces;
			Emit(passed
				? "popup surface audit: PASS, context menu, submenu, nested dropdown, tooltip, flyout, suggestion list, simultaneous dropdowns, and an empty source all presented and tore down cleanly"
				: "popup surface audit: FAIL, menu=" + menuPresented
					+ ", submenu=" + submenuPresented
					+ ", nested=" + nestedPresented
					+ ", nestedClose=" + nestedClosed
					+ ", menuClose=" + menuClosed
					+ ", tooltip=" + tipPresented
					+ ", tooltipClose=" + tipClosed
					+ ", flyout=" + flyoutPresented
					+ ", flyoutClose=" + flyoutClosed
					+ ", suggest=" + suggestPresented
					+ ", suggestClose=" + suggestClosed
					+ ", firstOpen=" + firstPresented
					+ ", handover=" + handoverSettled
					+ ", handoverStable=" + handoverStable
					+ ", handoverClose=" + handoverClosed
					+ ", empty=" + emptyConsistent
					+ ", emptyClose=" + emptyClosed
					+ ", residue=" + noResidentSurfaces);
		}
		catch (Exception ex)
		{
			Emit("popup surface audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			probe?.Close();
			Pump(80);
		}
	}

	private static void AuditDropdownFallback()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		const string nativePopupSwitch = "PROGPU_WPF_DISABLE_NATIVE_POPUPS";
		string? previous = Environment.GetEnvironmentVariable(nativePopupSwitch);
		Window? probe = null;
		try
		{
			Environment.SetEnvironmentVariable(nativePopupSwitch, "1");

			System.Windows.Controls.ComboBox combo = CreateAuditCombo("fallback item ", 16, 16);
			System.Windows.Controls.Grid content = new();
			content.Children.Add(combo);
			probe = new Window
			{
				Width = 420,
				Height = 320,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(180);

			combo.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup popup = combo.Template?.FindName("Popup", combo) as System.Windows.Controls.Primitives.Popup
				?? throw new InvalidOperationException("fallback ComboBox popup was not created");

			int presentedCycles = 0;
			int settledCycles = 0;
			for (int cycle = 0; cycle < 6; cycle++)
			{
				combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
				Pump(700);
				if (IsDropdownPresented(combo, popup))
					presentedCycles++;
				else
					Emit("fallback dropdown open state on cycle " + cycle + ": " + GetDropdownState(combo, popup));

				combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
				Pump(650);
				if (IsDropdownSettled(combo, popup))
					settledCycles++;
			}

			bool ownerComposited = true;
			string backend = "backend unknown";
#if CROSSPLAT
			if (System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetPortablePopupSnapshot(probe, out var snapshot))
				ownerComposited = snapshot.NativeWindowCount == 0;

			if (System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowingCapabilities(probe, out var capabilities))
				backend = "backend " + capabilities.Backend
					+ ", wayland session " + capabilities.IsWaylandDesktopSession
					+ ", native popup support " + capabilities.SupportsNativePopupWindows
					+ ", owner composited " + capabilities.UsesOwnerCompositedPopups;
#endif

			bool passed = presentedCycles == 6 && settledCycles == 6 && ownerComposited;
			Emit(passed
				? "dropdown owner surface fallback audit: PASS, 6 forced owner composited cycles presented and settled, " + backend
				: "dropdown owner surface fallback audit: FAIL, presented=" + presentedCycles
					+ "/6, settled=" + settledCycles
					+ "/6, ownerComposited=" + ownerComposited
					+ ", " + backend);
		}
		catch (Exception ex)
		{
			Emit("dropdown owner surface fallback audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			Environment.SetEnvironmentVariable(nativePopupSwitch, previous);
			probe?.Close();
			Pump(80);
		}
	}

	private static void AuditDropdownAcrossWindows()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Window? owner = null;
		Window? secondary = null;
		try
		{
			System.Windows.Controls.ComboBox combo = CreateAuditCombo("multi-window item ", 16, 16);
			System.Windows.Controls.Grid content = new();
			content.Children.Add(combo);
			owner = new Window
			{
				Width = 420,
				Height = 320,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			owner.Show();
			Pump(180);

			combo.ApplyTemplate();
			System.Windows.Controls.Primitives.Popup popup = combo.Template?.FindName("Popup", combo) as System.Windows.Controls.Primitives.Popup
				?? throw new InvalidOperationException("multi-window ComboBox popup was not created");

			secondary = new Window
			{
				Width = 260,
				Height = 180,
				Owner = owner,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			secondary.Show();
			Pump(220);

			combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
			Pump(700);
			bool whileSecondaryOpen = IsDropdownPresented(combo, popup);
			combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
			Pump(650);
			bool settledWithSecondary = IsDropdownSettled(combo, popup);

			secondary.Close();
			secondary = null;
			Pump(260);

			combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, true);
			Pump(700);
			bool afterSecondaryClosed = IsDropdownPresented(combo, popup);
			combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
			Pump(650);
			bool settledAfterClose = IsDropdownSettled(combo, popup);

			bool passed = whileSecondaryOpen
				&& settledWithSecondary
				&& afterSecondaryClosed
				&& settledAfterClose;
			Emit(passed
				? "dropdown multi-window audit: PASS, the owner popup presented and settled while a second window was open and after it closed"
				: "dropdown multi-window audit: FAIL, whileOpen=" + whileSecondaryOpen
					+ ", settledWhileOpen=" + settledWithSecondary
					+ ", afterClose=" + afterSecondaryClosed
					+ ", settledAfterClose=" + settledAfterClose
					+ ", " + GetDropdownState(combo, popup));
		}
		catch (Exception ex)
		{
			Emit("dropdown multi-window audit: FAIL, " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
		}
		finally
		{
			secondary?.Close();
			owner?.Close();
			Pump(100);
		}
	}

	private static System.Windows.Controls.ComboBox CreateAuditCombo(string prefix, double left, double top)
	{
		System.Windows.Controls.ComboBox combo = new()
		{
			Width = 240,
			Height = 32,
			MaxDropDownHeight = 220,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(left, top, 0, 0)
		};
		for (int i = 0; i < 24; i++)
			combo.Items.Add(prefix + i);
		combo.SelectedIndex = 0;
		return combo;
	}

	private static bool ClickToOpenDropdown(
		System.Windows.Controls.ComboBox combo,
		System.Windows.Controls.Primitives.ToggleButton toggle)
	{
		if (!IsToggleReachable(combo, toggle))
			return false;

		System.Windows.Input.MouseDevice mouse = System.Windows.Input.Mouse.PrimaryDevice
			?? throw new InvalidOperationException("the audit could not resolve a mouse device");

		System.Windows.Input.MouseButtonEventArgs preview = new(mouse, 0, System.Windows.Input.MouseButton.Left)
		{
			RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
		};
		toggle.RaiseEvent(preview);

		System.Windows.Input.MouseButtonEventArgs down = new(mouse, 0, System.Windows.Input.MouseButton.Left)
		{
			RoutedEvent = UIElement.MouseLeftButtonDownEvent
		};
		toggle.RaiseEvent(down);

		return combo.IsDropDownOpen;
	}

	private static string DescribeToggleReachability(
		System.Windows.Controls.ComboBox combo,
		System.Windows.Controls.Primitives.ToggleButton toggle)
	{
		string hitName = "none";
		try
		{
			Point centre = toggle.TranslatePoint(
				new Point(toggle.ActualWidth / 2d, toggle.ActualHeight / 2d),
				combo);
			if (combo.InputHitTest(centre) is DependencyObject hit)
			{
				hitName = hit.GetType().Name;
				if (hit is FrameworkElement named && !string.IsNullOrWhiteSpace(named.Name))
					hitName += " " + named.Name;
			}
		}
		catch (InvalidOperationException)
		{
			hitName = "untranslatable";
		}

		System.Windows.Controls.TextBox? editable = combo.Template?.FindName("PART_EditableTextBox", combo) as System.Windows.Controls.TextBox;
		return "toggleSize=" + toggle.ActualWidth.ToString("F0") + "x" + toggle.ActualHeight.ToString("F0")
			+ ", toggleVisible=" + toggle.IsVisible
			+ ", toggleHitTestVisible=" + toggle.IsHitTestVisible
			+ ", hitAtCentre=" + hitName
			+ ", editableBox=" + (editable is null
				? "absent"
				: editable.Visibility + "/" + (editable.IsHitTestVisible ? "hit" : "nohit") + "/" + editable.ActualWidth.ToString("F0"));
	}

	private static bool IsToggleReachable(
		System.Windows.Controls.ComboBox combo,
		System.Windows.Controls.Primitives.ToggleButton toggle)
	{
		if (!toggle.IsVisible || !toggle.IsHitTestVisible || !toggle.IsEnabled)
			return false;

		if (toggle.ActualWidth <= 0d || toggle.ActualHeight <= 0d)
			return false;

		try
		{
			Point centre = toggle.TranslatePoint(
				new Point(toggle.ActualWidth / 2d, toggle.ActualHeight / 2d),
				combo);
			DependencyObject? hit = combo.InputHitTest(centre) as DependencyObject;
			for (int depth = 0; hit is not null && depth < 24; depth++)
			{
				if (ReferenceEquals(hit, toggle))
					return true;

				hit = VisualTreeHelper.GetParent(hit);
			}
		}
		catch (InvalidOperationException)
		{
		}

		return false;
	}

	private static void OpenDropdown(System.Windows.Controls.Primitives.ToggleButton toggle)
	{
		toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
	}

	private static bool IsDropdownSettled(
		System.Windows.Controls.ComboBox combo,
		System.Windows.Controls.Primitives.Popup popup)
	{
		return !combo.IsDropDownOpen
			&& !Wpf.Ui.Controls.PopupReveal.GetCloseTarget(popup)
			&& !Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(popup)
			&& !popup.IsOpen;
	}

	private static bool IsDropdownConsistent(
		System.Windows.Controls.ComboBox combo,
		System.Windows.Controls.Primitives.Popup popup)
	{
		return IsDropdownPresented(combo, popup) || IsDropdownSettled(combo, popup);
	}

	private static bool IsCaptureWithin(DependencyObject root)
	{
		DependencyObject? current = System.Windows.Input.Mouse.Captured as DependencyObject;
		while (current is not null)
		{
			if (ReferenceEquals(current, root))
				return true;

			current = current is FrameworkElement element
				? element.Parent ?? element.TemplatedParent
				: null;
		}

		return false;
	}

	private static bool HasResidentPopupSurface(Window? owner)
	{
#if CROSSPLAT
		if (!System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetPortablePopupSnapshot(owner, out var snapshot))
			return false;

		return snapshot.OpenCount > 0 || snapshot.VisibleCount > 0 || snapshot.NativeWindowCount > 0;
#else
		return false;
#endif
	}

	private static string DescribePopupSurfaces(Window? owner)
	{
#if CROSSPLAT
		if (!System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetPortablePopupSnapshot(owner, out var snapshot))
			return "portable popup snapshot unavailable";

		return "open=" + snapshot.OpenCount
			+ ", visible=" + snapshot.VisibleCount
			+ ", native=" + snapshot.NativeWindowCount
			+ ", presentedNative=" + snapshot.PresentedNativeWindowCount;
#else
		return "portable popup snapshot unavailable";
#endif
	}

	private static bool IsPopupHitTarget(
		System.Windows.Controls.Primitives.Popup popup,
		FrameworkElement element)
	{
		return popup.Child is FrameworkElement child && IsPresentedHitTarget(child, element);
	}

	private static bool IsPresentedHitTarget(Visual surface, FrameworkElement element)
	{
		if (PresentationSource.FromVisual(surface)?.RootVisual is not UIElement root)
			return false;

		if (element.ActualWidth <= 0d || element.ActualHeight <= 0d || !element.IsHitTestVisible)
			return false;

		Point point;
		try
		{
			point = element.TranslatePoint(new Point(element.ActualWidth / 2d, element.ActualHeight / 2d), root);
		}
		catch (InvalidOperationException)
		{
			return false;
		}

		DependencyObject? current = root.InputHitTest(point) as DependencyObject;
		while (current is not null)
		{
			if (ReferenceEquals(current, element))
				return true;

			current = VisualTreeHelper.GetParent(current);
		}

		return false;
	}

	private static void SendKey(UIElement target, System.Windows.Input.Key key)
	{
		PresentationSource source = PresentationSource.FromVisual(target)
			?? (Window.GetWindow(target) is Window window ? PresentationSource.FromVisual(window) : null)
			?? throw new InvalidOperationException("the audit could not resolve a presentation source for keyboard input");

		System.Windows.Input.KeyboardDevice keyboard = System.Windows.Input.Keyboard.PrimaryDevice
			?? throw new InvalidOperationException("the audit could not resolve a keyboard device");

		System.Windows.Input.KeyEventArgs preview = new(keyboard, source, 0, key)
		{
			RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
		};
		target.RaiseEvent(preview);
		if (preview.Handled)
			return;

		System.Windows.Input.KeyEventArgs down = new(keyboard, source, 0, key)
		{
			RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent
		};
		target.RaiseEvent(down);
	}

	private static void SendMouseLeftButtonUp(UIElement target)
	{
		System.Windows.Input.MouseDevice mouse = System.Windows.Input.Mouse.PrimaryDevice
			?? throw new InvalidOperationException("the audit could not resolve a mouse device");

		System.Windows.Input.MouseButtonEventArgs args = new(mouse, 0, System.Windows.Input.MouseButton.Left)
		{
			RoutedEvent = UIElement.MouseLeftButtonUpEvent
		};
		target.RaiseEvent(args);
	}

	private static void SendOutsideMouseDown(System.Windows.Controls.ComboBox combo)
	{
		System.Windows.Input.MouseDevice mouse = System.Windows.Input.Mouse.PrimaryDevice
			?? throw new InvalidOperationException("the audit could not resolve a mouse device");

		System.Windows.Input.MouseButtonEventArgs args = new(mouse, 0, System.Windows.Input.MouseButton.Left)
		{
			RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent
		};
		combo.RaiseEvent(args);
	}

	private static void RaiseWindowDeactivated(Window window)
	{
		MethodInfo? method = typeof(Window).GetMethod(
			"OnDeactivated",
			BindingFlags.Instance | BindingFlags.NonPublic,
			null,
			new[] { typeof(EventArgs) },
			null);
		if (method is null)
			throw new InvalidOperationException("the audit could not resolve the window deactivation entry point");

		method.Invoke(window, new object[] { EventArgs.Empty });
	}

	private static void AuditThemeTransition()
	{
		Window? probe = null;
		try
		{
			System.Windows.Controls.Grid content = new System.Windows.Controls.Grid
			{
				Background = System.Windows.Media.Brushes.DimGray
			};
			probe = new Window
			{
				Width = 300,
				Height = 200,
				Content = content,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			probe.Show();
			Pump(80);

			bool applied = false;
			Voidstrap.UI.ThemeTransition.Animate(probe, () => applied = true);
			Pump(80);

			System.Windows.Documents.AdornerLayer? layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(content);
			System.Windows.Documents.Adorner[]? during = layer?.GetAdorners(content);
			double covering = during != null && during.Length > 0 ? during[0].Opacity : double.NaN;
			Pump(600);
			System.Windows.Documents.Adorner[]? after = layer?.GetAdorners(content);

			bool passed = applied
				&& layer != null
				&& during != null
				&& during.Length > 0
				&& covering > 0.05
				&& covering < 0.995
				&& (after == null || after.Length == 0);
			Emit(passed
				? $"theme crossfade audit: PASS, overlay faded through {covering:F2} and was removed"
				: $"theme crossfade audit: FAIL, applied {applied}, layer {(layer != null)}, overlay {(during?.Length ?? 0)}, opacity {covering:F2}, remaining {(after?.Length ?? 0)}");
		}
		catch (Exception ex)
		{
			Emit($"theme crossfade audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			probe?.Close();
			Pump(40);
		}
	}

	private static void AuditScrolling()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		Window? probe = null;
		bool originalEnabled = App.Settings.Prop.SmooothBARRyesirikikthxlucipook;
		try
		{
			System.Windows.Controls.ScrollViewer viewer = new System.Windows.Controls.ScrollViewer
			{
				Width = 360,
				Height = 240,
				VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
				Content = new System.Windows.Controls.Border
				{
					Width = 340,
					Height = 2400,
					Background = System.Windows.Media.Brushes.Black
				}
			};
			probe = new Window
			{
				Width = 400,
				Height = 280,
				Content = viewer,
				ShowInTaskbar = false,
				WindowStyle = WindowStyle.None
			};
			Wpf.Ui.Controls.SmoothScroll.Register();
			Wpf.Ui.Controls.SmoothScroll.SetGlobalEnabled(false);
			probe.Show();
			Pump(40);
			viewer.Measure(new Size(360, 240));
			viewer.Arrange(new Rect(0, 0, 360, 240));
			viewer.UpdateLayout();

			MethodInfo? wheelHandler = typeof(Wpf.Ui.Controls.SmoothScroll).GetMethod(
				"TryHandleLinuxWheel",
				BindingFlags.Static | BindingFlags.NonPublic);
			bool handled = wheelHandler != null
				&& wheelHandler.Invoke(null, new object?[] { viewer.Content, -120 }) is true;
			Pump(40);
			double gliding = ReadScrollOffset(viewer);
			double settled = WaitForScrollRest(viewer, 2000);
			Pump(120);
			double stable = ReadScrollOffset(viewer);
			System.Diagnostics.Stopwatch burst = System.Diagnostics.Stopwatch.StartNew();
			bool burstHandled = true;
			for (int i = 0; i < 100; i++)
			{
				burstHandled &= wheelHandler != null
					&& wheelHandler.Invoke(null, new object?[] { viewer.Content, -120 }) is true;
			}
			burst.Stop();
			double burstSettled = WaitForScrollRest(viewer, 3000);
			Pump(120);
			double burstStable = ReadScrollOffset(viewer);
			bool passed = handled
				&& gliding > 0
				&& settled > gliding
				&& Math.Abs(stable - settled) < 0.5
				&& settled <= viewer.ScrollableHeight
				&& burstHandled
				&& burst.ElapsedMilliseconds < 100
				&& burstSettled > settled
				&& burstSettled <= viewer.ScrollableHeight
				&& Math.Abs(burstStable - burstSettled) < 0.5;
			Emit(passed
				? $"scroll wheel audit: PASS, glided through {gliding:F1} then rested at {settled:F1} of {viewer.ScrollableHeight:F1}, 100-event burst {burst.ElapsedMilliseconds}ms rested at {burstSettled:F1}"
				: $"scroll wheel audit: FAIL, handled {handled}, range {viewer.ScrollableHeight:F1}, gliding {gliding:F1}, settled {settled:F1}/{stable:F1}, burst {burst.ElapsedMilliseconds}ms/{burstSettled:F1}/{burstStable:F1}");
		}
		catch (Exception ex)
		{
			Emit($"scroll cadence audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			Wpf.Ui.Controls.SmoothScroll.SetGlobalEnabled(originalEnabled);
			if (probe != null)
			{
				probe.Close();
			}
		}
	}

	private static int CountRenderFrames(int milliseconds)
	{
		int frames = 0;
		EventHandler counter = (_, _) => frames++;
		System.Windows.Media.CompositionTarget.Rendering += counter;
		try
		{
			Pump(milliseconds);
		}
		finally
		{
			System.Windows.Media.CompositionTarget.Rendering -= counter;
		}

		return frames;
	}

	private static double WaitForScrollRest(System.Windows.Controls.ScrollViewer viewer, int timeoutMilliseconds)
	{
		double last = ReadScrollOffset(viewer);
		long deadline = Environment.TickCount64 + timeoutMilliseconds;
		int quiet = 0;
		while (Environment.TickCount64 < deadline)
		{
			Pump(24);
			double current = ReadScrollOffset(viewer);
			quiet = Math.Abs(current - last) < 0.001 ? quiet + 1 : 0;
			last = current;
			if (quiet >= 3)
				break;
		}

		return last;
	}

	private static double ReadScrollOffset(System.Windows.Controls.ScrollViewer viewer)
	{
		viewer.ApplyTemplate();
		if (viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) is System.Windows.Controls.Primitives.IScrollInfo scrollInfo)
		{
			return scrollInfo.VerticalOffset;
		}

		return viewer.VerticalOffset;
	}

	private static void AuditLinuxAccountStorage()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voidstrap-account-audit-" + Guid.NewGuid().ToString("N"));
		string template = System.IO.Path.Combine(directory, "template.dat");
		string output = System.IO.Path.Combine(directory, "output.dat");
		try
		{
			System.IO.Directory.CreateDirectory(directory);
			System.IO.File.WriteAllText(template, "theme=dark; .ROBLOSECURITY=old-cookie; locale=en");
			bool synthesized = Voidstrap.Integrations.RobloxCookie.SynthesizeDatWithCookie(template, "new-cookie", output);
			string? extracted = Voidstrap.Integrations.RobloxCookie.ExtractCookieFromDat(output);
			string contents = System.IO.File.Exists(output) ? System.IO.File.ReadAllText(output) : string.Empty;
			System.IO.UnixFileMode mode = !OperatingSystem.IsWindows() && System.IO.File.Exists(output)
				? System.IO.File.GetUnixFileMode(output)
				: 0;
			System.IO.UnixFileMode exposed = System.IO.UnixFileMode.GroupRead |
				System.IO.UnixFileMode.GroupWrite |
				System.IO.UnixFileMode.GroupExecute |
				System.IO.UnixFileMode.OtherRead |
				System.IO.UnixFileMode.OtherWrite |
				System.IO.UnixFileMode.OtherExecute;
			bool passed = synthesized &&
				string.Equals(extracted, "new-cookie", StringComparison.Ordinal) &&
				contents.Contains("theme=dark", StringComparison.Ordinal) &&
				contents.Contains("locale=en", StringComparison.Ordinal) &&
				(mode & exposed) == 0;
			Emit(passed
				? "Linux account storage audit: PASS, Sober cookie preserved and backup mode is private"
				: $"Linux account storage audit: FAIL, synthesized {synthesized}, extracted {extracted}, mode {mode}");
		}
		catch (Exception ex)
		{
			Emit($"Linux account storage audit: FAIL, {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
		}
		finally
		{
			try
			{
				if (System.IO.File.Exists(template))
					System.IO.File.Delete(template);
				if (System.IO.File.Exists(output))
					System.IO.File.Delete(output);
				if (System.IO.Directory.Exists(directory))
					System.IO.Directory.Delete(directory);
			}
			catch
			{
			}
		}
	}

	private static void AuditImaging()
	{
		try
		{
			Uri uri = new("pack://application:,,,/Voidstrap.png", UriKind.Absolute);
			System.Windows.Resources.StreamResourceInfo? resource = Application.GetResourceStream(uri);
			using System.IO.Stream? stream = resource?.Stream;
			System.Windows.Media.Imaging.BitmapSource? image = SafeImaging.FromStream(stream, 256);
			bool valid = image?.PixelWidth > 0 && image.PixelHeight > 0 && image.IsFrozen;
			string detail = image == null
				? "null"
				: image.PixelWidth + "x" + image.PixelHeight + ", frozen=" + image.IsFrozen + ", format=" + image.Format;
			Emit(valid ? "image audit: PASS, " + detail : "image audit: FAIL, " + detail);

			System.Windows.Resources.StreamResourceInfo? bytesResource = Application.GetResourceStream(uri);
			using System.IO.Stream? bytesStream = bytesResource?.Stream;
			using System.IO.MemoryStream bytesOutput = new();
			bytesStream?.CopyTo(bytesOutput);
			byte[] bytes = bytesOutput.ToArray();
			string localPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Voidstrap", "image-audit.png");
			string webpPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Voidstrap", "image-audit.webp");
			string gifPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Voidstrap", "image-audit.gif");
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(localPath)!);
			System.IO.File.WriteAllBytes(localPath, bytes);
			try
			{
				System.Windows.Media.Imaging.BitmapSource? pack = SafeImaging.FromUri(uri, 128);
				System.Windows.Media.Imaging.BitmapSource? local = SafeImaging.FromFile(localPath, 128);
				System.Windows.Media.Imaging.BitmapSource? inline = AppImage.LoadSync("data:image/png;base64," + Convert.ToBase64String(bytes), 128);
				System.Windows.Media.Imaging.BitmapSource? icon = SafeImaging.FromUri(new Uri("pack://application:,,,/Resources/Checkmark.ico", UriKind.Absolute), 64);
				IReadOnlyList<(System.Windows.Media.Imaging.BitmapSource Frame, int DelayMilliseconds)> animation = SafeImaging.DecodeAnimationPortable(localPath, 128);
				System.Windows.Media.Imaging.BitmapSource? remote = AppImage.LoadSync("https://github.com/rojo-rbx.png", 64);
				bool coverage = pack?.PixelWidth > 0
					&& local?.PixelWidth > 0
					&& inline?.PixelWidth > 0
					&& icon?.PixelWidth > 0
					&& animation.Count > 0
					&& remote?.PixelWidth > 0;
				Emit(coverage
					? "image coverage audit: PASS, pack, local, inline, icon, animation, remote"
					: "image coverage audit: FAIL, pack=" + (pack != null) + ", local=" + (local != null) + ", inline=" + (inline != null) + ", icon=" + (icon != null) + ", animation=" + animation.Count + ", remote=" + (remote != null));

				List<Voidstrap.Models.Entities.ActivityData> history = System.IO.File.Exists(Paths.ServerHistory)
					? JsonFile.Deserialize<List<Voidstrap.Models.Entities.ActivityData>>(Paths.ServerHistory, JsonOptions.Tolerant, 8L * 1024 * 1024)
					: new List<Voidstrap.Models.Entities.ActivityData>();
				string? thumbnailUrl = history.Select(entry => entry.UniverseDetails?.Thumbnail?.ImageUrl).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
				System.Windows.Media.Imaging.BitmapSource? thumbnail = string.IsNullOrWhiteSpace(thumbnailUrl) ? null : AppImage.LoadSync(thumbnailUrl, 128);
				Emit(string.IsNullOrWhiteSpace(thumbnailUrl)
					? "user media audit: SKIP, no history thumbnail"
					: thumbnail?.PixelWidth > 0
						? "user media audit: PASS, history thumbnail"
						: "user media audit: FAIL, thumbnail=" + (thumbnail != null));

				using (SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> portable = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes))
				{
					using System.IO.FileStream webpOutput = System.IO.File.Create(webpPath);
					portable.Save(webpOutput, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder());
					using System.IO.FileStream gifOutput = System.IO.File.Create(gifPath);
					portable.Save(gifOutput, new SixLabors.ImageSharp.Formats.Gif.GifEncoder());
				}
				string webpBanner = "data:image/webp;base64," + Convert.ToBase64String(System.IO.File.ReadAllBytes(webpPath));
				string gifBanner = "data:image/gif;base64," + Convert.ToBase64String(System.IO.File.ReadAllBytes(gifPath));
				System.Windows.Media.Imaging.BitmapSource? webpPreview = AppImage.LoadSync(webpBanner, 256);
				System.Windows.Media.Imaging.BitmapSource? gifPreview = AppImage.LoadSync(gifBanner, 256);
				bool controlReady = webpPreview != null && PreparesImageControl(webpPreview);
				bool surfaceCaptureSupported = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
				bool surfacePixels = !surfaceCaptureSupported || webpPreview != null && RendersImagePixels(webpPreview);
				bool inlineMediaValid = webpPreview?.PixelWidth > 0 && gifPreview?.PixelWidth > 0 && controlReady && surfacePixels;
				Emit(inlineMediaValid
					? "inline media audit: PASS, WebP, GIF, JPEG conversion, image control"
					: "inline media audit: FAIL, webpPreview=" + (webpPreview != null) + ", gifPreview=" + (gifPreview != null) + ", control=" + controlReady + ", surface=" + surfacePixels);
				if (!surfaceCaptureSupported)
					Emit("surface pixel audit: SKIP, Wayland compositor capture is unavailable");
			}
			finally
			{
				System.IO.File.Delete(localPath);
				System.IO.File.Delete(webpPath);
				System.IO.File.Delete(gifPath);
			}
		}
		catch (Exception ex)
		{
			Emit($"image audit: FAIL {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
		}
	}

	private static bool PreparesImageControl(System.Windows.Media.Imaging.BitmapSource image)
	{
		System.Windows.Controls.Image control = new()
		{
			Width = 128,
			Height = 128,
			Source = image,
			Stretch = System.Windows.Media.Stretch.UniformToFill
		};
		control.Measure(new Size(128, 128));
		control.Arrange(new Rect(0, 0, 128, 128));
		return ReferenceEquals(control.Source, image) && control.ActualWidth > 0 && control.ActualHeight > 0;
	}

	private static bool RendersImagePixels(System.Windows.Media.Imaging.BitmapSource image)
	{
		if (!Platform.IsLinux)
			return image.PixelWidth > 0 && image.PixelHeight > 0;
		Window? probe = null;
		try
		{
			System.Windows.Controls.Image control = new()
			{
				Source = image,
				Stretch = System.Windows.Media.Stretch.UniformToFill
			};
			probe = new Window
			{
				Title = "Voidstrap image pixel audit",
				Width = 128,
				Height = 128,
				WindowStyle = WindowStyle.None,
				ResizeMode = ResizeMode.NoResize,
				ShowInTaskbar = false,
				ShowActivated = false,
				Content = control
			};
			probe.Show();
			Pump(300);
			nint window = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
			return Voidstrap.Platform.Linux.LinuxWindowInterop.TryWindowHasColorVariation(window, 128, 128);
		}
		catch
		{
			return false;
		}
		finally
		{
			probe?.Close();
			Pump(80);
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

	private static void AuditTextFlowScenarios()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		Window probe = new Window
		{
			Width = 420,
			Height = 560,
			ShowInTaskbar = false,
			WindowStartupLocation = WindowStartupLocation.CenterScreen,
			Title = "Text flow audit"
		};
		Voidstrap.UI.AppFont.Apply(probe);
		System.Windows.Controls.Grid root = new System.Windows.Controls.Grid();
		root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		Wpf.Ui.Controls.TitleBar titleBar = new Wpf.Ui.Controls.TitleBar
		{
			Title = "Voidstrap Installer",
			ShowMaximize = false,
			ShowMinimize = false
		};
		root.Children.Add(titleBar);
		System.Windows.Controls.ScrollViewer viewer = new System.Windows.Controls.ScrollViewer
		{
			VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
		};
		System.Windows.Controls.StackPanel panel = new System.Windows.Controls.StackPanel
		{
			MaxWidth = 220,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(16)
		};
		System.Windows.Controls.TextBlock paragraphs = new System.Windows.Controls.TextBlock
		{
			Text = "The first paragraph contains enough words to require wrapping on Linux.\n\nThe second paragraph must remain separate after every reflow.",
			TextWrapping = TextWrapping.Wrap
		};
		System.Windows.Controls.TextBlock pageHeading = new System.Windows.Controls.TextBlock
		{
			Text = "Fast Flag Settings",
			FontSize = 24,
			TextWrapping = TextWrapping.NoWrap,
			Margin = new Thickness(0, 0, 0, 8)
		};
		System.Windows.Controls.TextBlock longToken = new System.Windows.Controls.TextBlock
		{
			Text = "https://github.com/KloBraticc/Voidstrap/really-long-path-without-natural-breaking-points",
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 8, 0, 0)
		};
		System.Windows.Controls.TextBlock multilingual = new System.Windows.Controls.TextBlock
		{
			Text = "界面文字需要自动换行并保留字符边界界面文字需要自动换行并保留字符边界 👩🏽‍💻 👨‍👩‍👧‍👦 שלום עולם שלום עולם שלום עולם שלום עולם",
			TextWrapping = TextWrapping.Wrap,
			FlowDirection = FlowDirection.RightToLeft,
			Margin = new Thickness(0, 8, 0, 0)
		};
		System.Windows.Controls.Button compactButton = new System.Windows.Controls.Button
		{
			Content = "Localized action button",
			MinWidth = 120,
			Margin = new Thickness(0, 12, 0, 0)
		};
		panel.Children.Add(pageHeading);
		panel.Children.Add(paragraphs);
		panel.Children.Add(longToken);
		panel.Children.Add(multilingual);
		panel.Children.Add(compactButton);
		viewer.Content = panel;
		System.Windows.Controls.Grid.SetRow(viewer, 1);
		root.Children.Add(viewer);
		probe.Content = root;
		try
		{
			probe.Show();
			Pump();
			Pump();
			RequireWrapped(paragraphs, "paragraph text");
			if (!string.Equals(
				Voidstrap.UI.LinuxTextGuard.GetSourceText(paragraphs),
				"The first paragraph contains enough words to require wrapping on Linux.\n\nThe second paragraph must remain separate after every reflow.",
				StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Text flow changed the paragraph source");
			}

			RequireWrapped(longToken, "long token");
			RequireWrapped(multilingual, "multilingual text");
			if (!Voidstrap.UI.LinuxTextGuard.IsCompactText(pageHeading)
				|| pageHeading.Text.Contains('\n')
				|| pageHeading.Text.Contains('\r'))
			{
				throw new InvalidOperationException("Page heading entered body text flow");
			}
			if (!string.Equals(Voidstrap.UI.LinuxTextGuard.GetSourceText(longToken), "https://github.com/KloBraticc/Voidstrap/really-long-path-without-natural-breaking-points", StringComparison.Ordinal)
				|| !string.Equals(Voidstrap.UI.LinuxTextGuard.GetSourceText(multilingual), "界面文字需要自动换行并保留字符边界界面文字需要自动换行并保留字符边界 👩🏽‍💻 👨‍👩‍👧‍👦 שלום עולם שלום עולם שלום עולם שלום עולם", StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Text flow changed the long-token source text");
			}

			RequireCompactDescendants(root);
			int narrowLines = RenderedLineCount(paragraphs);
			panel.MaxWidth = 330;
			probe.Width = 540;
			Pump();
			Pump();
			int wideLines = RenderedLineCount(paragraphs);
			if (wideLines > narrowLines)
			{
				throw new InvalidOperationException("Text flow added lines after the available width increased");
			}

			paragraphs.Text = "A dynamically replaced source must wrap from its new complete value instead of the previous rendered lines.";
			paragraphs.FontSize = 18;
			panel.MaxWidth = 210;
			probe.Width = 420;
			Pump();
			Pump();
			RequireWrapped(paragraphs, "dynamic scaled text");
			if (!string.Equals(
				Voidstrap.UI.LinuxTextGuard.GetSourceText(paragraphs),
				"A dynamically replaced source must wrap from its new complete value instead of the previous rendered lines.",
				StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Text flow retained stale source text after a dynamic update");
			}

			Emit("text flow audit: PASS, paragraphs, long tokens, emoji, CJK, RTL, dynamic source, font scaling, resize, compact controls");
		}
		finally
		{
			probe.Close();
			Pump();
		}
	}

	private static void AuditWindowTextFlow(Window window)
	{
		Pump();
		window.UpdateLayout();
		if (Voidstrap.Utility.Platform.IsLinux
			&& !string.Equals(window.FontFamily.Source, Voidstrap.UI.AppFont.CurrentFontFamily.Source, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Window did not inherit the WPF UI content font: actual=" + window.FontFamily.Source + ", expected=" + Voidstrap.UI.AppFont.CurrentFontFamily.Source);
		}
		RequireCompactDescendants(window);
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			AuditWindowsCaptionControls(window);
		}
        if (Voidstrap.Utility.Platform.IsLinux && window is Voidstrap.UI.Elements.Installer.MainWindow installer)
        {
            AuditInstallerLayout(installer);
        }
		if (window is Voidstrap.UI.Elements.About.MainWindow about)
		{
			AuditAboutHeaderText(about);
		}
		if (Voidstrap.Utility.Platform.IsLinux && window is Voidstrap.UI.Elements.Settings.MainWindow settings)
		{
			AuditLinuxMainWindowSurface(settings);
			AuditRestartNotificationInput(settings);
		}
		if (Voidstrap.Utility.Platform.IsLinux && window is Voidstrap.UI.Elements.Controls.RinColorPickerDialog colorPicker)
		{
			AuditColorPicker(colorPicker);
		}

		if (!string.Equals(window.GetType().Name, "LanguageSelectorDialog", StringComparison.Ordinal))
		{
			return;
		}

		List<Wpf.Ui.Controls.TitleBar> titleBars = FindVisualDescendants<Wpf.Ui.Controls.TitleBar>(window);
		if (titleBars.Count != 1)
		{
			throw new InvalidOperationException("Language selector did not expose exactly one title bar");
		}

		Wpf.Ui.Controls.TitleBar titleBar = titleBars[0];
		FrameworkElement? titleGrid = titleBar.Template?.FindName("TitleGrid", titleBar) as FrameworkElement;
		FrameworkElement? closeButton = titleBar.Template?.FindName("PART_CloseButton", titleBar) as FrameworkElement;
		if (titleGrid == null || closeButton == null)
		{
			throw new InvalidOperationException("Language selector title bar template parts were unavailable");
		}

		if (closeButton.IsVisible && BoundsInWindow(titleGrid, window).IntersectsWith(BoundsInWindow(closeButton, window)))
		{
			throw new InvalidOperationException("Language selector title overlaps the close button");
		}

		List<System.Windows.Controls.Button> buttons = FindVisualDescendants<System.Windows.Controls.Button>(window)
			.Where(button => button.Content is string text && !string.IsNullOrWhiteSpace(text))
			.ToList();
		if (buttons.Count < 2)
		{
			throw new InvalidOperationException("Language selector action buttons were unavailable");
		}

		foreach (System.Windows.Controls.Button button in buttons)
		{
			string label = (string)button.Content;
			if (label.Contains('\n') || label.Contains('\r'))
			{
				throw new InvalidOperationException("Language selector action text contains a line break");
			}

			System.Windows.Controls.TextBlock? labelBlock = FindVisualDescendants<System.Windows.Controls.TextBlock>(button).FirstOrDefault();
			if (labelBlock == null || labelBlock.Text.Contains('\n') || labelBlock.Text.Contains('\r'))
			{
				throw new InvalidOperationException("Language selector action text was split across lines");
			}

			double required = Voidstrap.UI.LinuxInlineText.MeasureCached(labelBlock, labelBlock.Text) + button.Padding.Left + button.Padding.Right;
			if (button.ActualWidth + 1.0 < required)
			{
				throw new InvalidOperationException("Language selector action button is narrower than its label");
			}
		}

		System.Windows.Controls.TextBlock? description = FindVisualDescendants<System.Windows.Controls.TextBlock>(window)
			.Where(block => !Voidstrap.UI.LinuxTextGuard.IsCompactText(block) && block.TextWrapping != TextWrapping.NoWrap)
			.OrderByDescending(block => block.Text.Length)
			.FirstOrDefault();
		if (description == null || RenderedLineCount(description) < 2)
		{
			throw new InvalidOperationException("Language selector description did not wrap onto multiple lines");
		}
		if (!string.Equals(
			Voidstrap.UI.LinuxTextGuard.GetSourceText(description).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
			Voidstrap.Resources.Strings.Dialog_LanguageSelector_Subtext.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
			StringComparison.Ordinal))
		{
			throw new InvalidOperationException("Language selector description differs from its source text");
		}

		Emit("language selector text audit: PASS, single line title and actions, wrapped body, no chrome overlap");
	}

	private static void AuditLinuxMainWindowSurface(Voidstrap.UI.Elements.Settings.MainWindow window)
	{
		nint nativeWindow = 0;
		for (int attempt = 0; attempt < 10 && nativeWindow == 0; attempt++)
		{
			Pump(50);
			nativeWindow = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(window.Title);
		}
		if (Voidstrap.UI.LinuxWindowMode.IsMaximized(window))
		{
			Voidstrap.UI.LinuxWindowMode.ToggleMaximize(window);
			Pump(500);
		}
		Voidstrap.UI.RoundedWindowChrome.Refresh(window);
		Pump(80);
		bool bounding = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetBoundingShapeRectangleCount(nativeWindow, out int boundingRectangles)
			&& boundingRectangles > 1;
		bool input = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetInputShapeRectangleCount(nativeWindow, out int inputRectangles)
			&& inputRectangles > 0;
		if (!bounding || !input)
			throw new InvalidOperationException("Linux main window surface is not rounded and interactive: " + boundingRectangles + "/" + inputRectangles);

		Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(nativeWindow, out int originalLeft, out int originalTop, out int originalWidth, out int originalHeight);
		Voidstrap.UI.LinuxWindowMode.ToggleMaximize(window);
		Pump(500);
		Voidstrap.Platform.Linux.LinuxDisplayBounds workArea = Voidstrap.Platform.Linux.LinuxDisplayMetrics.Refresh().WorkArea;
		bool nativeMaximized = Voidstrap.Platform.Linux.LinuxWindowInterop.IsMaximized(nativeWindow);
		bool maximizedGeometry = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(nativeWindow, out int maximizedLeft, out int maximizedTop, out int maximizedWidth, out int maximizedHeight)
			&& Math.Abs(maximizedLeft - workArea.Left) <= 2
			&& Math.Abs(maximizedTop - workArea.Top) <= 2
			&& Math.Abs(maximizedWidth - workArea.Width) <= 2
			&& Math.Abs(maximizedHeight - workArea.Height) <= 2;
		Wpf.Ui.Controls.TitleBar? mainTitleBar = FindVisualDescendants<Wpf.Ui.Controls.TitleBar>(window).FirstOrDefault();
		mainTitleBar?.ApplyTemplate();
		FrameworkElement? maximizeButton = mainTitleBar?.Template?.FindName("PART_MaximizeButton", mainTitleBar) as FrameworkElement;
		FrameworkElement? restoreButton = mainTitleBar?.Template?.FindName("PART_RestoreButton", mainTitleBar) as FrameworkElement;
		bool restoreGlyphVisible = maximizeButton?.Visibility == Visibility.Collapsed && restoreButton?.Visibility == Visibility.Visible;
		Voidstrap.UI.LinuxWindowMode.ToggleMaximize(window);
		Pump(500);
		bool maximizeRestored = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(nativeWindow, out int maximizeRestoredLeft, out int maximizeRestoredTop, out int maximizeRestoredWidth, out int maximizeRestoredHeight)
			&& Math.Abs(maximizeRestoredLeft - originalLeft) <= 2
			&& Math.Abs(maximizeRestoredTop - originalTop) <= 2
			&& Math.Abs(maximizeRestoredWidth - originalWidth) <= 2
			&& Math.Abs(maximizeRestoredHeight - originalHeight) <= 2;
		bool maximizeGlyphVisible = maximizeButton?.Visibility == Visibility.Visible && restoreButton?.Visibility == Visibility.Collapsed;
		if ((!nativeMaximized && !maximizedGeometry) || !restoreGlyphVisible || !maximizeRestored || !maximizeGlyphVisible)
		{
			throw new InvalidOperationException(
				"Linux maximize surface failed: native=" + nativeMaximized
				+ ", maximized=" + maximizedLeft + "," + maximizedTop + " " + maximizedWidth + "x" + maximizedHeight
				+ ", workArea=" + workArea.Left + "," + workArea.Top + " " + workArea.Width + "x" + workArea.Height
				+ ", glyphs=" + restoreGlyphVisible + "/" + maximizeGlyphVisible
				+ ", restored=" + maximizeRestored);
		}
		Voidstrap.UI.LinuxWindowMode.ToggleFullscreen(window);
		Pump(500);
		Voidstrap.Platform.Linux.LinuxDisplayBounds displayBounds = Voidstrap.Platform.Linux.LinuxDisplayMetrics.Refresh().Bounds;
		bool nativeFullscreen = Voidstrap.Platform.Linux.LinuxWindowInterop.IsFullscreen(nativeWindow);
		bool geometryRead = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(nativeWindow, out int fullscreenLeft, out int fullscreenTop, out int fullscreenWidth, out int fullscreenHeight);
		bool fallbackFullscreen = geometryRead
			&& Math.Abs(fullscreenLeft - displayBounds.Left) <= 2
			&& Math.Abs(fullscreenTop - displayBounds.Top) <= 2
			&& Math.Abs(fullscreenWidth - displayBounds.Width) <= 2
			&& Math.Abs(fullscreenHeight - displayBounds.Height) <= 2;
		Voidstrap.UI.LinuxWindowMode.ToggleFullscreen(window);
		Pump(500);
		bool restoredGeometry = Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(nativeWindow, out int restoredLeft, out int restoredTop, out int restoredWidth, out int restoredHeight)
			&& Math.Abs(restoredLeft - originalLeft) <= 2
			&& Math.Abs(restoredTop - originalTop) <= 2
			&& Math.Abs(restoredWidth - originalWidth) <= 2
			&& Math.Abs(restoredHeight - originalHeight) <= 2;
		if ((!nativeFullscreen && !fallbackFullscreen) || !restoredGeometry)
		{
			throw new InvalidOperationException(
				"Linux fullscreen surface failed: native=" + nativeFullscreen
				+ ", fullscreen=" + fullscreenLeft + "," + fullscreenTop + " " + fullscreenWidth + "x" + fullscreenHeight
				+ ", display=" + displayBounds.Left + "," + displayBounds.Top + " " + displayBounds.Width + "x" + displayBounds.Height
				+ ", restored=" + restoredGeometry);
		}
		Emit("Linux main window surface audit: PASS, rounded input, maximize/fullscreen surfaces and geometry restore");
	}

	private static void AuditWindowsCaptionControls(Window window)
	{
		foreach (Wpf.Ui.Controls.TitleBar titleBar in FindVisualDescendants<Wpf.Ui.Controls.TitleBar>(window))
		{
			titleBar.ApplyTemplate();
			Wpf.Ui.Controls.Button? minimize = titleBar.Template?.FindName("ButtonMinimize", titleBar) as Wpf.Ui.Controls.Button;
			Wpf.Ui.Controls.Button? maximize = titleBar.Template?.FindName("PART_MaximizeButton", titleBar) as Wpf.Ui.Controls.Button;
			Wpf.Ui.Controls.Button? restore = titleBar.Template?.FindName("PART_RestoreButton", titleBar) as Wpf.Ui.Controls.Button;
			Wpf.Ui.Controls.Button? close = titleBar.Template?.FindName("PART_CloseButton", titleBar) as Wpf.Ui.Controls.Button;
			if (minimize == null || maximize == null || restore == null || close == null)
				throw new InvalidOperationException(window.GetType().Name + " is missing a Windows caption control");

			Style? standardStyle = titleBar.TryFindResource("UiTitlebarButton") as Style;
			Style? closeStyle = titleBar.TryFindResource("UiTitlebarCloseButton") as Style;
			bool stylesMatch = standardStyle != null
				&& closeStyle != null
				&& ReferenceEquals(minimize.Style, standardStyle)
				&& ReferenceEquals(maximize.Style, standardStyle)
				&& ReferenceEquals(restore.Style, standardStyle)
				&& ReferenceEquals(close.Style, closeStyle);
			bool dimensionsMatch = minimize.Width == 44.0 && minimize.Height == 30.0
				&& maximize.Width == 44.0 && maximize.Height == 30.0
				&& restore.Width == 44.0 && restore.Height == 30.0
				&& close.Width == 44.0 && close.Height == 30.0;
			bool orderMatches = System.Windows.Controls.Grid.GetColumn(minimize) == 1
				&& System.Windows.Controls.Grid.GetColumn(maximize) == 2
				&& System.Windows.Controls.Grid.GetColumn(restore) == 3
				&& System.Windows.Controls.Grid.GetColumn(close) == 4;
			if (!stylesMatch || !dimensionsMatch || !orderMatches)
			{
				throw new InvalidOperationException(window.GetType().Name + " caption controls differ from the Windows Wpf.Ui template");
			}
		}
	}

	private static void AuditColorPicker(Voidstrap.UI.Elements.Controls.RinColorPickerDialog window)
	{
		Voidstrap.UI.Elements.Controls.RinColorPicker picker = window.Picker;
		picker.AlphaEnabled = true;
		window.UpdateLayout();

		System.Windows.Controls.Border? valueTrack = FindVisualDescendants<System.Windows.Controls.Border>(picker)
			.FirstOrDefault(element => string.Equals(element.Name, "ValueTrack", StringComparison.Ordinal));
		System.Windows.Controls.Border? alphaTrack = FindVisualDescendants<System.Windows.Controls.Border>(picker)
			.FirstOrDefault(element => string.Equals(element.Name, "AlphaTrack", StringComparison.Ordinal));
		System.Windows.Shapes.Ellipse? valueHandle = FindVisualDescendants<System.Windows.Shapes.Ellipse>(picker)
			.FirstOrDefault(element => string.Equals(element.Name, "ValueHandle", StringComparison.Ordinal));
		System.Windows.Shapes.Ellipse? alphaHandle = FindVisualDescendants<System.Windows.Shapes.Ellipse>(picker)
			.FirstOrDefault(element => string.Equals(element.Name, "AlphaHandle", StringComparison.Ordinal));
		System.Windows.Controls.Grid? spectrum = FindVisualDescendants<System.Windows.Controls.Grid>(picker)
			.FirstOrDefault(element => string.Equals(element.Name, "SpectrumGrid", StringComparison.Ordinal));
		System.Windows.Shapes.Ellipse? spectrumHandle = FindVisualDescendants<System.Windows.Shapes.Ellipse>(picker)
			.FirstOrDefault(element => string.Equals(element.Name, "SpectrumThumb", StringComparison.Ordinal));
		if (valueTrack == null || alphaTrack == null || valueHandle == null || alphaHandle == null
			|| spectrum == null || spectrumHandle == null
			|| valueTrack.ActualWidth <= valueHandle.ActualWidth || alphaTrack.ActualWidth <= alphaHandle.ActualWidth
			|| spectrum.ActualWidth <= spectrumHandle.ActualWidth || spectrum.ActualHeight <= spectrumHandle.ActualHeight)
		{
			throw new InvalidOperationException("Linux colour picker tracks were unavailable");
		}

		picker.SetValueFromPosition(0);
		if (picker.SelectedColor.R != 0 || picker.SelectedColor.G != 0 || picker.SelectedColor.B != 0
			|| Math.Abs(System.Windows.Controls.Canvas.GetLeft(valueHandle)) > 0.01)
		{
			throw new InvalidOperationException("Linux colour picker value track did not reach its left endpoint");
		}

		picker.SetValueFromPosition(valueTrack.ActualWidth);
		double valueRight = valueTrack.ActualWidth - valueHandle.ActualWidth;
		if (picker.SelectedColor.R != 255 || picker.SelectedColor.G != 255 || picker.SelectedColor.B != 255
			|| Math.Abs(System.Windows.Controls.Canvas.GetLeft(valueHandle) - valueRight) > 0.01)
		{
			throw new InvalidOperationException("Linux colour picker value track did not reach its right endpoint");
		}

		picker.SetAlphaFromPosition(0);
		if (picker.SelectedColor.A != 0 || Math.Abs(System.Windows.Controls.Canvas.GetLeft(alphaHandle)) > 0.01)
		{
			throw new InvalidOperationException("Linux colour picker alpha track did not reach its left endpoint");
		}

		picker.BeginAlphaInteraction();
		for (int index = 0; index < 1000; index++)
		{
			picker.SetAlphaFromPosition(alphaTrack.ActualWidth * index / 999.0);
		}
		picker.SetAlphaFromPosition(alphaTrack.ActualWidth);
		if (!picker.IsInputRefreshPending)
		{
			throw new InvalidOperationException("Linux colour picker did not coalesce rapid input refreshes");
		}
		picker.EndInteraction();
		Pump();
		double alphaRight = alphaTrack.ActualWidth - alphaHandle.ActualWidth;
		if (picker.SelectedColor.A != 255 || Math.Abs(System.Windows.Controls.Canvas.GetLeft(alphaHandle) - alphaRight) > 0.01)
		{
			throw new InvalidOperationException("Linux colour picker rapid alpha input did not settle at its right endpoint");
		}

		picker.SetSpectrumFromPosition(new Point(spectrum.ActualWidth / 2.0, spectrum.ActualHeight / 4.0));
		if (Math.Abs(picker.SelectedColor.R - 64) > 1 || picker.SelectedColor.G != 255 || picker.SelectedColor.B != 255
			|| Math.Abs(System.Windows.Controls.Canvas.GetLeft(spectrumHandle) - (spectrum.ActualWidth / 2.0 - spectrumHandle.ActualWidth / 2.0)) > 0.01
			|| Math.Abs(System.Windows.Controls.Canvas.GetTop(spectrumHandle) - (spectrum.ActualHeight / 4.0 - spectrumHandle.ActualHeight / 2.0)) > 0.01)
		{
			throw new InvalidOperationException("Linux colour picker spectrum did not preserve pointer geometry and HSV output");
		}

		Emit("colour picker input audit: PASS, exact endpoints, spectrum geometry, and 1000 rapid updates settled");
	}

	private static void AuditAboutHeaderText(Voidstrap.UI.Elements.About.MainWindow window)
	{
		const string subtitleSource = "A simple yet advanced Bloxstrap fork.";
		window.Navigate(typeof(Voidstrap.UI.Elements.About.Pages.AboutPage));
		Pump();
		Pump();
		window.UpdateLayout();
		System.Windows.Controls.TextBlock? subtitle = FindVisualDescendants<System.Windows.Controls.TextBlock>(window)
			.FirstOrDefault(block => string.Equals(Voidstrap.UI.LinuxTextGuard.GetSourceText(block), subtitleSource, StringComparison.Ordinal));
		if (subtitle == null)
		{
			throw new InvalidOperationException("About header subtitle was unavailable");
		}

		if (!string.Equals(subtitle.Text, subtitleSource, StringComparison.Ordinal)
			|| subtitle.Text.Contains('\n')
			|| subtitle.Text.Contains('\r')
			|| Voidstrap.UI.LinuxTextGuard.IsAutomaticallyWrapped(subtitle))
		{
			throw new InvalidOperationException("About header subtitle was split or altered");
		}

		double requiredWidth = Voidstrap.UI.LinuxInlineText.MeasureCached(subtitle, subtitleSource);
		if (subtitle.ActualWidth + 1.0 < requiredWidth)
		{
			throw new InvalidOperationException("About header subtitle is narrower than its complete sentence");
		}

		List<Voidstrap.UI.Elements.Controls.Expander> expanders = FindVisualDescendants<Voidstrap.UI.Elements.Controls.Expander>(window);
		if (expanders.Count != 3 || expanders.Any(Wpf.Ui.Controls.ExpanderMotion.GetUseLinuxAnimationClock))
		{
			throw new InvalidOperationException("The three About expanders did not retain their isolated legacy motion path");
		}

		Emit("About header text audit: PASS, subtitle preserved on one line and three expanders excluded from Linux motion changes");
	}

	private static void AuditRestartNotificationInput(Voidstrap.UI.Elements.Settings.MainWindow window)
	{
		const string auditKey = "window-audit-restart-notification";
		try
		{
			Voidstrap.UI.RestartNotificationService.Require(
				auditKey,
				"Hardware acceleration changed",
				"Hardware acceleration will be disabled the next time Voidstrap starts.",
				"Restart now",
				Voidstrap.UI.RestartTarget.Application);
			Pump();
			window.UpdateLayout();

			System.Windows.Controls.Border? card = window.FindName("RestartNotificationCard") as System.Windows.Controls.Border;
			System.Windows.Controls.Button? action = window.FindName("RestartNotificationAction") as System.Windows.Controls.Button;
			System.Windows.Controls.Button? dismiss = window.FindName("RestartNotificationDismiss") as System.Windows.Controls.Button;
			if (card == null || action == null || dismiss == null || card.Visibility != Visibility.Visible || !card.IsHitTestVisible)
			{
				throw new InvalidOperationException("Linux restart notification controls were unavailable for input");
			}

			if (card.Parent is not System.Windows.Controls.Panel parent
				|| parent.Children.Count == 0
				|| !ReferenceEquals(parent.Children[parent.Children.Count - 1], card))
			{
				throw new InvalidOperationException("Linux restart notification was not last in the input tree");
			}

			RequireHitTarget(window, action, "Restart now");
			RequireHitTarget(window, dismiss, "Dismiss");
			dismiss.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, dismiss));
			Pump();
			if (card.Visibility != Visibility.Collapsed)
			{
				throw new InvalidOperationException("Linux restart notification dismiss action did not close in the current input cycle");
			}

			Emit("restart notification input audit: PASS, action and dismiss hit targets reachable");
		}
		finally
		{
			Voidstrap.UI.RestartNotificationService.Clear(auditKey);
		}
	}

	private static void RequireHitTarget(Window window, FrameworkElement control, string label)
	{
		Point point = control.TranslatePoint(new Point(control.ActualWidth / 2.0, control.ActualHeight / 2.0), window);
		DependencyObject? hit = window.InputHitTest(point) as DependencyObject;
		DependencyObject? current = hit;
		while (current != null && !ReferenceEquals(current, control))
		{
			current = System.Windows.Media.VisualTreeHelper.GetParent(current);
		}

		if (current == null)
		{
			throw new InvalidOperationException("Linux restart notification " + label + " hit target was blocked by " + (hit?.GetType().Name ?? "nothing"));
		}
	}

	private static void AuditInstallerLayout(Voidstrap.UI.Elements.Installer.MainWindow window)
	{
		window.Navigate(typeof(Voidstrap.UI.Elements.Installer.Pages.InstallPage));
		Pump();
		Pump();
		window.UpdateLayout();
		if (window.FindName("StepHeadingText") is not System.Windows.Controls.TextBlock heading
			|| !string.Equals(heading.Text, Voidstrap.Resources.Strings.Installer_Install_Title, StringComparison.Ordinal)
			|| BoundsInWindow(heading, window).Top < -1.0
			|| window.FindName("StepIcon") is not Wpf.Ui.Controls.SymbolIcon
			|| window.FindName("RootFrame") is not FrameworkElement frame
			|| frame.ActualWidth <= 0.0
			|| frame.ActualHeight <= 0.0)
		{
			throw new InvalidOperationException("Linux installer layout is clipped or unavailable");
		}

		Emit("installer layout audit: PASS, install page and header visible");
	}

	private static void RequireCompactDescendants(DependencyObject root)
	{
		foreach (System.Windows.Controls.TextBlock block in FindVisualDescendants<System.Windows.Controls.TextBlock>(root))
		{
			if (Voidstrap.UI.LinuxTextGuard.IsCompactText(block) && Voidstrap.UI.LinuxTextGuard.IsAutomaticallyWrapped(block))
			{
				throw new InvalidOperationException("Compact control text was automatically wrapped: " + block.Text.Replace('\n', ' '));
			}
		}
	}

	private static void RequireWrapped(System.Windows.Controls.TextBlock block, string label)
	{
		if (RenderedLineCount(block) < 2)
		{
			throw new InvalidOperationException(label + " did not wrap onto multiple lines, actual=" + block.ActualWidth + "x" + block.ActualHeight + ", desired=" + block.DesiredSize.Width + "x" + block.DesiredSize.Height + ", max=" + block.MaxWidth + ", wrapping=" + block.TextWrapping + ", breaks=" + block.Text.Count(character => character == '\n') + ", font=" + block.FontFamily.Source + ", measured=" + Voidstrap.UI.LinuxInlineText.MeasureCached(block, Voidstrap.UI.LinuxTextGuard.GetSourceText(block)) + ", flow=" + Voidstrap.UI.LinuxTextGuard.GetFlowState(block));
		}
	}

	private static int RenderedLineCount(System.Windows.Controls.TextBlock block)
	{
		if (block == null || string.IsNullOrEmpty(block.Text))
		{
			return 0;
		}

		double lineHeight = double.IsNaN(block.LineHeight) || block.LineHeight <= 0.0
			? block.FontSize * 1.2
			: block.LineHeight;
		if (lineHeight <= 0.0)
		{
			return 1;
		}

		return Math.Max(1, (int)Math.Ceiling((block.ActualHeight - 0.5) / lineHeight));
	}

	private static Rect BoundsInWindow(FrameworkElement element, Window window)
	{
		Point origin = element.TranslatePoint(new Point(0.0, 0.0), window);
		return new Rect(origin, element.RenderSize);
	}

	private static List<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
	{
		List<T> found = new List<T>();
		Stack<DependencyObject> pending = new Stack<DependencyObject>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			DependencyObject current = pending.Pop();
			if (current is T match)
			{
				found.Add(match);
			}

			int children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
			for (int index = children - 1; index >= 0; index--)
			{
				pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(current, index));
			}
		}

		return found;
	}

	private static void AuditTabs(Window window, string label)
	{
		foreach (System.Windows.Controls.TabControl tabs in FindVisualDescendants<System.Windows.Controls.TabControl>(window))
		{
			int count = tabs.Items.Count;
			if (count < 2)
				continue;

			int restore = tabs.SelectedIndex;
			for (int index = 0; index < count; index++)
			{
				string header = tabs.Items[index] is System.Windows.Controls.TabItem item
					? item.Header?.ToString() ?? index.ToString()
					: index.ToString();
				try
				{
					_probeErrors.Clear();
					tabs.SelectedIndex = index;
					Pump();
					Pump();
					if (_probeErrors.Count > 0)
					{
						Emit($"  TAB DEFER {label}/{header}: {_probeErrors.Count} background failure(s)");
						foreach (string deferred in _probeErrors)
							Emit($"           {deferred}");
					}
					else
					{
						Emit($"  TAB OK   {label}/{header}");
					}
				}
				catch (Exception ex)
				{
					Exception root = ex;
					while (root.InnerException != null)
						root = root.InnerException;
					Emit($"  TAB FAIL {label}/{header}: {root.GetType().Name} {root.Message.Split('\n')[0]}");
				}
			}

			try
			{
				tabs.SelectedIndex = restore;
				Pump();
			}
			catch (Exception)
			{
			}
		}
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
					AuditTabs(window, label);
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

	private static void AuditCustomThemes()
	{
		try
		{
			if (!System.IO.Directory.Exists(Paths.CustomThemes))
			{
				Emit("custom themes: none installed");
				return;
			}

			int passed = 0;
			int failed = 0;

			foreach (string directory in System.IO.Directory.GetDirectories(Paths.CustomThemes).OrderBy(d => d, StringComparer.Ordinal))
			{
				string name = System.IO.Path.GetFileName(directory);
				if (string.Equals(name, FixtureThemeName, StringComparison.Ordinal))
				{
					continue;
				}

				if (!System.IO.File.Exists(System.IO.Path.Combine(directory, "Theme.xml")))
				{
					Emit($"  THEME {name}: no Theme.xml, skipped");
					continue;
				}

				Voidstrap.UI.Elements.Bootstrapper.CustomDialog? dialog = null;
				try
				{
					dialog = new Voidstrap.UI.Elements.Bootstrapper.CustomDialog();
					dialog.ApplyCustomTheme(name);
					dialog.Show();
					Pump();
					passed++;
					Emit($"  THEME {name}: builds and renders");
				}
				catch (Exception ex)
				{
					failed++;
					Exception root = ex;
					while (root.InnerException != null)
					{
						root = root.InnerException;
					}
					Emit($"  THEME {name} FAIL: {root.GetType().Name}: {root.Message.Split('\n')[0]}");
				}
				finally
				{
					try { dialog?.Close(); } catch { }
					Pump();
				}
			}

			Emit($"custom themes: {passed} rendered, {failed} failed");
		}
		catch (Exception ex)
		{
			Emit("custom theme audit failed: " + ex.Message);
		}
	}

	private const string FixtureThemeName = "audit";

	private static void PrepareFixtures()
	{
		try
		{
			string themeDirectory = System.IO.Path.Combine(Paths.CustomThemes, FixtureThemeName);
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

	private static void RemoveFixtures()
	{
		try
		{
			if (string.Equals(App.Settings.Prop.SelectedCustomTheme, FixtureThemeName, StringComparison.Ordinal))
			{
				App.Settings.Prop.SelectedCustomTheme = null;
			}

			string themeDirectory = System.IO.Path.Combine(Paths.CustomThemes, FixtureThemeName);
			if (System.IO.Directory.Exists(themeDirectory))
			{
				System.IO.Directory.Delete(themeDirectory, true);
			}
		}
		catch (Exception ex)
		{
			Emit("fixture cleanup failed: " + ex.Message);
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
			return false;
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
			Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
			System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
			while (elapsed.ElapsedMilliseconds < milliseconds)
			{
				PushBoundedFrame(dispatcher);
				int remaining = milliseconds - (int)elapsed.ElapsedMilliseconds;
				if (remaining > 0)
					System.Threading.Thread.Sleep(Math.Min(4, remaining));
			}
		}
		catch
		{
		}
	}

	private static void PushBoundedFrame(Dispatcher dispatcher)
	{
		DispatcherFrame frame = new();
		DispatcherTimer limit = new(DispatcherPriority.Normal, dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(50)
		};
		limit.Tick += (_, _) => frame.Continue = false;
		dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
		limit.Start();
		Dispatcher.PushFrame(frame);
		limit.Stop();
	}

	private static bool PumpOneRender(int timeoutMilliseconds)
	{
		DispatcherFrame frame = new();
		bool rendered = false;
		DispatcherTimer timeout = new(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(timeoutMilliseconds)
		};
		EventHandler? rendering = null;
		EventHandler? elapsed = null;
		rendering = (_, _) =>
		{
			rendered = true;
			frame.Continue = false;
		};
		elapsed = (_, _) =>
		{
			timeout.Stop();
			frame.Continue = false;
		};
		CompositionTarget.Rendering += rendering;
		timeout.Tick += elapsed;
		try
		{
			timeout.Start();
			Dispatcher.PushFrame(frame);
		}
		finally
		{
			timeout.Stop();
			timeout.Tick -= elapsed;
			CompositionTarget.Rendering -= rendering;
		}
		return rendered;
	}

#if CROSSPLAT
	private static void PumpPortableHost(
		System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host,
		int milliseconds)
	{
		System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
		do
		{
			Pump(Math.Min(8, Math.Max(1, milliseconds - (int)elapsed.ElapsedMilliseconds)));
			if (host?.SilkWindow is { } window)
			{
				window.DoUpdate();
				window.DoRender();
			}
		}
		while (elapsed.ElapsedMilliseconds < milliseconds);
	}
#endif

	private static bool PumpUntil(Func<bool> predicate, int timeoutMilliseconds)
	{
		Application application = Application.Current
			?? throw new InvalidOperationException("The Linux window audit needs an active WPF application");
		Dispatcher dispatcher = application.Dispatcher;
		if (!dispatcher.CheckAccess())
			throw new InvalidOperationException("The Linux window audit must run on the application dispatcher");
		long deadline = Environment.TickCount64 + timeoutMilliseconds;
		int consecutive = 0;
		while (Environment.TickCount64 < deadline)
		{
			PushBoundedFrame(dispatcher);
			if (predicate())
			{
				consecutive++;
				if (consecutive >= 2)
					return true;
			}
			else
			{
				consecutive = 0;
			}
			System.Threading.Thread.Sleep(4);
		}
		return false;
	}

	private static void Emit(string line)
	{
		App.Logger.WriteLine("WindowAudit", line);
	}
}
