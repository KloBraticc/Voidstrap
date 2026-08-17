using System;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers.Game;

[ApiController]
public class PlaceLauncherController : ControllerBase
{
	private readonly ILogger<PlaceLauncherController> _logger;

	public PlaceLauncherController(ILogger<PlaceLauncherController> logger)
	{
		_logger = logger;
	}

	private string GetBaseUrl()
	{
		string host = base.Request.Host.HasValue ? base.Request.Host.Value : "www.roblox.com";
		return base.Request.Scheme + "://" + host;
	}

	[HttpGet("Game/PlaceLauncher.ashx")]
	[HttpPost("Game/PlaceLauncher.ashx")]
	public IActionResult PlaceLauncher([FromQuery] long placeId = 1818L, [FromQuery] string? serverIp = "127.0.0.1", [FromQuery] ushort serverPort = 53640)
	{
		if (string.IsNullOrEmpty(serverIp))
		{
			serverIp = "127.0.0.1";
		}
		if (placeId <= 0 || serverPort == 0 || serverIp.Length > 255 || serverIp.Any(char.IsControl))
		{
			return BadRequest();
		}
		int userId = Config.Instance.User.Player.Id;
		string userName = Config.Instance.User.Player.Name;
		if (string.IsNullOrEmpty(userName))
		{
			userName = "Player";
		}
		string baseUrl = GetBaseUrl();
		string joinUrl = $"{baseUrl}/Game/Join.ashx?userId={userId}&userName={Uri.EscapeDataString(userName)}&serverIp={Uri.EscapeDataString(serverIp)}&serverPort={serverPort}&placeId={placeId}";
		string json = JsonSerializer.Serialize(new
		{
			jobId = "",
			status = 2,
			joinScriptUrl = joinUrl,
			authenticationUrl = baseUrl + "/Login/Negotiate.ashx",
			authenticationTicket = "revival",
			message = (string?)null
		});
		return Content(json, "application/json");
	}
}
