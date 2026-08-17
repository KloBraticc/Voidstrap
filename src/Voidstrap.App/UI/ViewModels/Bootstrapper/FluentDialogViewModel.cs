using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Bootstrapper;

public class FluentDialogViewModel : BootstrapperDialogViewModel
{
	public Brush BackgroundColourBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

	private bool _hasProfile;
	private string _profileName = "";
	private ImageSource? _profileAvatar;
	private Brush _profileBorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));
	private ImageSource? _borderImage;
	private double _borderImageWidth;
	private double _borderImageHeight;
	private Thickness _borderImageMargin;
	private int _borderImageZIndex = 10;

	public string ProfileName
	{
		get => _profileName;
		set { _profileName = value; OnPropertyChanged(nameof(ProfileName)); }
	}

	public ImageSource? ProfileAvatar
	{
		get => _profileAvatar;
		set { _profileAvatar = value; OnPropertyChanged(nameof(ProfileAvatar)); }
	}

	public Brush ProfileBorderBrush
	{
		get => _profileBorderBrush;
		set { _profileBorderBrush = value; OnPropertyChanged(nameof(ProfileBorderBrush)); }
	}

	public ImageSource? BorderImage
	{
		get => _borderImage;
		set { _borderImage = value; OnPropertyChanged(nameof(BorderImage)); OnPropertyChanged(nameof(BorderImageVisibility)); }
	}

	public double BorderImageWidth
	{
		get => _borderImageWidth;
		set { _borderImageWidth = value; OnPropertyChanged(nameof(BorderImageWidth)); }
	}

	public double BorderImageHeight
	{
		get => _borderImageHeight;
		set { _borderImageHeight = value; OnPropertyChanged(nameof(BorderImageHeight)); }
	}

	public Thickness BorderImageMargin
	{
		get => _borderImageMargin;
		set { _borderImageMargin = value; OnPropertyChanged(nameof(BorderImageMargin)); }
	}

	public int BorderImageZIndex
	{
		get => _borderImageZIndex;
		set { _borderImageZIndex = value; OnPropertyChanged(nameof(BorderImageZIndex)); }
	}

	public Visibility BorderImageVisibility => _borderImage != null ? Visibility.Visible : Visibility.Collapsed;

	public Visibility ProfileVisibility => _hasProfile && App.Settings.Prop.ShowLaunchProfile ? Visibility.Visible : Visibility.Collapsed;

	[Obsolete("Do not use this! This is for the designer only.", true)]
	public FluentDialogViewModel()
	{
	}

	public FluentDialogViewModel(IBootstrapperDialog dialog, bool aero)
		: base(dialog)
	{
		if (aero)
		{
			BackgroundColourBrush = ResolveGlassTint();
		}
	}

 public async Task LoadProfileAsync()
	{
		if (!App.Settings.Prop.ShowLaunchProfile)
			return;
		if (!await Task.Run(() => Voidstrap.Integrations.RobloxCookie.Exists).ConfigureAwait(true))
			return;
		try
		{
			if (LaunchProfile.TryGetCached(out string cachedName, out string cachedAvatar))
			{
				ProfileName = cachedName;
				_hasProfile = true;
				OnPropertyChanged(nameof(ProfileVisibility));
				if (!string.IsNullOrEmpty(cachedAvatar))
				{
					var cachedImage = await LaunchProfile.LoadAvatarAsync(cachedAvatar);
					if (cachedImage != null)
						ProfileAvatar = cachedImage;
				}
			}

			var data = await LaunchProfile.FetchAsync();
			if (data == null)
				return;

			if (!string.IsNullOrEmpty(data.Name))
				ProfileName = data.Name;
			if (data.Border != null)
				ProfileBorderBrush = data.Border;
			if (data.ImageBorder != null && data.ImageBorder.Image != null)
			{
				BorderImageWidth = data.ImageBorder.Width;
				BorderImageHeight = data.ImageBorder.Height;
				BorderImageMargin = data.ImageBorder.Margin;
				BorderImageZIndex = data.ImageBorder.ZIndex;
				BorderImage = data.ImageBorder.Image;
			}
			_hasProfile = true;
			OnPropertyChanged(nameof(ProfileVisibility));

			if (!string.IsNullOrEmpty(data.AvatarUrl))
			{
				var image = await LaunchProfile.LoadAvatarAsync(data.AvatarUrl);
				if (image != null)
					ProfileAvatar = image;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("FluentDialogViewModel::LoadProfile", ex);
		}
	}

	private static Brush ResolveGlassTint()
	{
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		Color c = Color.FromRgb(30, 11, 47);
		Color color = Color.FromRgb(19, 7, 36);
		Color c2 = Color.FromRgb(10, 4, 21);
		try
		{
			ResourceDictionary resourceDictionary = Application.Current?.Resources;
			if (resourceDictionary != null)
			{
				if (resourceDictionary["WindowBackgroundColorPrimary"] is Color color2)
				{
					c = color2;
				}
				if (resourceDictionary["WindowBackgroundColorSecondary"] is Color color3)
				{
					color = color3;
				}
				c2 = ((!((resourceDictionary["WindowBackgroundColorTertiary"] ?? resourceDictionary["WindowBackgroundColorThird"]) is Color color4)) ? color : color4);
			}
		}
		catch
		{
		}
		c = Darken(c, 0.46);
		color = Darken(color, 0.46);
		c2 = Darken(c2, 0.46);
		LinearGradientBrush linearGradientBrush = new LinearGradientBrush
		{
			StartPoint = new Point(0.0, 0.0),
			EndPoint = new Point(1.0, 1.0)
		};
		linearGradientBrush.GradientStops.Add(new GradientStop(Color.FromArgb(158, c.R, c.G, c.B), 0.0));
		linearGradientBrush.GradientStops.Add(new GradientStop(Color.FromArgb(142, color.R, color.G, color.B), 0.55));
		linearGradientBrush.GradientStops.Add(new GradientStop(Color.FromArgb(126, c2.R, c2.G, c2.B), 1.0));
		if (((Freezable)linearGradientBrush).CanFreeze)
		{
			((Freezable)linearGradientBrush).Freeze();
		}
		return linearGradientBrush;
	}

	private static Color Darken(Color c, double factor)
	{
		factor = Math.Clamp(factor, 0.0, 1.0);
		return Color.FromRgb((byte)((double)(int)c.R * factor), (byte)((double)(int)c.G * factor), (byte)((double)(int)c.B * factor));
	}
}
