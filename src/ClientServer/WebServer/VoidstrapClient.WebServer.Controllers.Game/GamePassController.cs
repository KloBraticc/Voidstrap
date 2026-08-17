using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers.Game;

[ApiController]
[Route("Game/GamePass/GamePassHandler.ashx")]
public class GamePassController : ControllerBase
{
	private readonly ILogger<GamePassController> _logger;

	public GamePassController(ILogger<GamePassController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	[HttpPost]
	public IActionResult Get()
	{
		return Content("<Value Type=\"boolean\">false</Value>", "text/html");
	}
}
