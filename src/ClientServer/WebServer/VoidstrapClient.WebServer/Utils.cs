using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

public class Utils
{
	public static string GetMapsDirectory()
	{
		return (!string.IsNullOrWhiteSpace(Config.Instance.User.Launch.CustomMapsDirectory)) ? Config.Instance.User.Launch.CustomMapsDirectory : PathHelper.Maps;
	}
}
