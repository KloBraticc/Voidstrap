using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.Common.Enums;

namespace VoidstrapClient.WebServer.Controllers.Asset;

[ApiController]
[Route("Asset/CharacterFetchFigure.ashx")]
public class CharacterFetchFigureController : ControllerBase
{
	private readonly ILogger<CharacterFetchFigureController> _logger;

	public CharacterFetchFigureController(ILogger<CharacterFetchFigureController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	public IActionResult Handle([FromQuery] FigureCharacterType figureType)
	{
		if (!Enum.IsDefined(figureType))
		{
			return BadRequest();
		}
		return Ok($"http://www.roblox.com/asset/bodycolorsfigure.ashx?figureType={figureType}");
	}
}
