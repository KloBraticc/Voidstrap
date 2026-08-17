using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers.Data;

[ApiController]
[Route("Data/AutoSave.ashx")]
public class AutoSaveController : ControllerBase
{
	private readonly ILogger<AutoSaveController> _logger;

	public AutoSaveController(ILogger<AutoSaveController> logger)
	{
		_logger = logger;
	}

	[RequestSizeLimit(AutoSaveHelper.MaxSaveBytes)]
	[HttpPost]
	public async Task<IActionResult> Handle()
	{
		if (Request.ContentLength > AutoSaveHelper.MaxSaveBytes)
		{
			return StatusCode(413);
		}
		try
		{
			await AutoSaveHelper.SaveAsync(Request.Body, shouldCompress: false, Request.HttpContext.RequestAborted);
			return Ok();
		}
		catch (System.IO.InvalidDataException)
		{
			return StatusCode(413);
		}
		catch (System.InvalidOperationException)
		{
			return StatusCode(429);
		}
	}
}
