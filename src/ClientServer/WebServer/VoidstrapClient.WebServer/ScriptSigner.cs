using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VoidstrapClient.Common;
using VoidstrapClient.WebServer.Enums;

namespace VoidstrapClient.WebServer;

internal static class ScriptSigner
{
	private static readonly RSA _RSA;
	private static readonly object SignGate = new object();

	private static string GetSignatureFormat()
	{
		return Config.Instance.Client.Signature switch
		{
			SignatureType.None => string.Empty,
			SignatureType.Legacy => "%{0}%",
			SignatureType.RbxSig => "--rbxsig%{0}%",
			SignatureType.RbxSig2 => "--rbxsig2%{0}%",
			SignatureType.RbxSig4 => "--rbxsig4%{0}%",
			_ => throw new Exception($"Unhandled signature type {Config.Instance.Client.Signature}"),
		};
	}

	private static string GetPreSignScriptFormat(bool includeAssetId)
	{
		if (!includeAssetId)
		{
			return "\r\n{0}";
		}
		return Config.Instance.Client.Signature switch
		{
			SignatureType.None => string.Empty,
			SignatureType.Legacy => "%{1}%\r\n{0}",
			SignatureType.RbxSig => "\r\n--rbxassetid%{1}%\r\n{0}",
			SignatureType.RbxSig2 => "\r\n--rbxassetid2%{1}%\r\n{0}",
			SignatureType.RbxSig4 => "\r\n--rbxassetid4%{1}%\r\n{0}",
			_ => throw new Exception($"Unhandled signature type {Config.Instance.Client.Signature}"),
		};
	}

	private static HashAlgorithmName GetHashAlgorithm()
	{
		return Config.Instance.Client.Signature switch
		{
			SignatureType.RbxSig4 => HashAlgorithmName.SHA256,
			_ => HashAlgorithmName.SHA1,
		};
	}

	public static string Sign(string script, ulong assetId = 0uL)
	{
		if (Config.Instance.Client.Signature == SignatureType.None)
		{
			return script;
		}
		string signatureFormat = GetSignatureFormat();
		string preSignScriptFormat = GetPreSignScriptFormat(assetId != 0);
		script = string.Format(preSignScriptFormat, script, assetId);
		byte[] inArray;
		lock (SignGate)
		{
			inArray = _RSA.SignData(Encoding.Default.GetBytes(script), GetHashAlgorithm(), RSASignaturePadding.Pkcs1);
		}
		return string.Format(signatureFormat, Convert.ToBase64String(inArray)) + script;
	}

	static ScriptSigner()
	{
		string path = Path.Combine(PathHelper.Data, "PrivateKey.pem");
		string text = Config.ReadTextFile(path, 1048576);
		_RSA = RSA.Create();
		_RSA.ImportFromPem(text);
	}
}
