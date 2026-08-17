using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers;

[ApiController]
public class BlankController : ControllerBase
{
	private readonly ILogger<BlankController> _logger;

	public BlankController(ILogger<BlankController> logger)
	{
		_logger = logger;
	}

	[HttpGet("Analytics/ContentProvider.ashx")]
	[HttpPost("Analytics/ContentProvider.ashx")]
	[HttpGet("Analytics/Measurement.ashx")]
	[HttpPost("Analytics/Measurement.ashx")]
	[HttpPost("Error/Dmp.ashx")]
	[HttpPost("Error/Lua.ashx")]
	[HttpGet("Game/Logout.aspx")]
	[HttpGet("Game/ClientPresence.ashx")]
	[HttpGet("Game/Cdn.ashx")]
	[HttpGet("Game/JoinRate.ashx")]
	[HttpPost("Game/Report-Stats")]
	[HttpPost("Game/MachineConfiguration.ashx")]
	[HttpPost("v1.1/Counters/Increment")]
	[HttpPost("v1.0/MultiIncrement")]
	[HttpPost("Gatherer/LogEntry")]
	public IActionResult Get()
	{
		return Ok();
	}
}
