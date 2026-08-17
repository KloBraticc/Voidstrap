using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers.ClientSettings;

[ApiController]
public class ApplicationSettingsController : ControllerBase
{
	private readonly ILogger<ApplicationSettingsController> _logger;

	public ApplicationSettingsController(ILogger<ApplicationSettingsController> logger)
	{
		_logger = logger;
	}

	[HttpGet("v1/settings/application")]
	[HttpGet("v2/settings/application")]
	[HttpGet("v1/settings/application/{applicationName}")]
	public IActionResult Application([FromQuery] string? applicationName = null)
	{
		string flags = ((!System.IO.File.Exists(ClientPaths.Flags)) ? "{}" : Config.ReadTextFile(ClientPaths.Flags, 4194304));
		string json = "{\"applicationSettings\":" + flags + "}";
		return Content(json, "application/json");
	}
}
