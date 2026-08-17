using System.ComponentModel;
using System.Text.Json.Serialization;

namespace VoidstrapClient.Common.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AvatarSlot
{
	[Description("T-Shirt")]
	TShirt,
	Shirt,
	Pants,
	[Description("Hat #1")]
	Hat1,
	[Description("Hat #2")]
	Hat2,
	[Description("Hat #3")]
	Hat3,
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
	Gear
}
