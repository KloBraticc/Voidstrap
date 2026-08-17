using System;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Voidstrap.Integrations.Animation;

public static class RobloxRig
{
	private const double H = Math.PI / 2.0;

	private const double P = Math.PI;

	private static readonly Color Skin = Color.FromRgb(245, 205, 48);

	private static readonly Color Torso = Color.FromRgb(13, 105, 172);

	private static readonly Color Legs = Color.FromRgb(40, 127, 71);

	private static RobloxCFrame CF(double x, double y, double z, double rx, double ry, double rz)
	{
		return RobloxCFrame.FromPosition(x, y, z) * RobloxCFrame.Angles(rx, ry, rz);
	}

	public static RigDefinition R6()
	{
		return new RigDefinition
		{
			RootPart = "HumanoidRootPart",
			Parts = 
			{
				new RigPart
				{
					Name = "HumanoidRootPart",
					Size = new Vector3D(2.0, 2.0, 1.0),
					Color = Torso
				},
				new RigPart
				{
					Name = "Torso",
					Size = new Vector3D(2.0, 2.0, 1.0),
					Color = Torso
				},
				new RigPart
				{
					Name = "Head",
					Size = new Vector3D(2.0, 1.0, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "Left Arm",
					Size = new Vector3D(1.0, 2.0, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "Right Arm",
					Size = new Vector3D(1.0, 2.0, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "Left Leg",
					Size = new Vector3D(1.0, 2.0, 1.0),
					Color = Legs
				},
				new RigPart
				{
					Name = "Right Leg",
					Size = new Vector3D(1.0, 2.0, 1.0),
					Color = Legs
				}
			},
			Joints = 
			{
				new RigJoint
				{
					Name = "Root",
					Part0 = "HumanoidRootPart",
					Part1 = "Torso",
					C0 = CF(0.0, 0.0, 0.0, -Math.PI / 2.0, 0.0, Math.PI),
					C1 = CF(0.0, 0.0, 0.0, -Math.PI / 2.0, 0.0, Math.PI)
				},
				new RigJoint
				{
					Name = "Neck",
					Part0 = "Torso",
					Part1 = "Head",
					C0 = CF(0.0, 1.0, 0.0, -Math.PI / 2.0, 0.0, Math.PI),
					C1 = CF(0.0, -0.5, 0.0, -Math.PI / 2.0, 0.0, Math.PI)
				},
				new RigJoint
				{
					Name = "Left Shoulder",
					Part0 = "Torso",
					Part1 = "Left Arm",
					C0 = CF(-1.0, 0.5, 0.0, 0.0, -Math.PI / 2.0, 0.0),
					C1 = CF(0.5, 0.5, 0.0, 0.0, -Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "Right Shoulder",
					Part0 = "Torso",
					Part1 = "Right Arm",
					C0 = CF(1.0, 0.5, 0.0, 0.0, Math.PI / 2.0, 0.0),
					C1 = CF(-0.5, 0.5, 0.0, 0.0, Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "Left Hip",
					Part0 = "Torso",
					Part1 = "Left Leg",
					C0 = CF(-1.0, -1.0, 0.0, 0.0, -Math.PI / 2.0, 0.0),
					C1 = CF(-0.5, 1.0, 0.0, 0.0, -Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "Right Hip",
					Part0 = "Torso",
					Part1 = "Right Leg",
					C0 = CF(1.0, -1.0, 0.0, 0.0, Math.PI / 2.0, 0.0),
					C1 = CF(0.5, 1.0, 0.0, 0.0, Math.PI / 2.0, 0.0)
				}
			}
		};
	}

	public static RigDefinition R15()
	{
		return new RigDefinition
		{
			RootPart = "HumanoidRootPart",
			Parts = 
			{
				new RigPart
				{
					Name = "HumanoidRootPart",
					Size = new Vector3D(2.0, 2.0, 1.0),
					Color = Torso
				},
				new RigPart
				{
					Name = "LowerTorso",
					Size = new Vector3D(2.0, 0.8, 1.0),
					Color = Torso
				},
				new RigPart
				{
					Name = "UpperTorso",
					Size = new Vector3D(2.0, 1.2, 1.0),
					Color = Torso
				},
				new RigPart
				{
					Name = "Head",
					Size = new Vector3D(1.2, 1.2, 1.2),
					Color = Skin
				},
				new RigPart
				{
					Name = "LeftUpperArm",
					Size = new Vector3D(1.0, 1.2, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "LeftLowerArm",
					Size = new Vector3D(1.0, 1.0, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "LeftHand",
					Size = new Vector3D(1.0, 0.5, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "RightUpperArm",
					Size = new Vector3D(1.0, 1.2, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "RightLowerArm",
					Size = new Vector3D(1.0, 1.0, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "RightHand",
					Size = new Vector3D(1.0, 0.5, 1.0),
					Color = Skin
				},
				new RigPart
				{
					Name = "LeftUpperLeg",
					Size = new Vector3D(0.9, 1.4, 1.0),
					Color = Legs
				},
				new RigPart
				{
					Name = "LeftLowerLeg",
					Size = new Vector3D(0.9, 1.2, 1.0),
					Color = Legs
				},
				new RigPart
				{
					Name = "LeftFoot",
					Size = new Vector3D(0.9, 0.4, 1.0),
					Color = Legs
				},
				new RigPart
				{
					Name = "RightUpperLeg",
					Size = new Vector3D(0.9, 1.4, 1.0),
					Color = Legs
				},
				new RigPart
				{
					Name = "RightLowerLeg",
					Size = new Vector3D(0.9, 1.2, 1.0),
					Color = Legs
				},
				new RigPart
				{
					Name = "RightFoot",
					Size = new Vector3D(0.9, 0.4, 1.0),
					Color = Legs
				}
			},
			Joints = 
			{
				new RigJoint
				{
					Name = "Root",
					Part0 = "HumanoidRootPart",
					Part1 = "LowerTorso",
					C0 = CF(0.0, -1.0, 0.0, -Math.PI / 2.0, 0.0, Math.PI),
					C1 = CF(0.0, -0.4, 0.0, -Math.PI / 2.0, 0.0, Math.PI)
				},
				new RigJoint
				{
					Name = "Waist",
					Part0 = "LowerTorso",
					Part1 = "UpperTorso",
					C0 = CF(0.0, 0.4, 0.0, -Math.PI / 2.0, 0.0, Math.PI),
					C1 = CF(0.0, -0.6, 0.0, -Math.PI / 2.0, 0.0, Math.PI)
				},
				new RigJoint
				{
					Name = "Neck",
					Part0 = "UpperTorso",
					Part1 = "Head",
					C0 = CF(0.0, 0.6, 0.0, -Math.PI / 2.0, 0.0, Math.PI),
					C1 = CF(0.0, -0.6, 0.0, -Math.PI / 2.0, 0.0, Math.PI)
				},
				new RigJoint
				{
					Name = "LeftShoulder",
					Part0 = "UpperTorso",
					Part1 = "LeftUpperArm",
					C0 = CF(-1.4, 0.4, 0.0, 0.0, -Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.6, 0.0, 0.0, -Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "LeftElbow",
					Part0 = "LeftUpperArm",
					Part1 = "LeftLowerArm",
					C0 = CF(0.0, -0.6, 0.0, 0.0, -Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.5, 0.0, 0.0, -Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "LeftWrist",
					Part0 = "LeftLowerArm",
					Part1 = "LeftHand",
					C0 = CF(0.0, -0.5, 0.0, 0.0, -Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.2, 0.0, 0.0, -Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "RightShoulder",
					Part0 = "UpperTorso",
					Part1 = "RightUpperArm",
					C0 = CF(1.4, 0.4, 0.0, 0.0, Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.6, 0.0, 0.0, Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "RightElbow",
					Part0 = "RightUpperArm",
					Part1 = "RightLowerArm",
					C0 = CF(0.0, -0.6, 0.0, 0.0, Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.5, 0.0, 0.0, Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "RightWrist",
					Part0 = "RightLowerArm",
					Part1 = "RightHand",
					C0 = CF(0.0, -0.5, 0.0, 0.0, Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.2, 0.0, 0.0, Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "LeftHip",
					Part0 = "LowerTorso",
					Part1 = "LeftUpperLeg",
					C0 = CF(-0.5, -0.4, 0.0, 0.0, -Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.7, 0.0, 0.0, -Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "LeftKnee",
					Part0 = "LeftUpperLeg",
					Part1 = "LeftLowerLeg",
					C0 = CF(0.0, -0.7, 0.0, 0.0, -Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.6, 0.0, 0.0, -Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "LeftAnkle",
					Part0 = "LeftLowerLeg",
					Part1 = "LeftFoot",
					C0 = CF(0.0, -0.6, 0.0, 0.0, -Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.2, 0.0, 0.0, -Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "RightHip",
					Part0 = "LowerTorso",
					Part1 = "RightUpperLeg",
					C0 = CF(0.5, -0.4, 0.0, 0.0, Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.7, 0.0, 0.0, Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "RightKnee",
					Part0 = "RightUpperLeg",
					Part1 = "RightLowerLeg",
					C0 = CF(0.0, -0.7, 0.0, 0.0, Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.6, 0.0, 0.0, Math.PI / 2.0, 0.0)
				},
				new RigJoint
				{
					Name = "RightAnkle",
					Part0 = "RightLowerLeg",
					Part1 = "RightFoot",
					C0 = CF(0.0, -0.6, 0.0, 0.0, Math.PI / 2.0, 0.0),
					C1 = CF(0.0, 0.2, 0.0, 0.0, Math.PI / 2.0, 0.0)
				}
			}
		};
	}
}
