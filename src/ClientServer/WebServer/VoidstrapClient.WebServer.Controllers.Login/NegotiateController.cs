using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers.Login;

[ApiController]
public class NegotiateController : ControllerBase
{
	private readonly ILogger<NegotiateController> _logger;

	public NegotiateController(ILogger<NegotiateController> logger)
	{
		_logger = logger;
	}

	[HttpGet("Login/Negotiate.ashx")]
	[HttpPost("Login/Negotiate.ashx")]
	public IActionResult Negotiate()
	{
		return Content("true", "text/plain");
	}
}
