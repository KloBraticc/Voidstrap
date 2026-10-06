using Voidstrap.Models.Attributes;

namespace Voidstrap.Enums;

public enum WindowCornerStyle
{
	[EnumName(StaticName = "Rounded")]
	Rounded,
	[EnumName(StaticName = "Slightly rounded")]
	SlightlyRounded,
	[EnumName(StaticName = "Square")]
	Square
}
