using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.WebServer.Enums;

namespace VoidstrapClient.WebServer.Controllers.Thumbs;

[ApiController]
public class AssetController : ThumbnailController
{
	private readonly ILogger<AssetController> _logger;

	public AssetController(ILogger<AssetController> logger)
	{
		_logger = logger;
	}

	private async Task<IActionResult> Get(ulong id, int x, int y, ThumbnailFormat format)
	{
		if (!IsValidSize(x, y) || !IsValidFormat(format))
		{
			return BadRequest();
		}

		byte[] array = LocalThumbnailHelper.Instance.GetThumbnailData(id);
		if (array == null)
		{
			Thumbnails.ThumbnailResponse response = await Thumbnails.GetThumbnail(id, x, y, format.ToString(), HttpContext.RequestAborted);
			return File(response.Data, response.ContentType);
		}
		return await ThumbnailAsync(array, x, y, format, base.HttpContext.RequestAborted);
	}

	[HttpGet]
	[Route("Thumbs/Asset.ashx")]
	public async Task<IActionResult> GetThumbs([FromQuery(Name = "assetid")] ulong? id1 = null, [FromQuery(Name = "x")] int? x1 = null, [FromQuery(Name = "y")] int? y1 = null, [FromQuery(Name = "id")] ulong? id2 = null, [FromQuery(Name = "width")] int? x2 = null, [FromQuery(Name = "height")] int? y2 = null, [FromQuery(Name = "format")] ThumbnailFormat format = ThumbnailFormat.Png)
	{
		ulong id3 = id1 ?? id2.GetValueOrDefault();
		int x3 = x1 ?? x2 ?? 30;
		int y3 = y1 ?? y2 ?? 30;
		return await Get(id3, x3, y3, format);
	}

	[HttpGet]
	[Route("Game/Tools/ThumbnailAsset.ashx")]
	public async Task<IActionResult> GetGame([FromQuery(Name = "aid")] ulong id, [FromQuery(Name = "wd")] int x, [FromQuery(Name = "ht")] int y, [FromQuery(Name = "fmt")] ThumbnailFormat format = ThumbnailFormat.Png, [FromQuery(Name = "assetversionid")] ulong assetVersionId = 0uL)
	{
		if (assetVersionId != 0L && AssetVersionIdHelper.Map.ContainsKey(assetVersionId))
		{
			id = AssetVersionIdHelper.Map[assetVersionId];
		}
		return await Get(id, x, y, format);
	}
}
