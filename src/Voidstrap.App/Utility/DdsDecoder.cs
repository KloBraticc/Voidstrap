using System;

namespace Voidstrap.Utility;

internal static class DdsDecoder
{
    private const int HeaderBytes = 128;

    private const int Dx10HeaderBytes = 20;

    private enum Format
    {
        Uncompressed,
        Dxt1,
        Dxt3,
        Dxt5,
    }

    public static bool TryDecode(byte[] dds, out int width, out int height, out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = [];
        if (dds.Length < HeaderBytes || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S' || dds[3] != (byte)' ')
            return false;
        int capsFlags = BitConverter.ToInt32(dds, 8);
        if ((capsFlags & 0x2) == 0 || dds.Length < HeaderBytes)
            return false;
        height = BitConverter.ToInt32(dds, 12);
        width = BitConverter.ToInt32(dds, 16);
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            return false;
        int formatFlags = BitConverter.ToInt32(dds, 80);
        int bits = BitConverter.ToInt32(dds, 88);
        string fourCc = (formatFlags & 0x4) != 0 ? ReadFourCc(dds, 84) : "";
        int dataOffset = HeaderBytes;
        Format format;
        uint rMask;
        uint gMask;
        uint bMask;
        uint aMask;
        if (fourCc == "DX10")
        {
            if (dds.Length < HeaderBytes + Dx10HeaderBytes)
                return false;
            int dxgi = BitConverter.ToInt32(dds, 128);
            dataOffset = HeaderBytes + Dx10HeaderBytes;
            switch (dxgi)
            {
                case 70:
                case 73:
                case 77:
                    format = dxgi == 70 ? Format.Dxt1 : dxgi == 73 ? Format.Dxt3 : Format.Dxt5;
                    rMask = gMask = bMask = aMask = 0;
                    break;
                case 71:
                    format = Format.Uncompressed;
                    bits = 32;
                    rMask = 0x000000FFu;
                    gMask = 0x0000FF00u;
                    bMask = 0x00FF0000u;
                    aMask = 0xFF000000u;
                    break;
                case 87:
                    format = Format.Uncompressed;
                    bits = 32;
                    rMask = 0x00FF0000u;
                    gMask = 0x0000FF00u;
                    bMask = 0x000000FFu;
                    aMask = 0xFF000000u;
                    break;
                default:
                    return false;
            }
        }
        else if ((formatFlags & 0x4) != 0)
        {
            switch (fourCc)
            {
                case "DXT1":
                    format = Format.Dxt1;
                    break;
                case "DXT2":
                case "DXT3":
                    format = Format.Dxt3;
                    break;
                case "DXT4":
                case "DXT5":
                    format = Format.Dxt5;
                    break;
                default:
                    return false;
            }
            rMask = gMask = bMask = aMask = 0;
        }
        else if ((formatFlags & 0x40) != 0)
        {
            format = Format.Uncompressed;
            rMask = BitConverter.ToUInt32(dds, 92);
            gMask = BitConverter.ToUInt32(dds, 96);
            bMask = BitConverter.ToUInt32(dds, 100);
            aMask = (formatFlags & 0x1) != 0 ? BitConverter.ToUInt32(dds, 104) : 0u;
        }
        else if ((formatFlags & 0x200) != 0 && bits == 8)
        {
            format = Format.Uncompressed;
            rMask = 0xFFu;
            gMask = 0xFFu;
            bMask = 0xFFu;
            aMask = 0u;
        }
        else
        {
            return false;
        }
        if (format == Format.Uncompressed)
            return DecodeUncompressed(dds, dataOffset, width, height, bits, rMask, gMask, bMask, aMask, out bgra);
        return DecodeBlocks(dds, dataOffset, width, height, format, out bgra);
    }

    private static string ReadFourCc(byte[] dds, int offset)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
            chars[i] = (char)dds[offset + i];
        return new string(chars);
    }

    private static bool DecodeUncompressed(byte[] dds, int dataOffset, int width, int height, int bits, uint rMask, uint gMask, uint bMask, uint aMask, out byte[] bgra)
    {
        bgra = [];
        int rowBytes = (width * bits + 7) / 8;
        rowBytes = (rowBytes + 3) & ~3;
        if (dataOffset + (long)rowBytes * height > dds.Length)
            return false;
        (int rShift, int rBits) = MaskParts(rMask);
        (int gShift, int gBits) = MaskParts(gMask);
        (int bShift, int bBits) = MaskParts(bMask);
        (int aShift, int aBits) = MaskParts(aMask);
        bgra = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int rowStart = dataOffset + y * rowBytes;
            int outRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                uint pixel = bits switch
                {
                    8 => dds[rowStart + x],
                    16 => BitConverter.ToUInt16(dds, rowStart + x * 2),
                    24 => (uint)(dds[rowStart + x * 3] | (dds[rowStart + x * 3 + 1] << 8) | (dds[rowStart + x * 3 + 2] << 16)),
                    32 => BitConverter.ToUInt32(dds, rowStart + x * 4),
                    _ => 0u,
                };
                int target = outRow + x * 4;
                bgra[target] = (byte)Scale(Extract(pixel, bShift, bBits), bBits);
                bgra[target + 1] = (byte)Scale(Extract(pixel, gShift, gBits), gBits);
                bgra[target + 2] = (byte)Scale(Extract(pixel, rShift, rBits), rBits);
                bgra[target + 3] = aBits > 0 ? (byte)Scale(Extract(pixel, aShift, aBits), aBits) : (byte)255;
            }
        }
        return true;
    }

    private static int Extract(uint pixel, int shift, int bits) => bits <= 0 ? 0 : (int)((pixel >> shift) & (uint)((1 << bits) - 1));

    private static int Scale(int value, int bits)
    {
        if (bits <= 0)
            return 0;
        return bits >= 8 ? value >> (bits - 8) : value * 255 / ((1 << bits) - 1);
    }

    private static (int Shift, int Bits) MaskParts(uint mask)
    {
        if (mask == 0)
            return (0, 0);
        int shift = 0;
        while ((mask & 1u) == 0)
        {
            mask >>= 1;
            shift++;
        }
        int bits = 0;
        while ((mask & 1u) != 0)
        {
            mask >>= 1;
            bits++;
        }
        return (shift, bits);
    }

    private static bool DecodeBlocks(byte[] dds, int dataOffset, int width, int height, Format format, out byte[] bgra)
    {
        bgra = [];
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int blockBytes = format == Format.Dxt1 ? 8 : 16;
        if (dataOffset + (long)blocksX * blocksY * blockBytes > dds.Length)
            return false;
        bgra = new byte[checked(width * height * 4)];
        byte[] pixelColors = new byte[64];
        byte[] pixelAlpha = new byte[16];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int blockStart = dataOffset + (by * blocksX + bx) * blockBytes;
                DecodeColorBlock(dds, blockStart, format != Format.Dxt1, pixelColors);
                if (format == Format.Dxt3)
                    DecodeDxt3Alpha(dds, blockStart, pixelAlpha);
                else if (format == Format.Dxt5)
                    DecodeDxt5Alpha(dds, blockStart, pixelAlpha);
                for (int py = 0; py < 4; py++)
                {
                    int pixelY = by * 4 + py;
                    if (pixelY >= height)
                        break;
                    for (int px = 0; px < 4; px++)
                    {
                        int pixelX = bx * 4 + px;
                        if (pixelX >= width)
                            break;
                        int pixelIndex = py * 4 + px;
                        int target = (pixelY * width + pixelX) * 4;
                        bgra[target] = pixelColors[pixelIndex * 4];
                        bgra[target + 1] = pixelColors[pixelIndex * 4 + 1];
                        bgra[target + 2] = pixelColors[pixelIndex * 4 + 2];
                        bgra[target + 3] = format == Format.Dxt1 ? pixelColors[pixelIndex * 4 + 3] : pixelAlpha[pixelIndex];
                    }
                }
            }
        }
        return true;
    }

    private static void DecodeColorBlock(byte[] dds, int blockStart, bool forceOpaquePalette, byte[] pixelsOut)
    {
        ushort c0 = BitConverter.ToUInt16(dds, blockStart);
        ushort c1 = BitConverter.ToUInt16(dds, blockStart + 2);
        Span<byte> colors = stackalloc byte[16];
        Expand565(c0, colors, 0);
        Expand565(c1, colors, 4);
        bool transparentMode = !forceOpaquePalette && c0 <= c1;
        for (int channel = 0; channel < 4; channel++)
        {
            byte a = colors[channel];
            byte b = colors[4 + channel];
            if (transparentMode)
            {
                colors[8 + channel] = (byte)((a + b) / 2);
                colors[12 + channel] = 0;
            }
            else
            {
                colors[8 + channel] = (byte)((a * 2 + b + 1) / 3);
                colors[12 + channel] = (byte)((a + b * 2 + 1) / 3);
            }
        }
        if (transparentMode)
        {
            colors[8 + 3] = 255;
            colors[15] = 0;
        }
        uint indices = BitConverter.ToUInt32(dds, blockStart + 4);
        for (int pixelIndex = 0; pixelIndex < 16; pixelIndex++)
        {
            uint code = (indices >> (pixelIndex * 2)) & 0x3u;
            int source = (int)code * 4;
            int target = pixelIndex * 4;
            pixelsOut[target] = colors[source];
            pixelsOut[target + 1] = colors[source + 1];
            pixelsOut[target + 2] = colors[source + 2];
            pixelsOut[target + 3] = colors[source + 3];
        }
    }

    private static void Expand565(ushort value, Span<byte> target, int offset)
    {
        int r = (value >> 11) & 0x1F;
        int g = (value >> 5) & 0x3F;
        int b = value & 0x1F;
        target[offset] = (byte)((b * 527 + 23) >> 6);
        target[offset + 1] = (byte)((g * 259 + 33) >> 6);
        target[offset + 2] = (byte)((r * 527 + 23) >> 6);
        target[offset + 3] = 255;
    }

    private static void DecodeDxt3Alpha(byte[] dds, int blockStart, byte[] alphaPalette)
    {
        for (int pixelIndex = 0; pixelIndex < 16; pixelIndex++)
        {
            int nibble = dds[blockStart + pixelIndex / 2];
            alphaPalette[pixelIndex] = (byte)((pixelIndex & 1) == 0 ? (nibble & 0xF) * 17 : (nibble >> 4) * 17);
        }
    }

    private static void DecodeDxt5Alpha(byte[] dds, int blockStart, byte[] alphaPalette)
    {
        byte a0 = dds[blockStart];
        byte a1 = dds[blockStart + 1];
        alphaPalette[0] = a0;
        alphaPalette[1] = a1;
        if (a0 > a1)
        {
            for (int i = 2; i < 8; i++)
                alphaPalette[i] = (byte)(((8 - i) * a0 + (i - 1) * a1) / 7);
        }
        else
        {
            for (int i = 2; i < 6; i++)
                alphaPalette[i] = (byte)(((6 - i) * a0 + (i - 1) * a1) / 5);
            alphaPalette[6] = 0;
            alphaPalette[7] = 255;
        }
        ulong codes = BitConverter.ToUInt64(dds, blockStart + 2);
        for (int pixelIndex = 0; pixelIndex < 16; pixelIndex++)
        {
            int code = (int)((codes >> (pixelIndex * 3)) & 0x7u);
            alphaPalette[pixelIndex] = alphaPalette[code];
        }
    }
}
