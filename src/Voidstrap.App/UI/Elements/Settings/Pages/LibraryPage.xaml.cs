using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class LibraryPage : UiPage{
	private const double StickyThreshold = 220;

	private readonly LibraryViewModel _viewModel = new LibraryViewModel();

	private bool _stickyShown;

	private const double SidebarMinWidth = 64.0;

	private const double SidebarMaxWidth = 320.0;

	private const double SidebarDefaultWidth = 225.0;

	private const double SidebarIconThreshold = 150.0;

	private const double SidebarCollapsedWidth = 72.0;

	private double _sidebarExpandedWidth = SidebarDefaultWidth;

	private bool _sidebarIconMode;

	private bool _sidebarResizing;

	private double _resizeStartX;

	private double _resizeStartWidth;

	public bool IsSidebarCollapsed => _sidebarIconMode;

	public LibraryPage()
	{
		base.DataContext = _viewModel;
		InitializeComponent();
		ApplySidebarWidth(App.Settings.Prop.LibrarySidebarWidth);
	}

	private async void Page_Loaded(object sender, RoutedEventArgs e)
	{
		if (!_viewModel.HasLoaded)
			await _viewModel.LoadAsync();
	}

	private void ApplySidebarWidth(double width)
	{
		if (double.IsNaN(width) || width <= 0.0)
			width = SidebarDefaultWidth;
		width = Math.Clamp(width, SidebarMinWidth, SidebarMaxWidth);
		if (Math.Abs(width - SidebarDefaultWidth) <= 12.0)
			width = SidebarDefaultWidth;
		LibrarySidebar.Width = width;
		bool iconsOnly = width < SidebarIconThreshold;
		if (!iconsOnly)
			_sidebarExpandedWidth = width;
		_sidebarIconMode = iconsOnly;
		Visibility labels = iconsOnly ? Visibility.Collapsed : Visibility.Visible;
		SidebarHomeLabel.Visibility = labels;
		SidebarHomeIcon.Margin = iconsOnly ? new Thickness(0) : new Thickness(0, 0, 14, 0);
		SidebarHomeContent.HorizontalAlignment = iconsOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;
		SidebarSearchBox.Visibility = labels;
		SidebarRefreshRow.Visibility = labels;
		SidebarAddRow.Visibility = labels;
		SidebarList.Tag = iconsOnly ? "icons" : "full";
	}

	private void SaveSidebarWidth()
	{
		App.Settings.Prop.LibrarySidebarWidth = LibrarySidebar.Width;
		App.Settings.SaveDeferred();
	}

	public void ToggleSidebar()
	{
		if (_sidebarResizing)
		{
			_sidebarResizing = false;
			try
			{
				SidebarResizer.ReleaseMouseCapture();
			}
			catch
			{
			}
		}
		LibrarySidebar.BeginAnimation(WidthProperty, null);
		double from = double.IsNaN(LibrarySidebar.Width) ? SidebarDefaultWidth : LibrarySidebar.Width;
		double to = _sidebarIconMode ? Math.Max(SidebarIconThreshold, _sidebarExpandedWidth) : SidebarCollapsedWidth;
		if (Math.Abs(to - from) <= 1.0)
		{
			ApplySidebarWidth(to);
			SaveSidebarWidth();
			return;
		}
		if (!_sidebarIconMode)
			SidebarList.Tag = "full";
		DoubleAnimation slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(180))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		};
		slide.Completed += SidebarToggleAnimation_Completed;
		LibrarySidebar.BeginAnimation(WidthProperty, slide);
	}

	private void SidebarToggleAnimation_Completed(object? sender, EventArgs e)
	{
		ApplySidebarWidth(_sidebarIconMode ? Math.Max(SidebarIconThreshold, _sidebarExpandedWidth) : SidebarCollapsedWidth);
		SaveSidebarWidth();
	}

	private void SidebarResizer_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		LibrarySidebar.BeginAnimation(WidthProperty, null);
		if (e.ClickCount > 1)
		{
			_sidebarResizing = false;
			try
			{
				SidebarResizer.ReleaseMouseCapture();
			}
			catch
			{
			}
			ApplySidebarWidth(SidebarDefaultWidth);
			SaveSidebarWidth();
			e.Handled = true;
			return;
		}
		_sidebarResizing = true;
		_resizeStartX = e.GetPosition(this).X;
		_resizeStartWidth = double.IsNaN(LibrarySidebar.Width) ? SidebarDefaultWidth : LibrarySidebar.Width;
		SidebarResizer.CaptureMouse();
		e.Handled = true;
	}

	private void SidebarResizer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (_sidebarResizing)
			ApplySidebarWidth(_resizeStartWidth + e.GetPosition(this).X - _resizeStartX);
	}

	private void SidebarResizer_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (!_sidebarResizing)
			return;
		_sidebarResizing = false;
		try
		{
			SidebarResizer.ReleaseMouseCapture();
		}
		catch
		{
		}
		SaveSidebarWidth();
	}

	private void DetailScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		bool shouldShow = e.VerticalOffset > StickyThreshold;
		if (shouldShow == _stickyShown)
			return;

		_stickyShown = shouldShow;
		StickyBar.IsHitTestVisible = shouldShow;

		var fade = new DoubleAnimation(shouldShow ? 1.0 : 0.0, TimeSpan.FromMilliseconds(160))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
		};
		StickyBar.BeginAnimation(OpacityProperty, fade);
	}

	private void DashboardScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (e.ExtentHeight <= 0)
			return;
		if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 480)
			_viewModel.LoadMoreGames();
	}
}
