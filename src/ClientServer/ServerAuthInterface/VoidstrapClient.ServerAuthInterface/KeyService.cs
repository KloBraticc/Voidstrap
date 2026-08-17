using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace VoidstrapClient.ServerAuthInterface;

internal class KeyService
{
	private const int MaxKeys = 1024;
	private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

	private class KeyData
	{
		public byte[] Key { get; set; } = Array.Empty<byte>();

		public bool Infinite { get; set; }
	}

	private List<KeyData> _Keys = new List<KeyData>();

	public static KeyService Instance { get; } = new KeyService();

	private string RandomString(int length)
	{
		char[] value = new char[length];
		for (int index = 0; index < value.Length; index++)
		{
			value[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
		}
		return new string(value);
	}

	public string GenerateKey(bool infinite)
	{
		string text = "ORRHServerAuthKey-";
		text += RandomString(64);
		KeyData keyData = new KeyData();
		keyData.Key = Encoding.UTF8.GetBytes(text);
		keyData.Infinite = infinite;
		lock (_Keys)
		{
			if (_Keys.Count >= MaxKeys)
			{
				CryptographicOperations.ZeroMemory(_Keys[0].Key);
				_Keys.RemoveAt(0);
			}
			_Keys.Add(keyData);
		}
		return text;
	}

	public bool ValidateProofThenInvalidateKey(string challenge, string encodedProof)
	{
		if (encodedProof.Length != 44)
		{
			return false;
		}
		byte[] proof;
		try
		{
			proof = Convert.FromBase64String(encodedProof);
		}
		catch (FormatException)
		{
			return false;
		}
		if (proof.Length != 32)
		{
			return false;
		}
		byte[] challengeBytes = Encoding.UTF8.GetBytes(challenge);
		lock (_Keys)
		{
			for (int index = 0; index < _Keys.Count; index++)
			{
				KeyData keyData = _Keys[index];
				byte[] expected = HMACSHA256.HashData(keyData.Key, challengeBytes);
				bool matches = CryptographicOperations.FixedTimeEquals(expected, proof);
				CryptographicOperations.ZeroMemory(expected);
				if (!matches)
				{
					continue;
				}
				if (!keyData.Infinite)
				{
					_Keys.RemoveAt(index);
					CryptographicOperations.ZeroMemory(keyData.Key);
				}
				CryptographicOperations.ZeroMemory(challengeBytes);
				CryptographicOperations.ZeroMemory(proof);
				return true;
			}
		}
		CryptographicOperations.ZeroMemory(challengeBytes);
		CryptographicOperations.ZeroMemory(proof);
		return false;
	}
}
