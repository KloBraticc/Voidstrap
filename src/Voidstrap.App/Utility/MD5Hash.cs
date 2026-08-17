using System;
using System.IO;
using System.Security.Cryptography;

namespace Voidstrap.Utility;

public static class MD5Hash
{
	public static string FromBytes(byte[] data)
	{
		using MD5 mD = MD5.Create();
		return Stringify(mD.ComputeHash(data));
	}

	public static string FromStream(Stream stream)
	{
		stream.Seek(0L, SeekOrigin.Begin);
		using MD5 mD = MD5.Create();
		return Stringify(mD.ComputeHash(stream));
	}

	public static string FromFile(string filename)
	{
		using FileStream stream = new(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
		using MD5 md5 = MD5.Create();
		return Stringify(md5.ComputeHash(stream));
	}

	public static string Stringify(byte[] hash)
	{
		return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
	}
}
