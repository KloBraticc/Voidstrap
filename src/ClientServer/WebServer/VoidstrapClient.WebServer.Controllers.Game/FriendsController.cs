using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.WebServer.Services;

namespace VoidstrapClient.WebServer.Controllers.Game;

[ApiController]
public class FriendsController : ControllerBase
{
	private readonly ILogger<FriendsController> _logger;

	public FriendsController(ILogger<FriendsController> logger)
	{
		_logger = logger;
	}

	[HttpGet("Game/AreFriends")]
	[HttpGet("Friend/AreFriends")]
	public IActionResult GetAreFriends([FromQuery(Name = "userId")] int userId, [FromQuery(Name = "otherUserIds")] string otherUserIdsStr)
	{
		if (userId <= 0)
		{
			return BadRequest();
		}
		string[] values = otherUserIdsStr?.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries) ?? [];
		if (values.Length > 100)
		{
			return BadRequest();
		}
		List<int> users = new(values.Length);
		foreach (string value in values)
		{
			if (!int.TryParse(value, out int user) || user < 1)
			{
				return BadRequest();
			}
			users.Add(user);
		}
		int[] friends = FriendsService.Instance.AreFriends(userId, users).ToArray();
		string text = "S";
		if (friends.Length > 0)
		{
			text += string.Join(',', friends);
			text += ",";
		}
		return Content(text, "text/plain");
	}

	[HttpPost("Game/CreateFriend")]
	[HttpPost("Friend/CreateFriend")]
	[HttpGet("Game/CreateFriend")]
	[HttpGet("Friend/CreateFriend")]
	public IActionResult GetCreateFriend([FromQuery(Name = "firstUserId")] int firstUserId, [FromQuery(Name = "secondUserId")] int secondUserId)
	{
		if (firstUserId <= 0 || secondUserId <= 0 || firstUserId == secondUserId)
		{
			return BadRequest();
		}
		if (!FriendsService.Instance.TryCreateFriend(firstUserId, secondUserId))
		{
			return StatusCode(429);
		}
		return Ok();
	}

	[HttpPost("Game/BreakFriend")]
	[HttpPost("Friend/BreakFriend")]
	[HttpGet("Game/BreakFriend")]
	[HttpGet("Friend/BreakFriend")]
	public IActionResult GetBreakFriend([FromQuery(Name = "firstUserId")] int firstUserId, [FromQuery(Name = "secondUserId")] int secondUserId)
	{
		if (firstUserId <= 0 || secondUserId <= 0 || firstUserId == secondUserId)
		{
			return BadRequest();
		}
		FriendsService.Instance.BreakFriend(firstUserId, secondUserId);
		return Ok();
	}
}
