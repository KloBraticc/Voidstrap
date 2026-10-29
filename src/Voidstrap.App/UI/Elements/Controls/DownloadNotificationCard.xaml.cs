using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Controls;

public partial class DownloadNotificationCard : UserControl
{
	private Action? _cancel;

	private bool _shown;

	private bool _locked;

	private bool _cancelling;

	private bool _completed;

	public DownloadNotificationCard()
	{
		InitializeComponent();
	}

	public bool IsShown => _shown;

	public bool IsCompleted => _completed;

	public void Show(string title, Action? cancel)
	{
		_cancel = cancel;
		CardTitle.Text = title;
		CardMessage.Text = "Preparing";
		CardMessage.Visibility = Visibility.Visible;
		CardProgress.IsIndeterminate = true;
		CardIcon.Symbol = SymbolRegular.ArrowDownload24;
		CardAction.Content = "Cancel";
		CardAction.Visibility = cancel != null ? Visibility.Visible : Visibility.Collapsed;
		CardDismiss.Visibility = cancel != null ? Visibility.Visible : Visibility.Collapsed;
		_cancelling = false;
		_completed = false;
		SetLocked(false);
		if (_shown)
			return;
		_shown = true;
		IsHitTestVisible = true;
		Visibility = Visibility.Visible;
		Opacity = 1.0;
		CardTranslate.X = 0.0;
		BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		});
		CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(28.0, 0.0, TimeSpan.FromMilliseconds(220))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		});
	}

	public void Update(string title, double fraction)
	{
		if (_cancelling && fraction < 1.0)
		{
			return;
		}
		CardTitle.Text = title;
		if (fraction < 0.0)
		{
			CardProgress.IsIndeterminate = true;
			CardMessage.Text = "Working";
			SetLocked(false);
			return;
		}
		int percent = (int)Math.Round(Math.Clamp(fraction, 0.0, 1.0) * 100.0);
		CardProgress.IsIndeterminate = false;
		CardProgress.Value = percent;
		if (fraction >= 1.0)
		{
			_completed = true;
			_cancelling = false;
			_cancel = null;
			CardMessage.Visibility = Visibility.Collapsed;
			CardIcon.Symbol = SymbolRegular.Info24;
			CardAction.Content = "Close";
			CardAction.Visibility = Visibility.Visible;
			CardDismiss.Visibility = Visibility.Visible;
			SetLocked(false);
			return;
		}
		CardMessage.Text = Math.Min(percent, 99) + "% done";
		SetLocked(false);
	}

	private void SetLocked(bool locked)
	{
		_locked = locked;
		CardAction.IsEnabled = !locked;
		CardDismiss.IsEnabled = !locked;
		CardDismiss.ToolTip = locked ? (_cancelling ? "Cancelling" : "Please wait") : (_completed ? "Close" : "Cancel");
	}

	public void Hide()
	{
		if (!_shown)
			return;
		_shown = false;
		_cancel = null;
		_cancelling = false;
		_completed = false;
		SetLocked(false);
		IsHitTestVisible = false;
		DoubleAnimation fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(160))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
			FillBehavior = FillBehavior.Stop
		};
		fade.Completed += Fade_Completed;
		BeginAnimation(OpacityProperty, fade);
		CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(0.0, 28.0, TimeSpan.FromMilliseconds(180))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
			FillBehavior = FillBehavior.Stop
		});
	}

	private void Fade_Completed(object? sender, EventArgs e)
	{
		if (_shown)
			return;
		Opacity = 0.0;
		CardTranslate.X = 28.0;
		Visibility = Visibility.Collapsed;
	}

	private void Dismiss_Click(object sender, RoutedEventArgs e)
	{
		if (_completed)
		{
			Hide();
			return;
		}
		if (_locked)
			return;
		Action? cancel = _cancel;
		_cancelling = true;
		SetLocked(true);
		CardMessage.Text = "Cancelling";
		cancel?.Invoke();
	}
}
