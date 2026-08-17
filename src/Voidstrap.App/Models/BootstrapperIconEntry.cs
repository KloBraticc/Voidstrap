using System.Windows.Media;
using Voidstrap.Enums;
using Voidstrap.Extensions;

namespace Voidstrap.Models;

public class BootstrapperIconEntry
{
	public BootstrapperIcon IconType { get; set; }

	public ImageSource ImageSource => IconType.GetIcon().GetImageSource();
}
