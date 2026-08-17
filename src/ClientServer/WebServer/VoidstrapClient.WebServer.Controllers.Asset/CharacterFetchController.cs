using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.Common;
using VoidstrapClient.WebServer.Models;

namespace VoidstrapClient.WebServer.Controllers.Asset;

[ApiController]
[Route("Asset/CharacterFetch.ashx")]
public class CharacterFetchController : ControllerBase
{
	private readonly ILogger<CharacterFetchController> _logger;

	public CharacterFetchController(ILogger<CharacterFetchController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	public IActionResult Handle([FromQuery][Required] ulong userId)
	{
		if (!Directory.Exists(PathHelper.CharacterFetch))
		{
			return Ok();
		}
		string path = Path.Combine(PathHelper.CharacterFetch, $"{userId}.json");
		if (!System.IO.File.Exists(path))
		{
			return Ok();
		}
		string json = Config.ReadTextFile(path, 1048576);
		CharacterFetchData characterFetchData = JsonSerializer.Deserialize<CharacterFetchData>(json) ?? throw new Exception($"Failed to deserialise CharacterFetchData for {userId}");
		if (characterFetchData.BodyColors == null || characterFetchData.Assets == null || characterFetchData.Assets.Count > 100)
		{
			return BadRequest();
		}
		BodyColorData bodyColors = characterFetchData.BodyColors;
		string text = "";
		text += $"http://www.roblox.com/asset/bodycolorslist.ashx?colors={HttpUtility.UrlEncode($"{bodyColors.Head},{bodyColors.LeftArm},{bodyColors.RightArm},{bodyColors.LeftLeg},{bodyColors.RightLeg},{bodyColors.Torso}")}";
		foreach (ulong asset in characterFetchData.Assets)
		{
			int value = AvatarItems.GetById(asset)?.AssetVersion ?? 0;
			text += $";http://www.roblox.com/asset/?id={asset}&version={value}";
		}
		return Ok(text);
	}
}
