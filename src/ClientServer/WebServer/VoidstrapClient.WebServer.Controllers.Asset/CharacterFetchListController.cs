using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.Common.Enums;
using VoidstrapClient.WebServer.Models;

namespace VoidstrapClient.WebServer.Controllers.Asset;

[ApiController]
[Route("Asset/CharacterFetchList.ashx")]
public class CharacterFetchListController : ControllerBase
{
	private readonly ILogger<CharacterFetchListController> _logger;

	public CharacterFetchListController(ILogger<CharacterFetchListController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	public async Task<IActionResult> Handle([FromQuery] string? items, [FromQuery] string? colors)
	{
		if (items == null)
		{
			items = "";
		}
		if (colors == null)
		{
			colors = "";
		}
		if (items.Length > 4096 || colors.Length > 128)
		{
			return BadRequest();
		}
		if (Config.Instance.Client.CharacterCompatibility.FigureBodyColours)
		{
			return Redirect("/Asset/CharacterFetchFigure.ashx?figureType=1");
		}
		StringBuilder text = new StringBuilder();
		List<ulong> list = new List<ulong>();
		string[] array = items.Split(',');
		foreach (string s in array)
		{
			if (list.Count >= 100)
			{
				break;
			}
			if (ulong.TryParse(s, out var result))
			{
				list.Add(result);
			}
		}
		IReadOnlyList<ulong> enumerable = await AvatarAuthoriser.FilterUnsafeAssetsAsync(list, base.HttpContext.RequestAborted);
		foreach (ulong item in enumerable)
		{
			bool flag = false;
			AvatarItem byId = AvatarItems.GetById(item);
			int value;
			if (byId != null)
			{
				value = byId.AssetVersion;
				if (byId.Type == AvatarAssetType.Gear)
				{
					flag = true;
				}
			}
			else
			{
				value = 0;
			}
			text.Append($"http://www.roblox.com/asset/?id={item}&version={value}{(flag ? "&equipped=1" : "")};");
		}
		if (!string.IsNullOrEmpty(colors))
		{
			text.Append("http://www.roblox.com/asset/bodycolorslist.ashx?colors=").Append(HttpUtility.UrlEncode(colors));
		}
		return Ok(text.ToString());
	}
}
