using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VoidstrapClient.Common.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AvatarAssetType
{
	[Description("T-Shirt")]
	TShirt,
	Shirt,
	Pants,
	Hat,
	Face,
	Head,
	Torso,
	[Description("Left Arm")]
	LeftArm,
	[Description("Right Arm")]
	RightArm,
	[Description("Left Leg")]
	LeftLeg,
	[Description("Right Leg")]
	RightLeg,
	Package,
	Gear
}
