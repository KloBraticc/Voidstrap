using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Voidstrap.Integrations.ClassicTopBar;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.ClassicTopBar;

internal sealed class ClassicImageButton : Grid
{
	private readonly Rectangle _highlight;

	private readonly System.Windows.Controls.Image _picture;

	private readonly Action _click;

	public ClassicImageButton(string name, double width, double height, Action click)
	{
		_click = click;
		Width = width;
		Height = height;
		Cursor = Cursors.Hand;
		Background = Brushes.Transparent;
		BitmapImage? source = ClassicTopBarOverlay.Image(name);
		_picture = new System.Windows.Controls.Image
		{
			Source = source,
			Stretch = Stretch.Fill
		};
		RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
		_highlight = new Rectangle
		{
			Fill = Brushes.White,
			Opacity = 0,
			IsHitTestVisible = false
		};
		if (source != null)
		{
			_highlight.OpacityMask = new ImageBrush(source);
		}
		Children.Add(_picture);
		Children.Add(_highlight);
		MouseEnter += OnEnter;
		MouseLeave += OnLeave;
		MouseLeftButtonDown += OnPressed;
	}

	public void SetImage(string name)
	{
		BitmapImage? source = ClassicTopBarOverlay.Image(name);
		_picture.Source = source;
		_highlight.OpacityMask = source == null ? null : new ImageBrush(source);
	}

	public void Detach()
	{
		MouseEnter -= OnEnter;
		MouseLeave -= OnLeave;
		MouseLeftButtonDown -= OnPressed;
	}

	private void OnEnter(object sender, MouseEventArgs e) => _highlight.Opacity = 0.22;

	private void OnLeave(object sender, MouseEventArgs e) => _highlight.Opacity = 0;

	private void OnPressed(object sender, MouseButtonEventArgs e) => _click();
}

internal sealed class ClassicTopBarWindow : Window
{
	private const double BarHeight = 36;

	private readonly RobloxOverlayAnchor _anchor;

	private readonly Grid _content;

	private readonly ScaleTransform _scale;

	private readonly ClassicImageButton _menuButton;

	private readonly ClassicImageButton _chatButton;

	private readonly ClassicImageButton _backpackButton;

	private readonly TextBlock _userName;

	private readonly Border _recording;

	public bool IsClosing { get; private set; }

	public ClassicTopBarWindow()
	{
		Title = "Voidstrap Classic TopBar";
		LinuxOverlaySurface.ReleaseMainWindowClaim(this);
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		Topmost = true;
		ShowInTaskbar = false;
		ShowActivated = false;
		ResizeMode = ResizeMode.NoResize;
		SnapsToDevicePixels = true;
		UseLayoutRounding = true;
		Width = 640;
		Height = BarHeight;

		_scale = new ScaleTransform(1, 1);
		Canvas canvas = new Canvas
		{
			Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
			Height = BarHeight
		};

		_menuButton = new ClassicImageButton("Burger.png", 32, 25, ClassicTopBarOverlay.ToggleMenu);
		Canvas.SetLeft(_menuButton, 16);
		Canvas.SetTop(_menuButton, 5);
		canvas.Children.Add(_menuButton);

		_chatButton = new ClassicImageButton("Chat.png", 28, 26, ToggleChat);
		Canvas.SetLeft(_chatButton, 68);
		Canvas.SetTop(_chatButton, 5);
		canvas.Children.Add(_chatButton);

		_backpackButton = new ClassicImageButton("Backpack.png", 36, 36, ToggleBackpack);
		Canvas.SetLeft(_backpackButton, 116);
		Canvas.SetTop(_backpackButton, 2);
		canvas.Children.Add(_backpackButton);

		_userName = new TextBlock
		{
			Text = ClassicTopBarOverlay.UserName,
			FontFamily = new System.Windows.Media.FontFamily("Source Sans Pro, Segoe UI"),
			FontSize = 12,
			Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
			VerticalAlignment = VerticalAlignment.Center
		};

		_recording = new Border
		{
			Background = new SolidColorBrush(Color.FromRgb(170, 34, 34)),
			CornerRadius = new CornerRadius(4),
			Padding = new Thickness(8, 2, 8, 2),
			Margin = new Thickness(0, 0, 12, 0),
			VerticalAlignment = VerticalAlignment.Center,
			Cursor = Cursors.Hand,
			Visibility = Visibility.Collapsed,
			Child = new TextBlock
			{
				Text = "Recording",
				Foreground = Brushes.White,
				FontWeight = FontWeights.Bold,
				FontSize = 12
			}
		};
		_recording.MouseLeftButtonDown += OnRecordingClicked;

		StackPanel right = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness(0, 0, 70, 0)
		};
		right.Children.Add(_recording);
		right.Children.Add(_userName);

		_content = new Grid
		{
			Height = BarHeight,
			LayoutTransform = _scale
		};
		_content.Children.Add(canvas);
		_content.Children.Add(right);
		Content = _content;

		SizeChanged += OnSizeChanged;
		SourceInitialized += OnSourceInitialized;
		Closed += OnClosed;

		_anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.TopStrip);
		RefreshLayout();
	}

	private void OnSourceInitialized(object? sender, EventArgs e)
	{
		IntPtr handle = new WindowInteropHelper(this).Handle;
		ClassicTopBarNative.MakeNonActivating(handle);
		RefreshLayout();
	}

	private void OnSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Canvas canvas)
		{
			canvas.Width = Math.Max(0, ActualWidth / Math.Max(0.01, _scale.ScaleX));
		}
	}

	public void RefreshLayout()
	{
		double factor = ClassicTopBarOverlay.Scale / Math.Max(0.01, VisualTreeHelper.GetDpi(this).DpiScaleY);
		_scale.ScaleX = factor;
		_scale.ScaleY = factor;
		Height = BarHeight * factor;
		_userName.Text = ClassicTopBarOverlay.UserName;
		ApplyVisibility();
		RefreshIcons();
		_anchor.Refresh();
	}

	public void ApplyVisibility()
	{
		Visibility = ClassicTopBarOverlay.TopBarVisible ? Visibility.Visible : Visibility.Hidden;
	}

	public void RefreshIcons()
	{
		_menuButton.SetImage(ClassicTopBarOverlay.MenuVisible ? "BurgerOpen.png" : "Burger.png");
		_chatButton.SetImage(ClassicTopBarOverlay.ChatOpen ? "ChatOpen.png" : "Chat.png");
		_backpackButton.SetImage(ClassicTopBarOverlay.BackpackOpen ? "BackpackOpen.png" : "Backpack.png");
	}

	public void ShowRecording(bool visible)
	{
		_recording.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
	}

	private void ToggleChat()
	{
		ClassicTopBarInput.FocusRoblox();
		if (ClassicTopBarOverlay.ChatOpen)
		{
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkReturn);
			ClassicTopBarOverlay.SetChatOpen(false);
			return;
		}
		ClassicTopBarInput.Tap(ClassicTopBarInput.VkOemSlash);
		ClassicTopBarOverlay.SetChatOpen(true);
	}

	private void ToggleBackpack()
	{
		ClassicTopBarInput.FocusRoblox();
		ClassicTopBarInput.Tap(ClassicTopBarInput.VkOemTilde);
		ClassicTopBarOverlay.SetBackpackOpen(!ClassicTopBarOverlay.BackpackOpen);
	}

	private void OnRecordingClicked(object sender, MouseButtonEventArgs e)
	{
		ClassicTopBarInput.FocusRoblox();
		ClassicTopBarInput.Tap(ClassicTopBarInput.VkF12);
		ShowRecording(false);
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		IsClosing = true;
		SizeChanged -= OnSizeChanged;
		SourceInitialized -= OnSourceInitialized;
		Closed -= OnClosed;
		_recording.MouseLeftButtonDown -= OnRecordingClicked;
		_menuButton.Detach();
		_chatButton.Detach();
		_backpackButton.Detach();
		_anchor.Dispose();
		ClassicTopBarOverlay.ReleaseTopBar(this);
	}
}

internal sealed class ClassicMenuWindow : Window
{
	private const double CardWidth = 460;

	private const double CardHeight = 380;

	private static readonly System.Windows.Media.FontFamily Face = new System.Windows.Media.FontFamily("Source Sans Pro, Segoe UI");

	private readonly RobloxOverlayAnchor _anchor;

	private readonly Border _dimmer;

	private readonly Grid _stage;

	private readonly Canvas _screens;

	private readonly ScaleTransform _scale;

	private readonly TranslateTransform _slide;

	private readonly Border _card;

	private Canvas? _current;

	private Canvas? _incoming;

	private bool _animating;

	private CancellationTokenSource? _actions;

	public bool IsClosing { get; private set; }

	public ClassicMenuWindow()
	{
		Title = "Voidstrap Classic Menu";
		LinuxOverlaySurface.ReleaseMainWindowClaim(this);
		WindowStyle = WindowStyle.None;
		AllowsTransparency = true;
		Background = Brushes.Transparent;
		Topmost = true;
		ShowInTaskbar = false;
		ShowActivated = false;
		ResizeMode = ResizeMode.NoResize;
		SnapsToDevicePixels = true;
		UseLayoutRounding = true;

		_dimmer = new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0))
		};

		_card = new Border
		{
			Width = CardWidth,
			Height = CardHeight,
			Background = new SolidColorBrush(Color.FromArgb(128, 27, 27, 27))
		};

		_screens = new Canvas
		{
			Width = CardWidth,
			Height = CardHeight,
			ClipToBounds = true
		};

		_scale = new ScaleTransform(1, 1);
		_slide = new TranslateTransform(0, 0);

		Grid card = new Grid
		{
			Width = CardWidth,
			Height = CardHeight,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			LayoutTransform = _scale,
			RenderTransform = _slide
		};
		card.Children.Add(_card);
		card.Children.Add(_screens);

		_stage = new Grid();
		_stage.Children.Add(_dimmer);
		_stage.Children.Add(card);
		Content = _stage;

		SourceInitialized += OnSourceInitialized;
		Closed += OnClosed;

		_anchor = new RobloxOverlayAnchor(this, placement: RobloxOverlayPlacement.Fill);
		ShowDefaultScreen();
	}

	private void OnSourceInitialized(object? sender, EventArgs e)
	{
		IntPtr handle = new WindowInteropHelper(this).Handle;
		ClassicTopBarNative.MakeNonActivating(handle);
	}

	public void PlayOpen()
	{
		ShowDefaultScreen();
		double factor = ClassicTopBarOverlay.Scale / Math.Max(0.01, VisualTreeHelper.GetDpi(this).DpiScaleY);
		_scale.ScaleX = factor;
		_scale.ScaleY = factor;
		_anchor.Refresh();
		Visibility = Visibility.Visible;
		Opacity = 1;
		double travel = (ActualHeight + CardHeight * factor) / 2;
		DoubleAnimation slide = new DoubleAnimation(-travel, 0, TimeSpan.FromMilliseconds(150))
		{
			EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		};
		_slide.BeginAnimation(TranslateTransform.YProperty, slide);
		_slide.Y = 0;
		DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
		{
			FillBehavior = FillBehavior.Stop
		};
		_dimmer.BeginAnimation(OpacityProperty, fade);
		_dimmer.Opacity = 1;
	}

	public void PlayClose()
	{
		double factor = Math.Max(0.01, _scale.ScaleY);
		double travel = (ActualHeight + CardHeight * factor) / 2;
		DoubleAnimation slide = new DoubleAnimation(0, -travel, TimeSpan.FromMilliseconds(120))
		{
			EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseIn },
			FillBehavior = FillBehavior.Stop
		};
		slide.Completed += OnCloseCompleted;
		_slide.BeginAnimation(TranslateTransform.YProperty, slide);
		DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
		{
			FillBehavior = FillBehavior.Stop
		};
		_dimmer.BeginAnimation(OpacityProperty, fade);
		_dimmer.Opacity = 0;
	}

	private void OnCloseCompleted(object? sender, EventArgs e)
	{
		_slide.Y = 0;
		Visibility = Visibility.Hidden;
		Hide();
	}

	private void ShowDefaultScreen()
	{
		_screens.Children.Clear();
		_current = BuildDefaultScreen();
		Canvas.SetLeft(_current, 0);
		_screens.Children.Add(_current);
	}

	private TextBlock Label(string text, double size, bool bold, Color color)
	{
		return new TextBlock
		{
			Text = text,
			FontFamily = Face,
			FontSize = size,
			FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
			Foreground = new SolidColorBrush(color),
			TextAlignment = TextAlignment.Center
		};
	}

	private void Place(Canvas host, UIElement element, double left, double top)
	{
		Canvas.SetLeft(host, 0);
		Canvas.SetLeft(element, left);
		Canvas.SetTop(element, top);
		host.Children.Add(element);
	}

	private void PlaceCentered(Canvas host, FrameworkElement element, double top)
	{
		element.Width = CardWidth;
		Canvas.SetLeft(element, 0);
		Canvas.SetTop(element, top);
		host.Children.Add(element);
	}

	private Canvas NewScreen()
	{
		return new Canvas
		{
			Width = CardWidth,
			Height = CardHeight
		};
	}

	private Canvas BuildDefaultScreen()
	{
		Canvas screen = NewScreen();
		PlaceCentered(screen, Label("Game Menu", 21, true, Colors.White), 12);
		Place(screen, new ClassicImageButton("Resume.png", 328, 41, ClassicTopBarOverlay.HideMenu), 65, 55);
		Place(screen, new ClassicImageButton("Reset.png", 328, 41, ShowResetScreen), 65, 104);
		Place(screen, new ClassicImageButton("Settings.png", 328, 41, ShowSettingsScreen), 65, 153);
		Place(screen, new ClassicImageButton("Help.png", 162, 41, OpenHelp), 65, 202);
		Place(screen, new ClassicImageButton("Screenshot.png", 162, 41, TakeScreenshot), 231, 202);
		Place(screen, new ClassicImageButton("Report.png", 162, 41, OpenReport), 65, 251);
		Place(screen, new ClassicImageButton("Record.png", 162, 41, ToggleRecording), 231, 251);
		Place(screen, new ClassicImageButton("Leave.png", 328, 41, ShowLeaveScreen), 65, 300);
		return screen;
	}

	private Canvas BuildResetScreen()
	{
		Canvas screen = NewScreen();
		PlaceCentered(screen, Label("Are you sure you want to reset\nyour character?", 18, true, Colors.White), 80);
		PlaceCentered(screen, Label("You will return to the spawn point", 13, false, Color.FromRgb(170, 170, 170)), 128);
		Place(screen, new ClassicImageButton("Cancel.png", 162, 41, ShowDefaultAnimated), 70, 202);
		Place(screen, new ClassicImageButton("Confirm.png", 162, 41, ConfirmReset), 240, 202);
		return screen;
	}

	private Canvas BuildLeaveScreen()
	{
		Canvas screen = NewScreen();
		PlaceCentered(screen, Label("Are you sure you want to leave this\ngame?", 18, true, Colors.White), 95);
		Place(screen, new ClassicImageButton("Cancel.png", 162, 41, ShowDefaultAnimated), 70, 202);
		Place(screen, new ClassicImageButton("Confirm.png", 162, 41, ConfirmLeave), 240, 202);
		return screen;
	}

	private Canvas BuildSettingsScreen()
	{
		Canvas screen = NewScreen();
		PlaceCentered(screen, Label("Settings", 24, true, Colors.White), 14);
		TextBlock caption = Label("Enable Shift Lock Switch:", 15, true, Colors.White);
		caption.TextAlignment = TextAlignment.Left;
		Place(screen, caption, 40, 66);
		ClassicImageButton toggle = new ClassicImageButton(ClassicTopBarOverlay.ShiftLockEnabled ? "ButtonOn.png" : "ButtonOff.png", 34, 34, ToggleShiftLock);
		Place(screen, toggle, 290, 60);
		ClassicImageButton back = new ClassicImageButton("Back.png", 204, 52, ShowDefaultAnimated);
		Place(screen, back, (CardWidth - 204) / 2, 320);
		return screen;
	}

	private void ShowResetScreen() => SlideTo(BuildResetScreen());

	private void ShowLeaveScreen() => SlideTo(BuildLeaveScreen());

	private void ShowSettingsScreen() => SlideTo(BuildSettingsScreen());

	private void ShowDefaultAnimated() => SlideBack(BuildDefaultScreen());

	private void SlideTo(Canvas screen)
	{
		if (_animating || _current == null)
		{
			return;
		}
		_animating = true;
		_incoming = screen;
		Canvas.SetLeft(screen, CardWidth);
		_screens.Children.Add(screen);
		Animate(_current, 0, -CardWidth);
		Animate(screen, CardWidth, 0, finish: true);
	}

	private void SlideBack(Canvas screen)
	{
		if (_animating || _current == null)
		{
			return;
		}
		_animating = true;
		_incoming = screen;
		Canvas.SetLeft(screen, -CardWidth);
		_screens.Children.Add(screen);
		Animate(_current, 0, CardWidth);
		Animate(screen, -CardWidth, 0, finish: true);
	}

	private void Animate(Canvas screen, double from, double to, bool finish = false)
	{
		DoubleAnimation animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(130))
		{
			EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		};
		if (finish)
		{
			animation.Completed += OnSlideCompleted;
		}
		screen.BeginAnimation(Canvas.LeftProperty, animation);
		Canvas.SetLeft(screen, to);
	}

	private void OnSlideCompleted(object? sender, EventArgs e)
	{
		if (_current != null && _screens.Children.Contains(_current))
		{
			DetachButtons(_current);
			_screens.Children.Remove(_current);
		}
		_current = _incoming;
		_incoming = null;
		_animating = false;
	}

	private static void DetachButtons(Panel panel)
	{
		foreach (UIElement child in panel.Children)
		{
			if (child is ClassicImageButton button)
			{
				button.Detach();
			}
		}
	}

	private CancellationToken RestartActions()
	{
		CancellationTokenSource source = new CancellationTokenSource();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _actions, source);
		try
		{
			previous?.Cancel();
			previous?.Dispose();
		}
		catch (ObjectDisposedException)
		{
		}
		return source.Token;
	}

	private void CloseForKeys()
	{
		ClassicTopBarOverlay.HideMenu();
		ClassicTopBarInput.FocusRoblox();
	}

	private async void OpenHelp()
	{
		CancellationToken token = RestartActions();
		CloseForKeys();
		try
		{
			await Task.Delay(60, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkEscape);
			await Task.Delay(120, token).ConfigureAwait(true);
			for (int index = 0; index < 4; index++)
			{
				ClassicTopBarInput.Tap(ClassicTopBarInput.VkTab);
				await Task.Delay(20, token).ConfigureAwait(true);
			}
			await Task.Delay(500, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkTab);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async void OpenReport()
	{
		CancellationToken token = RestartActions();
		CloseForKeys();
		try
		{
			await Task.Delay(60, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkEscape);
			await Task.Delay(120, token).ConfigureAwait(true);
			for (int index = 0; index < 3; index++)
			{
				ClassicTopBarInput.Tap(ClassicTopBarInput.VkTab);
				await Task.Delay(20, token).ConfigureAwait(true);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async void ConfirmReset()
	{
		CancellationToken token = RestartActions();
		CloseForKeys();
		try
		{
			await Task.Delay(60, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkEscape);
			await Task.Delay(120, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkR);
			await Task.Delay(120, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkReturn);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async void TakeScreenshot()
	{
		CancellationToken token = RestartActions();
		CloseForKeys();
		try
		{
			await Task.Delay(1000, token).ConfigureAwait(true);
			ClassicTopBarInput.Hold(ClassicTopBarInput.VkLeftWindows);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkSnapshot);
			ClassicTopBarInput.Release(ClassicTopBarInput.VkLeftWindows);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async void ToggleRecording()
	{
		CancellationToken token = RestartActions();
		CloseForKeys();
		try
		{
			await Task.Delay(100, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkF12);
			ClassicTopBarOverlay.ShowRecording(true);
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async void ToggleShiftLock()
	{
		CancellationToken token = RestartActions();
		bool enabled = !ClassicTopBarOverlay.ShiftLockEnabled;
		ClassicTopBarOverlay.SetShiftLock(enabled);
		Visibility = Visibility.Hidden;
		ClassicTopBarInput.FocusRoblox();
		try
		{
			await Task.Delay(60, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkEscape);
			await Task.Delay(120, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkTab);
			await Task.Delay(500, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkA);
			await Task.Delay(60, token).ConfigureAwait(true);
			ClassicTopBarInput.Tap(ClassicTopBarInput.VkEscape);
			await Task.Delay(60, token).ConfigureAwait(true);
			Visibility = Visibility.Visible;
			_anchor.Refresh();
			SlideBack(BuildSettingsScreen());
		}
		catch (OperationCanceledException)
		{
			Visibility = Visibility.Visible;
		}
	}

	private void ConfirmLeave()
	{
		ClassicTopBarOverlay.HideMenu();
		foreach (Process process in Process.GetProcessesByName("RobloxPlayerBeta"))
		{
			try
			{
				process.CloseMainWindow();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("ClassicTopBar", "Roblox could not be closed: " + ex.Message);
			}
			finally
			{
				process.Dispose();
			}
		}
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		IsClosing = true;
		SourceInitialized -= OnSourceInitialized;
		Closed -= OnClosed;
		CancellationTokenSource? actions = Interlocked.Exchange(ref _actions, null);
		try
		{
			actions?.Cancel();
			actions?.Dispose();
		}
		catch (ObjectDisposedException)
		{
		}
		if (_current != null)
		{
			DetachButtons(_current);
		}
		if (_incoming != null)
		{
			DetachButtons(_incoming);
		}
		_anchor.Dispose();
		ClassicTopBarOverlay.ReleaseMenu(this);
	}
}
