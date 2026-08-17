using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

internal static class Common
{
	public const string BodyColorsFormat = "<?xml version=\"1.0\" encoding=\"utf-8\"?><roblox xmlns:xmime=\"http://www.w3.org/2005/05/xmlmime\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"http://www.roblox.com/roblox.xsd\" version=\"4\"><External>null</External><External>nil</External><Item class=\"BodyColors\"><Properties><int name=\"HeadColor\">{0}</int><int name=\"LeftArmColor\">{1}</int><int name=\"LeftLegColor\">{2}</int><string name=\"Name\">Body Colors</string><int name=\"RightArmColor\">{3}</int><int name=\"RightLegColor\">{4}</int><int name=\"TorsoColor\">{5}</int><bool name=\"archivable\">true</bool></Properties></Item></roblox>";

	public static List<string> AssetPackDirectories { get; }

	public static HttpClient HttpClient { get; }

	public static CookieContainer CookieContainerAssetDelivery { get; }

	public static HttpClient HttpClientAssetDelivery { get; }

	public static string FormatBodyColors(int head, int leftArm, int leftLeg, int rightArm, int rightLeg, int torso)
	{
		return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><roblox xmlns:xmime=\"http://www.w3.org/2005/05/xmlmime\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"http://www.roblox.com/roblox.xsd\" version=\"4\"><External>null</External><External>nil</External><Item class=\"BodyColors\"><Properties><int name=\"HeadColor\">{head}</int><int name=\"LeftArmColor\">{leftArm}</int><int name=\"LeftLegColor\">{leftLeg}</int><string name=\"Name\">Body Colors</string><int name=\"RightArmColor\">{rightArm}</int><int name=\"RightLegColor\">{rightLeg}</int><int name=\"TorsoColor\">{torso}</int><bool name=\"archivable\">true</bool></Properties></Item></roblox>";
	}

	public static byte[] CompressGzip(ReadOnlySpan<byte> data)
	{
		using MemoryStream output = new MemoryStream();
		using (GZipStream gzip = new GZipStream(output, CompressionLevel.Optimal, true))
		{
			gzip.Write(data);
		}

		return output.ToArray();
	}

	static Common()
	{
		HttpClient = new HttpClient(new SocketsHttpHandler
		{
			AutomaticDecompression = DecompressionMethods.All,
			UseProxy = true,
			Proxy = null,
			DefaultProxyCredentials = CredentialCache.DefaultCredentials,
			ConnectTimeout = TimeSpan.FromSeconds(10),
			PooledConnectionLifetime = TimeSpan.FromMinutes(2),
			PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15)
		});
		CookieContainerAssetDelivery = new CookieContainer();
		HttpClientAssetDelivery = new HttpClient(new SocketsHttpHandler
		{
			AutomaticDecompression = DecompressionMethods.All,
			CookieContainer = CookieContainerAssetDelivery,
			AllowAutoRedirect = false,
			UseProxy = true,
			Proxy = null,
			DefaultProxyCredentials = CredentialCache.DefaultCredentials,
			ConnectTimeout = TimeSpan.FromSeconds(10),
			PooledConnectionLifetime = TimeSpan.FromMinutes(1.0),
			PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
			MaxConnectionsPerServer = 50
		});
		CookieContainerAssetDelivery.Add(new Cookie(".ROBLOSECURITY", SecureSettings.Default.RobloxCookie, "/", ".roblox.com"));
		AssetPackManager.Instance.SetDisabledList(Config.Instance.User.Launch.DisabledAssetPacks);
		AssetPackManager.Instance.SetClientYear(Config.Instance.Client.ClientYear);
		AssetPackDirectories = AssetPackManager.Instance.GetEnabledAssetPackDirectories();
		Logger.Instance.Info($"Loaded {AssetPackDirectories.Count} asset packs");
	}
}
