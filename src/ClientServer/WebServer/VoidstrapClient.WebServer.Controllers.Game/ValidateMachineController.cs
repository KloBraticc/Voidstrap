using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers.Game;

[ApiController]
[Route("game/validate-machine")]
public class ValidateMachineController : ControllerBase
{
	private readonly ILogger<ValidateMachineController> _logger;

	public ValidateMachineController(ILogger<ValidateMachineController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	public IActionResult Get()
	{
		return Content("{\"success\":true}", "application/json");
	}
}
