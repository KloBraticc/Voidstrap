using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers;

[ApiController]
public class OwnershipController : ControllerBase
{
	private readonly ILogger<OwnershipController> _logger;

	public OwnershipController(ILogger<OwnershipController> logger)
	{
		_logger = logger;
	}

	[HttpPost("Ownership/HasAsset")]
	public IActionResult Get()
	{
		return Ok("false");
	}
}
