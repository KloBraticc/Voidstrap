using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.UI.ViewModels.Bootstrapper;

public class ByfronDialogViewModel : BootstrapperDialogViewModel
{
	public ImageSource ByfronLogoLocation { get; set; } = Voidstrap.Utility.SafeImaging.FromUri(new Uri("pack://application:,,,/Resources/BootstrapperStyles/ByfronDialog/ByfronLogoDark.jpg"));

	public Thickness DialogBorder { get; set; } = new Thickness(0.0);

	public Brush Background { get; set; } = Brushes.Black;

	public Brush Foreground { get; set; } = new SolidColorBrush(Color.FromRgb(239, 239, 239));

	public Brush IconColor { get; set; } = new SolidColorBrush(Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue));

	public Brush ProgressBarBackground { get; set; } = new SolidColorBrush(Color.FromRgb(86, 86, 86));

	public Visibility VersionTextVisibility
	{
		get
		{
			if (!base.CancelEnabled)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public string VersionText { get; init; }

	public ByfronDialogViewModel(IBootstrapperDialog dialog, string version)
		: base(dialog)
	{
		VersionText = version;
	}
}
