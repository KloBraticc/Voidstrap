using System;

namespace Voidstrap.Integrations.Animation;

public static class Lz4Block
{
	public static byte[] Decompress(byte[] src, int srcOffset, int srcLength, int outputSize)
	{
		byte[] array = new byte[outputSize];
		int num = srcOffset;
		int num2 = 0;
		int num3 = srcOffset + srcLength;
		while (num < num3 && num2 < outputSize)
		{
			int num4 = src[num++];
			int num5 = num4 >> 4;
			if (num5 == 15)
			{
				int num6;
				do
				{
					if (num >= num3)
					{
						return array;
					}
					num6 = src[num++];
					num5 += num6;
				}
				while (num6 == 255);
			}
			if (num5 > 0)
			{
				if (num + num5 > num3)
				{
					num5 = num3 - num;
				}
				if (num2 + num5 > outputSize)
				{
					num5 = outputSize - num2;
				}
				Array.Copy(src, num, array, num2, num5);
				num += num5;
				num2 += num5;
			}
			if (num + 2 > num3 || num2 >= outputSize)
			{
				break;
			}
			int num7 = src[num++] | (src[num++] << 8);
			if (num7 == 0)
			{
				break;
			}
			int num8 = num4 & 0xF;
			if (num8 == 15)
			{
				int num9;
				do
				{
					if (num >= num3)
					{
						return array;
					}
					num9 = src[num++];
					num8 += num9;
				}
				while (num9 == 255);
			}
			num8 += 4;
			int num10 = num2 - num7;
			if (num10 < 0)
			{
				break;
			}
			for (int i = 0; i < num8; i++)
			{
				if (num2 >= outputSize)
				{
					break;
				}
				array[num2++] = array[num10++];
			}
		}
		return array;
	}
}
