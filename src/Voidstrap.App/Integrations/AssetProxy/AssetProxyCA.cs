using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Voidstrap.Core.Networking;

namespace Voidstrap.Integrations.AssetProxy;

internal static partial class AssetProxyCA
{
	private const string LOG_IDENT = "AssetProxyCA";

	private const string CA_SUBJECT = "CN=Voidstrap Proxy CA";

	private const int CA_KEY_SIZE = 2048;

	private const int LEAF_KEY_SIZE = 2048;

	private static readonly string CaDirectory = Path.Combine(Paths.AssetProxy, "Certificates");

	private static readonly string CaPath = Path.Combine(CaDirectory, "proxy-ca.pfx");

	private const string CaPassword = "VoidstrapProxyCA";

	private static readonly byte[] CaProtectionEntropy = Encoding.UTF8.GetBytes("Voidstrap AssetWarp CA");

	private static readonly DateTimeOffset CaNotBefore = DateTimeOffset.UtcNow.AddDays(-1);

	private static readonly DateTimeOffset CaNotAfter = CaNotBefore.AddYears(10);

	private static readonly DateTimeOffset LeafNotBefore = DateTimeOffset.UtcNow.AddDays(-1);

	private static readonly DateTimeOffset LeafNotAfter = LeafNotBefore.AddYears(1);

	private static X509Certificate2? _rootCa;

	private static readonly List<X509Certificate2> _retiredCerts = [];

	private static readonly ConcurrentDictionary<string, X509Certificate2> _certCache = new(StringComparer.OrdinalIgnoreCase);

	private static readonly System.Threading.Lock _initLock = new();

	private static bool _initialized;

	[GeneratedRegex("-----BEGIN CERTIFICATE-----[\\s\\S]*?-----END CERTIFICATE-----", RegexOptions.CultureInvariant)]
	private static partial Regex CertificatePattern();

	public static X509Certificate2? RootCa => _rootCa;

	private static void SetRootCa(X509Certificate2? certificate)
	{
		X509Certificate2? previous = _rootCa;
		_rootCa = certificate;
		if (previous != null && !ReferenceEquals(previous, certificate))
		{
			try
			{
				previous.Dispose();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Previous CA handle could not be released: " + ex.Message);
			}
		}
	}

	private static void DisposeRetiredCerts()
	{
		foreach (X509Certificate2 certificate in _retiredCerts)
		{
			try
			{
				certificate.Dispose();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Retired certificate handle could not be released: " + ex.Message);
			}
		}
		_retiredCerts.Clear();
	}

	public static void Initialize(bool requireTrustBundle = true)
	{
		lock (_initLock)
		{
			if (_initialized && _rootCa != null)
				return;

			EnsureRootCAInstalled();
			PatchRobloxTrustBundles(requireTrustBundle);
			_initialized = true;
		}
	}

	private static void EnsureRootCAInstalled()
	{
		if (File.Exists(CaPath))
		{
			try
			{
				byte[] stored = File.ReadAllBytes(CaPath);
				byte[] pfx;
				bool migrated = false;
				try
				{
					pfx = ProtectedData.Unprotect(stored, CaProtectionEntropy, DataProtectionScope.CurrentUser);
				}
				catch (CryptographicException)
				{
					pfx = stored;
					migrated = true;
				}
				try
				{
					SetRootCa(X509CertificateLoader.LoadPkcs12(pfx, CaPassword, X509KeyStorageFlags.EphemeralKeySet));
					if (migrated)
						WriteProtectedPfx(pfx);
				}
				finally
				{
					if (!ReferenceEquals(pfx, stored))
						CryptographicOperations.ZeroMemory(pfx);
					CryptographicOperations.ZeroMemory(stored);
				}
				DateTime now = DateTime.UtcNow;
				if (!_rootCa.HasPrivateKey || now < _rootCa.NotBefore.ToUniversalTime() || now >= _rootCa.NotAfter.ToUniversalTime() || !string.Equals(_rootCa.Subject, CA_SUBJECT, StringComparison.OrdinalIgnoreCase))
				{
					SetRootCa(null);
					throw new InvalidDataException("Existing AssetWarp CA is invalid");
				}
				ImportToRootStore(_rootCa);
				App.Logger?.WriteLine(LOG_IDENT, "Loaded existing CA certificate");
				return;
			}
			catch
			{
				App.Logger?.WriteLine(LOG_IDENT, "Existing CA file corrupt, regenerating");
			}
		}

		GenerateRootCA();
	}

	private static void GenerateRootCA()
	{
		using RSA rsa = RSA.Create(CA_KEY_SIZE);

		CertificateRequest req = new(CA_SUBJECT, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

		req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
		req.CertificateExtensions.Add(new X509KeyUsageExtension(
			X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
		req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

		X509Certificate2 cert = req.CreateSelfSigned(CaNotBefore, CaNotAfter);

		SetRootCa(cert);

		Directory.CreateDirectory(CaDirectory);

		byte[] pfx = cert.Export(X509ContentType.Pfx, CaPassword);
		try
		{
			WriteProtectedPfx(pfx);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(pfx);
		}

		ImportToRootStore(cert);

		App.Logger?.WriteLine(LOG_IDENT, "Generated new CA certificate");
	}

	private static void WriteProtectedPfx(byte[] pfx)
	{
		byte[] protectedPfx = ProtectedData.Protect(pfx, CaProtectionEntropy, DataProtectionScope.CurrentUser);
		try
		{
			string temporary = CaPath + ".tmp";
			File.WriteAllBytes(temporary, protectedPfx);
			File.Move(temporary, CaPath, true);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(protectedPfx);
		}
	}

	private static void ImportToRootStore(X509Certificate2 cert)
	{
		try
		{
			using X509Store store = new(StoreName.Root, StoreLocation.CurrentUser);
			store.Open(OpenFlags.ReadWrite);

			bool alreadyExists = false;
			X509Certificate2Collection present = store.Certificates;
			try
			{
				foreach (X509Certificate2 existing in present)
				{
					if (existing.Thumbprint == cert.Thumbprint)
					{
						alreadyExists = true;
					}
					else if (string.Equals(existing.Subject, CA_SUBJECT, StringComparison.OrdinalIgnoreCase))
					{
						store.Remove(existing);
					}
				}
			}
			finally
			{
				foreach (X509Certificate2 existing in present)
				{
					existing.Dispose();
				}
			}

			if (!alreadyExists)
				store.Add(cert);

			store.Close();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, $"Root store import failed: {ex.Message}");
			throw;
		}
	}

	public static X509Certificate2 GenerateHostCert(string hostname)
	{
		lock (_initLock)
		{
			if (_rootCa == null)
				return CreateSelfSignedFallback(hostname);
			if (_certCache.TryGetValue(hostname, out X509Certificate2? cached))
				return cached;
			using RSA rsa = RSA.Create(LEAF_KEY_SIZE);
			CertificateRequest req = new($"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
			req.CertificateExtensions.Add(new X509KeyUsageExtension(
				X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
			req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
				[new("1.3.6.1.5.5.7.3.1")], false));
			SubjectAlternativeNameBuilder sanBuilder = new();
			if (IPAddress.TryParse(hostname, out IPAddress? address))
				sanBuilder.AddIpAddress(address);
			else
				sanBuilder.AddDnsName(hostname);
			req.CertificateExtensions.Add(sanBuilder.Build());
			byte[] serial = RandomNumberGenerator.GetBytes(16);
			using X509Certificate2 issued = req.Create(_rootCa, LeafNotBefore, LeafNotAfter, serial);
			using X509Certificate2 ephemeralLeaf = issued.CopyWithPrivateKey(rsa);
			X509Certificate2 leafCert = AssetProxyTlsOptions.CreateServerCertificate(ephemeralLeaf);
			_certCache[hostname] = leafCert;
			return leafCert;
		}
	}

	private static void PatchRobloxTrustBundles(bool requireTrustBundle)
	{
		if (_rootCa == null)
		{
			throw new InvalidOperationException("AssetWarp CA is unavailable");
		}

		string caPem = _rootCa.ExportCertificatePem().Trim();
		HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
		AddVersionDirectories(directories, Paths.Versions);
		AddVersionDirectories(directories, Path.Combine(Paths.LocalAppData, "Roblox", "Versions"));

		int patched = 0;
		int failed = 0;
		foreach (string directory in directories)
		{
			string bundle = Path.Combine(directory, "ssl", "cacert.pem");
			if (!File.Exists(bundle))
			{
				continue;
			}
			try
			{
				PatchBundle(bundle, caPem);
				patched++;
			}
			catch (Exception ex)
			{
				failed++;
				App.Logger?.WriteLine(LOG_IDENT, "Trust bundle could not be updated for " + directory + ": " + ex.Message);
			}
		}

		App.Logger?.WriteLine(LOG_IDENT, "Roblox trust bundles ready: " + patched + ", not updated: " + failed);
		if (patched > 0)
		{
			return;
		}
		if (failed > 0)
		{
			throw new IOException("The Roblox certificate bundle could not be updated");
		}
		if (requireTrustBundle)
		{
			throw new IOException("No Roblox certificate bundle was found, AssetWarp cannot intercept asset delivery");
		}
		App.Logger?.WriteLine(LOG_IDENT, "No Roblox certificate bundle is present yet, it will be patched when Roblox launches");
	}

	private static void AddVersionDirectories(HashSet<string> directories, string root)
	{
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
		{
			return;
		}

		if (File.Exists(Path.Combine(root, "RobloxPlayerBeta.exe")))
		{
			directories.Add(root);
		}

		foreach (string directory in Directory.EnumerateDirectories(root))
		{
			if (File.Exists(Path.Combine(directory, "RobloxPlayerBeta.exe")))
			{
				directories.Add(directory);
			}
		}
	}

	private static void PatchBundle(string path, string caPem)
	{
		string original = File.ReadAllText(path);
		MatchCollection matches = CertificatePattern().Matches(original);
		StringBuilder cleaned = new(original.Length + caPem.Length + 4);
		int cursor = 0;
		foreach (Match match in matches)
		{
			cleaned.Append(original, cursor, match.Index - cursor);
			if (!IsVoidstrapCertificate(match.Value))
			{
				cleaned.Append(match.Value);
			}
			cursor = match.Index + match.Length;
		}
		cleaned.Append(original, cursor, original.Length - cursor);
		string updated = cleaned.ToString().TrimEnd() + Environment.NewLine + caPem + Environment.NewLine;
		if (string.Equals(original.Replace("\r\n", "\n"), updated.Replace("\r\n", "\n"), StringComparison.Ordinal))
		{
			return;
		}

		FileAttributes attributes = File.GetAttributes(path);
		bool readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
		if (readOnly)
		{
			File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
		}
		try
		{
			string temporary = path + ".voidstrap.tmp";
			File.WriteAllText(temporary, updated, new UTF8Encoding(false));
			File.Move(temporary, path, true);
		}
		finally
		{
			if (readOnly && File.Exists(path))
			{
				File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
			}
		}
	}

	private static bool IsVoidstrapCertificate(string pem)
	{
		try
		{
			using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
			return certificate.Subject.Contains(CA_SUBJECT, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static X509Certificate2 CreateSelfSignedFallback(string hostname)
	{
		using RSA rsa = RSA.Create(LEAF_KEY_SIZE);

		CertificateRequest req = new($"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

		req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
		req.CertificateExtensions.Add(new X509KeyUsageExtension(
			X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
		req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
			[new("1.3.6.1.5.5.7.3.1")], false));

		SubjectAlternativeNameBuilder sanBuilder = new();
		sanBuilder.AddDnsName(hostname);
		req.CertificateExtensions.Add(sanBuilder.Build());

		using X509Certificate2 ephemeralLeaf = req.CreateSelfSigned(LeafNotBefore, LeafNotAfter);
		return AssetProxyTlsOptions.CreateServerCertificate(ephemeralLeaf);
	}

	public static void Cleanup()
	{
		lock (_initLock)
		{
			DisposeRetiredCerts();
			foreach (X509Certificate2 cert in _certCache.Values)
			{
				DeletePersistedKey(cert);
				_retiredCerts.Add(cert);
			}
			_certCache.Clear();
			SetRootCa(null);
			_initialized = false;
		}
	}

	private static void DeletePersistedKey(X509Certificate2 certificate)
	{
		if (!OperatingSystem.IsWindows())
			return;
		try
		{
			using RSA? key = certificate.GetRSAPrivateKey();
			if (key is RSACng cng)
			{
				cng.Key.Delete();
			}
			else if (key is RSACryptoServiceProvider csp)
			{
				csp.PersistKeyInCsp = false;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "A leaf certificate private key could not be deleted: " + ex.Message);
		}
	}

	public static void RemoveOutdatedCertificates(bool preserveCurrent)
	{
		Cleanup();
		lock (_initLock)
		{
			string? keepThumbprint = preserveCurrent ? GetPersistedCertificateThumbprint() : null;
			try
			{
				UnpatchRobloxTrustBundles(keepThumbprint);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LOG_IDENT, "Trust bundle cleanup failed: " + ex.Message);
			}
			RemoveFromRootStore(keepThumbprint);
			if (keepThumbprint == null)
			{
				try
				{
					if (File.Exists(CaPath))
						File.Delete(CaPath);
				}
				catch (Exception ex)
				{
					App.Logger?.WriteLine(LOG_IDENT, "Certificate data cleanup failed: " + ex.Message);
				}
			}
		}
	}

	private static string? GetPersistedCertificateThumbprint()
	{
		if (!File.Exists(CaPath))
			return null;
		byte[] stored = File.ReadAllBytes(CaPath);
		byte[]? pfx = null;
		try
		{
			try
			{
				pfx = ProtectedData.Unprotect(stored, CaProtectionEntropy, DataProtectionScope.CurrentUser);
			}
			catch (CryptographicException)
			{
				pfx = (byte[])stored.Clone();
			}
			using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(pfx, CaPassword, X509KeyStorageFlags.EphemeralKeySet);
			DateTime now = DateTime.UtcNow;
			if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime() || !string.Equals(certificate.Subject, CA_SUBJECT, StringComparison.OrdinalIgnoreCase))
				return null;
			return certificate.Thumbprint;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (pfx != null)
				CryptographicOperations.ZeroMemory(pfx);
			CryptographicOperations.ZeroMemory(stored);
		}
	}

	private static void UnpatchRobloxTrustBundles(string? keepThumbprint)
	{
		HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
		AddVersionDirectories(directories, Paths.Versions);
		AddVersionDirectories(directories, Path.Combine(Paths.LocalAppData, "Roblox", "Versions"));
		foreach (string directory in directories)
		{
			string bundle = Path.Combine(directory, "ssl", "cacert.pem");
			if (!File.Exists(bundle))
				continue;
			RemoveFromBundle(bundle, keepThumbprint);
		}
	}

	private static void RemoveFromBundle(string path, string? keepThumbprint)
	{
		string original = File.ReadAllText(path);
		MatchCollection matches = CertificatePattern().Matches(original);
		StringBuilder cleaned = new(original.Length);
		int cursor = 0;
		bool removed = false;
		foreach (Match match in matches)
		{
			cleaned.Append(original, cursor, match.Index - cursor);
			string? thumbprint = GetVoidstrapCertificateThumbprint(match.Value);
			if (thumbprint == null || string.Equals(thumbprint, keepThumbprint, StringComparison.OrdinalIgnoreCase))
				cleaned.Append(match.Value);
			else
				removed = true;
			cursor = match.Index + match.Length;
		}
		if (!removed)
			return;
		cleaned.Append(original, cursor, original.Length - cursor);
		FileAttributes attributes = File.GetAttributes(path);
		bool readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
		if (readOnly)
			File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
		try
		{
			string temporary = path + ".voidstrap.tmp";
			File.WriteAllText(temporary, cleaned.ToString().TrimEnd() + Environment.NewLine, new UTF8Encoding(false));
			File.Move(temporary, path, true);
		}
		finally
		{
			if (readOnly && File.Exists(path))
				File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
		}
	}

	private static string? GetVoidstrapCertificateThumbprint(string pem)
	{
		try
		{
			using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
			return string.Equals(certificate.Subject, CA_SUBJECT, StringComparison.OrdinalIgnoreCase) ? certificate.Thumbprint : null;
		}
		catch
		{
			return null;
		}
	}

	private static void RemoveFromRootStore(string? keepThumbprint)
	{
		try
		{
			using X509Store store = new(StoreName.Root, StoreLocation.CurrentUser);
			store.Open(OpenFlags.ReadWrite);
			X509Certificate2Collection present = store.Certificates;
			try
			{
				foreach (X509Certificate2 existing in present)
				{
					if (string.Equals(existing.Subject, CA_SUBJECT, StringComparison.OrdinalIgnoreCase) && !string.Equals(existing.Thumbprint, keepThumbprint, StringComparison.OrdinalIgnoreCase))
						store.Remove(existing);
				}
			}
			finally
			{
				foreach (X509Certificate2 existing in present)
				{
					existing.Dispose();
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Root store cleanup failed: " + ex.Message);
		}
	}
}
