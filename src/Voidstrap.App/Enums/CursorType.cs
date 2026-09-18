using Voidstrap.Models.Attributes;

namespace Voidstrap.Enums;

public enum CursorType
{
	[EnumSort(Order = 1)]
	[EnumName(StaticName = "Default Cursor")]
	Default = 0,
	[EnumSort(Order = 2)]
	[EnumName(StaticName = "Voidstrap Default")]
	VoidstrapDefault = 9,
	[EnumSort(Order = 6)]
	[EnumName(StaticName = "FPS Cursor (V1)")]
	FPSCursor = 1,
	[EnumSort(Order = 5)]
	[EnumName(StaticName = "Clean Cursor")]
	CleanCursor = 2,
	[EnumSort(Order = 4)]
	[EnumName(StaticName = "Dot Cursor")]
	DotCursor = 3,
	[EnumSort(Order = 3)]
	[EnumName(StaticName = "Stoofs Cursor")]
	StoofsCursor = 4,
	[EnumSort(Order = 7)]
	[EnumName(StaticName = "2006 Legacy Cursor")]
	From2006 = 5,
	[EnumSort(Order = 8)]
	[EnumName(StaticName = "2013 Legacy Cursor")]
	From2013 = 6,
	[EnumSort(Order = 9)]
	[EnumName(StaticName = "WhiteDotCursor")]
	WhiteDotCursor = 7,
	[EnumSort(Order = 10)]
	[EnumName(StaticName = "ArceyPlayz Dot")]
	VerySmallWhiteDot = 8,
	[EnumSort(Order = 11)]
	[EnumName(StaticName = "Custom")]
	Custom = 10
}
