using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace Voidstrap.UI.Elements.Settings.Pages;

public class GradientStopViewModel : INotifyPropertyChanged
{
	private float offset;

	private string colorHex = "#FFFFFF";

	public float Offset
	{
		get
		{
			return offset;
		}
		set
		{
			offset = value;
			OnPropertyChanged("Offset");
		}
	}

	public string ColorHex
	{
		get
		{
			return colorHex;
		}
		set
		{
			colorHex = value;
			OnPropertyChanged("ColorHex");
		}
	}

	public Color Color
	{
		get
		{
			try
			{
				return ColorTranslator.FromHtml(colorHex);
			}
			catch
			{
				return Color.White;
			}
		}
		set
		{
			ColorHex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
