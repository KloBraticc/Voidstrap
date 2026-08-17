using System;
using System.Text;

namespace Voidstrap.Platform.Windows.Integrations.AssetProxy
{
    public static class RobloxMetadata
    {
        private static readonly byte[] RbxhPrefix = new byte[25]
        {
            82, 66, 88, 72, 2, 0, 0, 0, 0, 0,
            0, 0, 0, 200, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0
        };

        public static byte[] StripRobloxMetadata(string filePath, byte[] data)
        {
            return StripRobloxMetadata(data);
        }

        public static byte[] StripRobloxMetadata(byte[]? data)
        {
            if (data == null)
                return [];

            if (data.Length < RbxhPrefix.Length)
                return data;

            for (int i = 0; i < RbxhPrefix.Length; i++)
            {
                if (data[i] != RbxhPrefix[i])
                    return data;
            }

            if (data.Length < RbxhPrefix.Length + 12)
                return data;

            int contentLength = BitConverter.ToInt32(data, RbxhPrefix.Length);
            int payloadOffset = RbxhPrefix.Length + 12;

            if (contentLength >= 0 && contentLength <= data.Length - payloadOffset)
            {
                byte[] content = new byte[contentLength];
                Array.Copy(data, payloadOffset, content, 0, contentLength);
                return content;
            }

            return data;
        }

        public static byte[] WrapRbxh(byte[] content)
        {
            ArgumentNullException.ThrowIfNull(content);
            int payloadOffset = RbxhPrefix.Length + 12;
            byte[] result = new byte[payloadOffset + content.Length];
            RbxhPrefix.CopyTo(result, 0);
            BitConverter.TryWriteBytes(result.AsSpan(RbxhPrefix.Length, 4), content.Length);
            uint crc = ComputeCrc32(content);
            BitConverter.TryWriteBytes(result.AsSpan(RbxhPrefix.Length + 4, 4), crc);
            content.CopyTo(result, payloadOffset);
            return result;
        }

        private static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFF;
        }

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                {
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                }
                table[i] = c;
            }
            return table;
        }
    }
}
