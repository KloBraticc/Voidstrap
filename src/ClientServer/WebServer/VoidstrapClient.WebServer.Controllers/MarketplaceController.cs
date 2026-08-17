using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers;

[ApiController]
public class MarketplaceController : ControllerBase
{
	private readonly ILogger<MarketplaceController> _logger;

	public MarketplaceController(ILogger<MarketplaceController> logger)
	{
		_logger = logger;
	}

	private static string ResolvePlaceName()
	{
		string map = Config.Instance?.User?.Launch?.SelectedMap ?? "";
		string name = Path.GetFileName(map);
		foreach (string ext in new[] { ".gz", ".rbxlx", ".rbxl" })
		{
			if (name.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase))
				name = name.Substring(0, name.Length - ext.Length);
		}
		return string.IsNullOrWhiteSpace(name) ? "Place" : name;
	}

	[HttpGet("marketplace/productinfo")]
	public IActionResult ProductInfo([FromQuery] long assetId = 0L)
	{
		string creator = "ROBLOX";
		var info = new
		{
			TargetId = assetId,
			ProductType = "User Product",
			AssetId = assetId,
			ProductId = assetId,
			Name = ResolvePlaceName(),
			Description = "",
			AssetTypeId = 9,
			Creator = new
			{
				Id = 0,
				Name = creator,
				CreatorType = "User",
				CreatorTargetId = 0
			},
			IconImageAssetId = 0,
			Created = "2013-01-01T00:00:00Z",
			Updated = "2013-01-01T00:00:00Z",
			PriceInRobux = (int?)null,
			PriceInTickets = (int?)null,
			Sales = 0,
			IsNew = false,
			IsForSale = false,
			IsPublicDomain = true,
			IsLimited = false,
			IsLimitedUnique = false,
			Remaining = (int?)null,
			MinimumMembershipLevel = 0,
			ContentRatingTypeId = 0
		};
		return Content(JsonSerializer.Serialize(info), "application/json");
	}
}
