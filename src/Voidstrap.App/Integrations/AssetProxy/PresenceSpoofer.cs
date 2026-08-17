using System;
using Voidstrap.Core.AssetProxy;

namespace Voidstrap.Integrations.AssetProxy;

public static class PresenceSpoofer
{
	public enum Mode
	{
		Off,
		Online,
		Studio,
		Offline
	}

	private static readonly string SessionId = Guid.NewGuid().ToString();

	public static Mode Current => (Mode)Math.Clamp(App.Settings.Prop.PresenceSpoofMode, 0, 3);

	public static bool IsEnabled()
	{
		return Current != Mode.Off;
	}

	public static bool CanTransformRequest(string host, string path, string method)
	{
		return PresenceSpoofPolicy.CanTransformRequest(ToCoreMode(Current), host, path, method);
	}

	public static bool TryCreateLocalResponse(string host, string path, string method, out byte[] body)
	{
		body = [];
		if (PresenceSpoofPolicy.ShouldSuppress(ToCoreMode(Current), host, path, method))
		{
			body = "{}"u8.ToArray();
			return true;
		}
		return false;
	}

	public static byte[]? ProcessRequest(string host, string path, string method, byte[] body)
	{
		return PresenceSpoofPolicy.TransformRequest(ToCoreMode(Current), host, path, method, body, SessionId);
	}

	private static PresenceSpoofMode ToCoreMode(Mode mode)
	{
		return (PresenceSpoofMode)(int)mode;
	}
}
