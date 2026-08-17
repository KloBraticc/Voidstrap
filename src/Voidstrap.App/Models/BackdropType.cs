using Voidstrap.Models.Attributes;

namespace Voidstrap.Models;

public enum BackdropType
{
	[EnumName(StaticName = "Mica")]
	Mica = 0,

	[EnumName(StaticName = "Aero")]
	Aero = 1,

	[EnumName(StaticName = "Acrylic")]
	Acrylic = 2,

	[EnumName(StaticName = "Default")]
	Default = 3,

	[EnumName(StaticName = "Mica Alt")]
	MicaAlt = 4,

	[EnumName(StaticName = "No backdrop")]
	None = 5
}
