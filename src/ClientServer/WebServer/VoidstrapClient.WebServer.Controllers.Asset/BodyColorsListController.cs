using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace VoidstrapClient.WebServer.Controllers.Asset;

[ApiController]
[Route("Asset/BodyColorsList.ashx")]
public class BodyColorsListController : ControllerBase
{
	private readonly ILogger<BodyColorsListController> _logger;

	public BodyColorsListController(ILogger<BodyColorsListController> logger)
	{
		_logger = logger;
	}

	private int GetBodyColor(string colorStr, int fallback)
	{
		if (!int.TryParse(colorStr, out var result))
		{
			return fallback;
		}
		if (!BrickColors.IsValid(result))
		{
			return fallback;
		}
		return result;
	}

	[HttpGet]
	public IActionResult Get([FromQuery] string colors)
	{
		if (Config.Instance.Client.CharacterCompatibility.FigureBodyColours)
		{
			return Redirect("/Asset/BodyColorsFigure.ashx?figureType=1");
		}
		string[] array = colors.Split(',');
		if (array.Length != 6)
		{
			return BadRequest();
		}
		int bodyColor = GetBodyColor(array[0], 24);
		int bodyColor2 = GetBodyColor(array[1], 24);
		int bodyColor3 = GetBodyColor(array[2], 24);
		int bodyColor4 = GetBodyColor(array[3], 119);
		int bodyColor5 = GetBodyColor(array[4], 119);
		int bodyColor6 = GetBodyColor(array[5], 23);
		string content = Common.FormatBodyColors(bodyColor, bodyColor2, bodyColor4, bodyColor3, bodyColor5, bodyColor6);
		return Content(content, "text/xml");
	}
}
