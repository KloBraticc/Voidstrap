using System.Threading.Tasks;
using System.Windows.Media;
using RinColor;

namespace Voidstrap.Utility;

internal sealed class MacOSColorPicker : IRinScreenColorPicker
{
	public bool IsAvailable => Voidstrap.Platform.MacOS.MacOSColorSampler.IsAvailable;

	public async Task<Color?> PickAsync()
	{
		(byte R, byte G, byte B)? picked = await Voidstrap.Platform.MacOS.MacOSColorSampler.PickAsync();
		return picked is { } rgb ? Color.FromRgb(rgb.R, rgb.G, rgb.B) : null;
	}
}
