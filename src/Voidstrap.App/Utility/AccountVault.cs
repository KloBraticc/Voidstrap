using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Voidstrap.Utility;

public static class AccountVault
{
    public const int MinimumPasswordLength = 12;

    public const string Extension = ".vsaccount";

    private const int MemoryKiB = 256 * 1024;

    private const int Iterations = 3;

    private const int Parallelism = 4;

    private const int KeyBytes = 32;

    private const int SaltBytes = 32;

    private const int NonceBytes = 12;

    private const int TagBytes = 16;

    private const int MaximumFileBytes = 64 * 1024;

    private const int HeaderBytes = 4 + 4 + 4 + 4 + SaltBytes + NonceBytes;

    private static ReadOnlySpan<byte> Magic => "VSA1"u8;

    public static byte[] Seal(string cookie, string password)
    {
        if (password.Length < MinimumPasswordLength)
            throw new ArgumentException("The password is too short.", nameof(password));
        byte[] plain = Encoding.UTF8.GetBytes(cookie);
        byte[] output = new byte[HeaderBytes + TagBytes + plain.Length];
        byte[] key = [];
        try
        {
            Span<byte> header = output.AsSpan(0, HeaderBytes);
            Magic.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], MemoryKiB);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], Iterations);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], Parallelism);
            RandomNumberGenerator.Fill(header.Slice(16, SaltBytes + NonceBytes));
            key = DeriveKey(password, header.Slice(16, SaltBytes).ToArray());
            using AesGcm aes = new(key, TagBytes);
            aes.Encrypt(
                header.Slice(16 + SaltBytes, NonceBytes),
                plain,
                output.AsSpan(HeaderBytes + TagBytes),
                output.AsSpan(HeaderBytes, TagBytes),
                header
            );
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static string? TryOpen(byte[] file, string password)
    {
        if (file.Length <= HeaderBytes + TagBytes || file.Length > MaximumFileBytes || !file.AsSpan(0, 4).SequenceEqual(Magic))
            return null;
        ReadOnlySpan<byte> header = file.AsSpan(0, HeaderBytes);
        if (
            BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != MemoryKiB
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != Iterations
            || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != Parallelism
        )
            return null;
        byte[] plain = new byte[file.Length - HeaderBytes - TagBytes];
        byte[] key = DeriveKey(password, header.Slice(16, SaltBytes).ToArray());
        try
        {
            using AesGcm aes = new(key, TagBytes);
            aes.Decrypt(
                header.Slice(16 + SaltBytes, NonceBytes),
                file.AsSpan(HeaderBytes + TagBytes),
                file.AsSpan(HeaderBytes, TagBytes),
                plain,
                header
            );
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        byte[] secret = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try
        {
            using Argon2id argon = new(secret)
            {
                Salt = salt,
                MemorySize = MemoryKiB,
                Iterations = Iterations,
                DegreeOfParallelism = Parallelism,
            };
            return argon.GetBytes(KeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
