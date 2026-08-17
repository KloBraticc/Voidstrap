using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers.Asset;

[ApiController]
[Route("Asset/GetScriptState.ashx")]
public class GetScriptStateController : ControllerBase
{
	private readonly ILogger<GetScriptStateController> _logger;

	public GetScriptStateController(ILogger<GetScriptStateController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	public IActionResult Handle()
	{
		return Ok("0 0 1 0");
	}
}
