using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.Common.Enums;

namespace VoidstrapClient.WebServer.Controllers.Asset;

[ApiController]
[Route("Asset/BodyColorsFigure.ashx")]
public class BodyColorsFigureController : ControllerBase
{
	private struct FigureBodyColours
	{
		public int Head;

		public int Torso;

		public int LeftArm;

		public int RightArm;

		public int LeftLeg;

		public int RightLeg;
	}

	private static readonly IReadOnlyDictionary<FigureCharacterType, FigureBodyColours> _figureBodyColorsMap = new Dictionary<FigureCharacterType, FigureBodyColours>
	{
		[FigureCharacterType.Figure1] = new FigureBodyColours
		{
			Head = 24,
			Torso = 194,
			LeftArm = 24,
			RightArm = 24,
			LeftLeg = 119,
			RightLeg = 119
		},
		[FigureCharacterType.Figure2] = new FigureBodyColours
		{
			Head = 24,
			Torso = 22,
			LeftArm = 24,
			RightArm = 24,
			LeftLeg = 9,
			RightLeg = 9
		},
		[FigureCharacterType.Figure3] = new FigureBodyColours
		{
			Head = 24,
			Torso = 23,
			LeftArm = 24,
			RightArm = 24,
			LeftLeg = 119,
			RightLeg = 119
		},
		[FigureCharacterType.Figure4] = new FigureBodyColours
		{
			Head = 24,
			Torso = 22,
			LeftArm = 24,
			RightArm = 24,
			LeftLeg = 119,
			RightLeg = 119
		},
		[FigureCharacterType.Figure5] = new FigureBodyColours
		{
			Head = 24,
			Torso = 11,
			LeftArm = 24,
			RightArm = 24,
			LeftLeg = 119,
			RightLeg = 119
		},
		[FigureCharacterType.Figure6] = new FigureBodyColours
		{
			Head = 12,
			Torso = 194,
			LeftArm = 12,
			RightArm = 12,
			LeftLeg = 119,
			RightLeg = 119
		},
		[FigureCharacterType.Figure7] = new FigureBodyColours
		{
			Head = 18,
			Torso = 119,
			LeftArm = 18,
			RightArm = 18,
			LeftLeg = 119,
			RightLeg = 119
		},
		[FigureCharacterType.Figure8] = new FigureBodyColours
		{
			Head = 9,
			Torso = 194,
			LeftArm = 9,
			RightArm = 9,
			LeftLeg = 119,
			RightLeg = 119
		}
	};

	private readonly ILogger<BodyColorsFigureController> _logger;

	public BodyColorsFigureController(ILogger<BodyColorsFigureController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	public IActionResult Get([FromQuery] FigureCharacterType figureType)
	{
		if (!_figureBodyColorsMap.TryGetValue(figureType, out FigureBodyColours figureBodyColours))
		{
			return BadRequest();
		}
		string content = Common.FormatBodyColors(figureBodyColours.Head, figureBodyColours.LeftArm, figureBodyColours.LeftLeg, figureBodyColours.RightArm, figureBodyColours.RightLeg, figureBodyColours.Torso);
		return Content(content, "text/xml");
	}
}
