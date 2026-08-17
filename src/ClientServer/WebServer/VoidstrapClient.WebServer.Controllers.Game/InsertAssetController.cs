using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.WebServer.Services;

namespace VoidstrapClient.WebServer.Controllers.Game;

[ApiController]
[Route("Game/Tools/InsertAsset.ashx")]
public class InsertAssetController : ControllerBase
{
	private readonly ILogger<InsertAssetController> _logger;

	public InsertAssetController(ILogger<InsertAssetController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	public IActionResult Handle([FromQuery(Name = "userId")] int userId = 0, [FromQuery(Name = "sId")] int setId = 0, [FromQuery(Name = "type")] string? type = null)
	{
		string content = "<List></List>";
		if (type == "base")
		{
			content = SetsService.Instance.GetBaseSet();
		}
		else if (setId > 0)
		{
			content = SetsService.Instance.GetSet(setId);
		}
		else if (userId > 0)
		{
			content = SetsService.Instance.GetUserSets(userId);
		}
		return Content(content, "text/xml");
	}
}
