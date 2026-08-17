using System;
using System.Collections.Generic;
using System.Linq;
using VoidstrapClient.Common.Enums;

namespace VoidstrapClient.Common.Models;

public class Character : ICloneable
{
	public int Head { get; set; } = 24;

	public int Torso { get; set; } = 23;

	public int RightArm { get; set; } = 24;

	public int LeftArm { get; set; } = 24;

	public int RightLeg { get; set; } = 119;

	public int LeftLeg { get; set; } = 119;

	public Dictionary<AvatarSlot, ulong> Equipped { get; set; } = new Dictionary<AvatarSlot, ulong>();

	public FigureCharacterType FigureCharacterType { get; set; } = FigureCharacterType.Figure1;

	public object Clone()
	{
		return new Character
		{
			Head = Head,
			Torso = Torso,
			RightArm = RightArm,
			LeftArm = LeftArm,
			RightLeg = RightLeg,
			LeftLeg = LeftLeg,
			Equipped = Equipped.ToDictionary((KeyValuePair<AvatarSlot, ulong> e) => e.Key, (KeyValuePair<AvatarSlot, ulong> e) => e.Value),
			FigureCharacterType = FigureCharacterType
		};
	}
}
