using System;
using System.Windows.Media.Media3D;

namespace Voidstrap.Integrations.Animation;

public readonly struct RobloxCFrame(double x, double y, double z, double r00, double r01, double r02, double r10, double r11, double r12, double r20, double r21, double r22)
{
	public readonly double X = x;

	public readonly double Y = y;

	public readonly double Z = z;

	public readonly double R00 = r00;

	public readonly double R01 = r01;

	public readonly double R02 = r02;

	public readonly double R10 = r10;

	public readonly double R11 = r11;

	public readonly double R12 = r12;

	public readonly double R20 = r20;

	public readonly double R21 = r21;

	public readonly double R22 = r22;

	public static readonly RobloxCFrame Identity = new(0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);

	public static RobloxCFrame FromPosition(double x, double y, double z)
	{
		return new RobloxCFrame(x, y, z, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);
	}

	public static RobloxCFrame Angles(double rx, double ry, double rz)
	{
		double num = Math.Cos(rx);
		double num2 = Math.Sin(rx);
		double num3 = Math.Cos(ry);
		double num4 = Math.Sin(ry);
		double num5 = Math.Cos(rz);
		double num6 = Math.Sin(rz);
		double r = num3 * num5;
		double r2 = (0.0 - num3) * num6;
		double r3 = num4;
		double r4 = num2 * num4 * num5 + num * num6;
		double r5 = (0.0 - num2) * num4 * num6 + num * num5;
		double r6 = (0.0 - num2) * num3;
		double r7 = (0.0 - num) * num4 * num5 + num2 * num6;
		double r8 = num * num4 * num6 + num2 * num5;
		double r9 = num * num3;
		return new RobloxCFrame(0.0, 0.0, 0.0, r, r2, r3, r4, r5, r6, r7, r8, r9);
	}

	public static double[] SpecialRotation(byte id)
	{
		return id switch
		{
			2 => [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0], 
			3 => [1.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, 1.0, 0.0], 
			5 => [1.0, 0.0, 0.0, 0.0, -1.0, 0.0, 0.0, 0.0, -1.0], 
			6 => [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, -1.0, 0.0], 
			7 => [0.0, 1.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, -1.0], 
			9 => [0.0, 0.0, 1.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0], 
			10 => [0.0, -1.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0], 
			12 => [0.0, 0.0, -1.0, 1.0, 0.0, 0.0, 0.0, -1.0, 0.0], 
			13 => [0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 1.0, 0.0, 0.0], 
			14 => [0.0, 0.0, -1.0, 0.0, 1.0, 0.0, 1.0, 0.0, 0.0], 
			16 => [0.0, -1.0, 0.0, 0.0, 0.0, -1.0, 1.0, 0.0, 0.0], 
			17 => [0.0, 0.0, 1.0, 0.0, -1.0, 0.0, 1.0, 0.0, 0.0], 
			20 => [-1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, -1.0], 
			21 => [-1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 1.0, 0.0], 
			23 => [-1.0, 0.0, 0.0, 0.0, -1.0, 0.0, 0.0, 0.0, 1.0], 
			24 => [-1.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, -1.0, 0.0], 
			25 => [0.0, 1.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0, 1.0], 
			27 => [0.0, 0.0, -1.0, -1.0, 0.0, 0.0, 0.0, 1.0, 0.0], 
			28 => [0.0, -1.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0, -1.0], 
			30 => [0.0, 0.0, 1.0, -1.0, 0.0, 0.0, 0.0, -1.0, 0.0], 
			31 => [0.0, 1.0, 0.0, 0.0, 0.0, -1.0, -1.0, 0.0, 0.0], 
			32 => [0.0, 0.0, 1.0, 0.0, 1.0, 0.0, -1.0, 0.0, 0.0], 
			34 => [0.0, -1.0, 0.0, 0.0, 0.0, 1.0, -1.0, 0.0, 0.0], 
			35 => [0.0, 0.0, -1.0, 0.0, -1.0, 0.0, -1.0, 0.0, 0.0], 
			_ => [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0], 
		};
	}

	public static RobloxCFrame operator *(RobloxCFrame a, RobloxCFrame b)
	{
		double x = a.X + a.R00 * b.X + a.R01 * b.Y + a.R02 * b.Z;
		double y = a.Y + a.R10 * b.X + a.R11 * b.Y + a.R12 * b.Z;
		double z = a.Z + a.R20 * b.X + a.R21 * b.Y + a.R22 * b.Z;
		double r = a.R00 * b.R00 + a.R01 * b.R10 + a.R02 * b.R20;
		double r2 = a.R00 * b.R01 + a.R01 * b.R11 + a.R02 * b.R21;
		double r3 = a.R00 * b.R02 + a.R01 * b.R12 + a.R02 * b.R22;
		double r4 = a.R10 * b.R00 + a.R11 * b.R10 + a.R12 * b.R20;
		double r5 = a.R10 * b.R01 + a.R11 * b.R11 + a.R12 * b.R21;
		double r6 = a.R10 * b.R02 + a.R11 * b.R12 + a.R12 * b.R22;
		double r7 = a.R20 * b.R00 + a.R21 * b.R10 + a.R22 * b.R20;
		double r8 = a.R20 * b.R01 + a.R21 * b.R11 + a.R22 * b.R21;
		double r9 = a.R20 * b.R02 + a.R21 * b.R12 + a.R22 * b.R22;
		return new RobloxCFrame(x, y, z, r, r2, r3, r4, r5, r6, r7, r8, r9);
	}

	public RobloxCFrame Inverse()
	{
		double r = R00;
		double r2 = R10;
		double r3 = R20;
		double r4 = R01;
		double r5 = R11;
		double r6 = R21;
		double r7 = R02;
		double r8 = R12;
		double r9 = R22;
		double x = 0.0 - (r * X + r2 * Y + r3 * Z);
		double y = 0.0 - (r4 * X + r5 * Y + r6 * Z);
		double z = 0.0 - (r7 * X + r8 * Y + r9 * Z);
		return new RobloxCFrame(x, y, z, r, r2, r3, r4, r5, r6, r7, r8, r9);
	}

	public Matrix3D ToMatrix3D()
	{
		return new Matrix3D(R00, R10, R20, 0.0, R01, R11, R21, 0.0, R02, R12, R22, 0.0, X, Y, Z, 1.0);
	}

	public static RobloxCFrame Lerp(RobloxCFrame a, RobloxCFrame b, double t)
	{
		double x = a.X + (b.X - a.X) * t;
		double y = a.Y + (b.Y - a.Y) * t;
		double z = a.Z + (b.Z - a.Z) * t;
		Quaternion quaternion = QuaternionFrom(a);
		Quaternion to = QuaternionFrom(b);
		return FromQuaternion(Quaternion.Slerp(quaternion, to, t), x, y, z);
	}

	private static Quaternion QuaternionFrom(RobloxCFrame c)
	{
		double num = c.R00 + c.R11 + c.R22;
		double num3;
		double num4;
		double num5;
		double num6;
		if (num > 0.0)
		{
			double num2 = Math.Sqrt(num + 1.0) * 2.0;
			num3 = 0.25 * num2;
			num4 = (c.R21 - c.R12) / num2;
			num5 = (c.R02 - c.R20) / num2;
			num6 = (c.R10 - c.R01) / num2;
		}
		else if (c.R00 > c.R11 && c.R00 > c.R22)
		{
			double num7 = Math.Sqrt(1.0 + c.R00 - c.R11 - c.R22) * 2.0;
			num3 = (c.R21 - c.R12) / num7;
			num4 = 0.25 * num7;
			num5 = (c.R01 + c.R10) / num7;
			num6 = (c.R02 + c.R20) / num7;
		}
		else if (c.R11 > c.R22)
		{
			double num8 = Math.Sqrt(1.0 + c.R11 - c.R00 - c.R22) * 2.0;
			num3 = (c.R02 - c.R20) / num8;
			num4 = (c.R01 + c.R10) / num8;
			num5 = 0.25 * num8;
			num6 = (c.R12 + c.R21) / num8;
		}
		else
		{
			double num9 = Math.Sqrt(1.0 + c.R22 - c.R00 - c.R11) * 2.0;
			num3 = (c.R10 - c.R01) / num9;
			num4 = (c.R02 + c.R20) / num9;
			num5 = (c.R12 + c.R21) / num9;
			num6 = 0.25 * num9;
		}
		Quaternion result = new(num4, num5, num6, num3);
		if (num4 * num4 + num5 * num5 + num6 * num6 + num3 * num3 > 1E-12)
		{
			result.Normalize();
		}
		return result;
	}

	private static RobloxCFrame FromQuaternion(Quaternion q, double x, double y, double z)
	{
		if (q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W <= 1E-12)
		{
			return new RobloxCFrame(x, y, z, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);
		}
		q.Normalize();
		double num = q.X * q.X;
		double num2 = q.Y * q.Y;
		double num3 = q.Z * q.Z;
		double num4 = q.X * q.Y;
		double num5 = q.X * q.Z;
		double num6 = q.Y * q.Z;
		double num7 = q.W * q.X;
		double num8 = q.W * q.Y;
		double num9 = q.W * q.Z;
		return new RobloxCFrame(x, y, z, 1.0 - 2.0 * (num2 + num3), 2.0 * (num4 - num9), 2.0 * (num5 + num8), 2.0 * (num4 + num9), 1.0 - 2.0 * (num + num3), 2.0 * (num6 - num7), 2.0 * (num5 - num8), 2.0 * (num6 + num7), 1.0 - 2.0 * (num + num2));
	}
}
