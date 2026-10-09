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
	None = 5,

	[EnumName(FromTranslation = "Appearance.Backdrop.Sidebar")]
	Sidebar = 6,

	[EnumName(FromTranslation = "Appearance.Backdrop.Popover")]
	Popover = 7,

	[EnumName(FromTranslation = "Appearance.Backdrop.Hud")]
	Hud = 8,

	[EnumName(FromTranslation = "Appearance.Backdrop.UnderWindow")]
	UnderWindow = 9,

	[EnumName(FromTranslation = "Appearance.Backdrop.UnderPage")]
	UnderPage = 10
}
