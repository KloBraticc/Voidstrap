using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Voidstrap.Core.Networking;

public static class AssetProxyTlsOptions
{
	public static X509Certificate2 CreateServerCertificate(X509Certificate2 certificate)
	{
		byte[] data = certificate.Export(X509ContentType.Pfx);
		try
		{
			return X509CertificateLoader.LoadPkcs12(data, null, X509KeyStorageFlags.UserKeySet);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(data);
		}
	}

	public static SslServerAuthenticationOptions Create(ServerCertificateSelectionCallback certificateSelector)
	{
		return new SslServerAuthenticationOptions
		{
			ServerCertificateSelectionCallback = certificateSelector,
			EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
			ClientCertificateRequired = false,
			ApplicationProtocols = [SslApplicationProtocol.Http11]
		};
	}
}
