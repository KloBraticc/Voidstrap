using Voidstrap.Models.Attributes;

namespace Voidstrap.Enums;

public enum CleanerOptions
{
	[EnumName(StaticName = "Never")]
	Never,
	[EnumName(StaticName = "After Launch")]
	AfterLaunch,
	[EnumName(StaticName = "After 1 Day")]
	OneDay,
	[EnumName(StaticName = "After 1 Week")]
	OneWeek,
	[EnumName(StaticName = "After 2 Weeks")]
	TwoWeeks,
	[EnumName(StaticName = "After 3 Weeks")]
	ThreeWeeks,
	[EnumName(StaticName = "After 1 Month")]
	OneMonth,
	[EnumName(StaticName = "After 2 Months")]
	TwoMonths,
	[EnumName(StaticName = "After 3 Months")]
	ThreeMonths
}
