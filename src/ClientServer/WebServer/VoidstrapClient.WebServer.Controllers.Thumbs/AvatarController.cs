using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.Common;
using VoidstrapClient.WebServer.Enums;

namespace VoidstrapClient.WebServer.Controllers.Thumbs;

[ApiController]
public class AvatarController : ThumbnailController
{
	private const long MaxImageBytes = 10L * 1024 * 1024;

	private static readonly string _renderPath = Path.Combine(PathHelper.UserAppData, "render.png");

	private static readonly string _defaultPath = Path.Combine(PathHelper.ThumbnailsDeprecated, "avatar.png");

	private static byte[]? _renderBytes = null;

	private static byte[]? _defaultBytes = null;
	private static readonly object _imageCacheLock = new object();
	private static DateTime _renderLastWriteUtc;
	private static long _renderLength = -1;

	private readonly ILogger<AvatarController> _logger;

	public AvatarController(ILogger<AvatarController> logger)
	{
		_logger = logger;
	}

	[HttpGet]
	[Route("Thumbs/Avatar.ashx")]
	public async Task<IActionResult> GetAvatar([FromQuery(Name = "x")][Required] int width, [FromQuery(Name = "y")][Required] int height, [FromQuery(Name = "format")] ThumbnailFormat format = ThumbnailFormat.Png, [FromQuery(Name = "userid")] ulong? userId = null, [FromQuery(Name = "username")] string? userName = null, CancellationToken cancellationToken = default)
	{
		if (!ThumbnailController.IsValidSize(width, height) || !IsValidFormat(format))
		{
			return BadRequest();
		}
		bool isCurrentUser = Config.Instance.User.Player.Id > 0 && userId == (ulong)Config.Instance.User.Player.Id || string.Equals(userName, Config.Instance.User.Player.Name, StringComparison.OrdinalIgnoreCase);
		if (isCurrentUser)
		{
			byte[]? renderBytes = GetRenderBytes();
			if (renderBytes != null)
			{
				return await ThumbnailAsync(renderBytes, width, height, format, cancellationToken);
			}
		}
		byte[]? defaultBytes = GetDefaultBytes();
		if (defaultBytes == null)
		{
			return NotFound();
		}
		return await ThumbnailAsync(defaultBytes, width, height, format, cancellationToken);
	}

	private static byte[]? GetRenderBytes()
	{
		lock (_imageCacheLock)
		{
			try
			{
				FileInfo info = new FileInfo(_renderPath);
				if (!info.Exists)
				{
					return null;
				}
				if (_renderBytes != null && info.LastWriteTimeUtc == _renderLastWriteUtc && info.Length == _renderLength)
				{
					return _renderBytes;
				}
				byte[] data = Config.ReadBytesFile(_renderPath, MaxImageBytes);
				_renderBytes = data;
				_renderLastWriteUtc = info.LastWriteTimeUtc;
				_renderLength = info.Length;
				return data;
			}
			catch
			{
				return _renderBytes;
			}
		}
	}

	private static byte[]? GetDefaultBytes()
	{
		lock (_imageCacheLock)
		{
			if (_defaultBytes != null)
			{
				return _defaultBytes;
			}
			try
			{
				_defaultBytes = Config.ReadBytesFile(_defaultPath, MaxImageBytes);
				return _defaultBytes;
			}
			catch
			{
				return null;
			}
		}
	}
}
