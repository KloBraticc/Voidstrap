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

	public LibraryPage()
	{
		base.DataContext = _viewModel;
		InitializeComponent();
		Voidstrap.Utility.WebsiteAuth.Changed += OnWebsiteAuthChanged;
		Unloaded += OnPageUnloaded;
	}

	private void OnPageUnloaded(object sender, RoutedEventArgs e)
	{
		Unloaded -= OnPageUnloaded;
		Voidstrap.Utility.WebsiteAuth.Changed -= OnWebsiteAuthChanged;
	}

	private void OnWebsiteAuthChanged()
	{
		Dispatcher.BeginInvoke((Action)delegate
		{
			_ = ReloadAsync();
		});
	}

	private async System.Threading.Tasks.Task ReloadAsync()
	{
		try
		{
			await _viewModel.LoadAsync();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LibraryPage", "Could not reload after the account changed: " + ex.Message);
		}
	}

	private async void Page_Loaded(object sender, RoutedEventArgs e)
	{
		Unloaded -= OnPageUnloaded;
		Unloaded += OnPageUnloaded;
		Voidstrap.Utility.WebsiteAuth.Changed -= OnWebsiteAuthChanged;
		Voidstrap.Utility.WebsiteAuth.Changed += OnWebsiteAuthChanged;
		if (!_viewModel.HasLoaded)
			await _viewModel.LoadAsync();
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
}
