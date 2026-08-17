using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voidstrap.Utility
{
    public sealed class AuthAccount
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Avatar { get; set; } = "";
    }

    public static class WebsiteAuth
    {
        private static readonly object Sync = new object();

        private const string WindowsTokenPrefix = "dpapi:";

        private const string PortableTokenPrefix = "aesgcm:";

        private static readonly byte[] PortableEntropy = Encoding.UTF8.GetBytes("Voidstrap.WebsiteAuth.v1");

        public static event Action? Changed;

        public static void Notify()
        {
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch
            {
            }
        }

        private static string FilePath => Path.Combine(Paths.DocumentsData, "WebsiteAuth.json");

        private static string PortableKeyPath => Path.Combine(Paths.DocumentsData, "WebsiteAuth.key");

        private sealed class StoredAccount
        {
            public string id { get; set; } = "";
            public string label { get; set; } = "";
            public string avatar { get; set; } = "";
            public string token { get; set; } = "";
        }

        private sealed class Store
        {
            public int v { get; set; } = 2;
            public string active { get; set; } = "";
            public List<StoredAccount> accounts { get; set; } = new List<StoredAccount>();
        }

        private static Store Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new Store();

				JsonElement root = JsonFile.Deserialize<JsonElement>(FilePath, JsonOptions.Tolerant, 8388608);

                if (root.TryGetProperty("accounts", out _) || root.TryGetProperty("v", out _))
                {
					var parsed = JsonFile.Deserialize<Store>(FilePath, JsonOptions.Tolerant, 8388608);

                    bool migrated = false;
                    foreach (var account in parsed.accounts)
                    {
                        string? token = DecodeToken(account.token);
                        if (token == null || account.id != token)
                            continue;

                        string oldId = account.id;
                        account.id = NewPendingId();
                        if (parsed.active == oldId)
                            parsed.active = account.id;
                        migrated = true;
                    }

                    if (migrated)
                        Persist(parsed);

                    return parsed;
                }

                if (root.TryGetProperty("token", out var legacyTok) && legacyTok.ValueKind == JsonValueKind.String)
                {
                    string raw = legacyTok.GetString() ?? "";
                    bool isProtected = root.TryGetProperty("dpapi", out var d) && d.ValueKind == JsonValueKind.True;
                    if (raw.Length > 0)
                    {
                        string encoded = isProtected ? raw : EncodeToken(raw);
                        var s = new Store { active = "legacy", accounts = new List<StoredAccount> { new StoredAccount { id = "legacy", label = "Account", token = encoded } } };
                        Persist(s);
                        return s;
                    }
                }
                return new Store();
            }
            catch
            {
                return new Store();
            }
        }

        private static void Persist(Store store)
        {
            try
            {
				JsonFile.SerializeAtomic(FilePath, store);
                RestrictFile(FilePath);
            }
            catch { }
        }

        private static string EncodeToken(string token)
        {
            byte[] plain = Encoding.UTF8.GetBytes(token);
            try
            {
                if (Platform.IsWindows)
                {
                    byte[]? blob = Protect(plain);
                    return blob == null ? "" : WindowsTokenPrefix + Convert.ToBase64String(blob);
                }
                return PortableTokenPrefix + Convert.ToBase64String(ProtectPortable(plain));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        private static string? DecodeToken(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return null;
            try
            {
                byte[]? plain;
                if (encoded.StartsWith(WindowsTokenPrefix, StringComparison.Ordinal))
                {
                    if (!Platform.IsWindows)
                        return null;
                    plain = Unprotect(Convert.FromBase64String(encoded.Substring(WindowsTokenPrefix.Length)));
                }
                else if (encoded.StartsWith(PortableTokenPrefix, StringComparison.Ordinal))
                {
                    plain = UnprotectPortable(Convert.FromBase64String(encoded.Substring(PortableTokenPrefix.Length)));
                }
                else
                {
                    if (!Platform.IsWindows)
                        return null;
                    plain = Unprotect(Convert.FromBase64String(encoded));
                }
                if (plain == null)
                    return null;
                try
                {
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch
            {
                return null;
            }
        }

        private static string NewPendingId() => $"pending_{Guid.NewGuid():N}";

        public static void Save(string token)
        {
            AddOrUpdateAccount(token, "", "", "");
        }

        public static void AddOrUpdateAccount(string token, string id, string label, string avatar)
        {
            bool changed;
            lock (Sync)
            {
                if (string.IsNullOrWhiteSpace(token))
                    return;
                string encoded = EncodeToken(token.Trim());
                if (encoded.Length == 0)
                    return;
                var store = Load();
                string rawToken = token.Trim();
                var existing = store.accounts.FirstOrDefault(a => DecodeToken(a.token) == rawToken);
                string key = string.IsNullOrEmpty(id) ? existing?.id ?? NewPendingId() : id;
                existing ??= store.accounts.FirstOrDefault(a => a.id == key);
                store.accounts.RemoveAll(a => a != existing && (a.id == key || DecodeToken(a.token) == token.Trim()));
                bool isNew = existing == null;
                if (existing == null)
                {
                    existing = new StoredAccount { id = key };
                    store.accounts.Add(existing);
                }
                bool tokenChanged = DecodeToken(existing.token) != token.Trim();
                bool activeChanged = store.active != key;
                existing.id = key;
                existing.token = encoded;
                if (!string.IsNullOrEmpty(label)) existing.label = label;
                if (!string.IsNullOrEmpty(avatar)) existing.avatar = avatar;
                store.active = key;
                Persist(store);
                changed = isNew || tokenChanged || activeChanged;
            }
            if (changed)
                RaiseChanged();
        }

        public static string? GetToken()
        {
            lock (Sync)
            {
                var store = Load();
                var active = store.accounts.FirstOrDefault(a => a.id == store.active) ?? store.accounts.FirstOrDefault();
                return active == null ? null : DecodeToken(active.token);
            }
        }

        public static List<AuthAccount> GetAccounts()
        {
            lock (Sync)
            {
                return Load().accounts
                    .Where(a => !string.IsNullOrEmpty(a.id) && (a.id.All(char.IsDigit) || !string.IsNullOrEmpty(a.label)))
                    .GroupBy(a => a.id)
                    .Select(g => g.First())
                    .Select(a => new AuthAccount { Id = a.id, Label = string.IsNullOrEmpty(a.label) ? a.id : a.label, Avatar = a.avatar })
                    .ToList();
            }
        }

        public static string? GetActiveId()
        {
            lock (Sync)
            {
                return Load().active;
            }
        }

        public static bool SetActive(string id)
        {
            lock (Sync)
            {
                var store = Load();
                if (!store.accounts.Any(a => a.id == id))
                    return false;
                store.active = id;
                Persist(store);
            }
            RaiseChanged();
            return true;
        }

        public static void RemoveAccount(string id)
        {
            lock (Sync)
            {
                var store = Load();
                store.accounts.RemoveAll(a => a.id == id);
                if (store.active == id)
                    store.active = store.accounts.FirstOrDefault()?.id ?? "";
                Persist(store);
            }
            RaiseChanged();
        }

        internal static string ProtectString(string plain) => EncodeToken(plain);

        internal static string? UnprotectString(string encoded) => DecodeToken(encoded);

        public static bool IsSignedIn() => !string.IsNullOrEmpty(GetToken());

        public static void Clear()
        {
            lock (Sync)
            {
                var store = Load();
                if (!string.IsNullOrEmpty(store.active))
                {
                    store.accounts.RemoveAll(a => a.id == store.active);
                    store.active = store.accounts.FirstOrDefault()?.id ?? "";
                    Persist(store);
                }
                if (store.accounts.Count == 0)
                {
                    try
                    {
                        if (File.Exists(FilePath))
                            File.Delete(FilePath);
                    }
                    catch { }
                }
            }
            RaiseChanged();
        }

        public static void ClearAll()
        {
            lock (Sync)
            {
                try
                {
                    if (File.Exists(FilePath))
                        File.Delete(FilePath);
                }
                catch { }
            }
            RaiseChanged();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[]? Protect(byte[] data)
        {
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            try
            {
                inBlob.cbData = data.Length;
                inBlob.pbData = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, inBlob.pbData, data.Length);

                if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    return null;

                byte[] outData = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outData, 0, outBlob.cbData);
                return outData;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static byte[]? Unprotect(byte[] data)
        {
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            try
            {
                inBlob.cbData = data.Length;
                inBlob.pbData = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, inBlob.pbData, data.Length);

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    return null;

                byte[] outData = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outData, 0, outBlob.cbData);
                return outData;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static byte[] ProtectPortable(byte[] data)
        {
            byte[] key = GetPortableKey();
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] cipher = new byte[data.Length];
            try
            {
                using AesGcm aes = new AesGcm(key, tag.Length);
                aes.Encrypt(nonce, data, cipher, tag, PortableEntropy);
                byte[] output = new byte[nonce.Length + tag.Length + cipher.Length];
                Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
                Buffer.BlockCopy(cipher, 0, output, nonce.Length + tag.Length, cipher.Length);
                return output;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(cipher);
            }
        }

        private static byte[] UnprotectPortable(byte[] data)
        {
            if (data.Length < 28)
                throw new CryptographicException();
            byte[] key = GetPortableKey();
            byte[] plain = new byte[data.Length - 28];
            try
            {
                using AesGcm aes = new AesGcm(key, 16);
                aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, PortableEntropy);
                return plain;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plain);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        private static byte[] GetPortableKey()
        {
            lock (Sync)
            {
                if (!File.Exists(PortableKeyPath))
                {
                    Directory.CreateDirectory(Paths.Config);
                    byte[] generated = RandomNumberGenerator.GetBytes(32);
                    string temporaryPath = PortableKeyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(temporaryPath, generated);
                        RestrictFile(temporaryPath);
                        try
                        {
                            File.Move(temporaryPath, PortableKeyPath, false);
                        }
                        catch (IOException) when (File.Exists(PortableKeyPath))
                        {
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(generated);
                        try
                        {
                            if (File.Exists(temporaryPath))
                                File.Delete(temporaryPath);
                        }
                        catch
                        {
                        }
                    }
                }
                byte[] key = File.ReadAllBytes(PortableKeyPath);
                if (key.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new CryptographicException();
                }
                RestrictFile(PortableKeyPath);
                return key;
            }
        }

        private static void RestrictFile(string path)
        {
            if (Platform.IsWindows)
                return;
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
