using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.WebServer.Services;

namespace VoidstrapClient.WebServer.Controllers;

[ApiController]
[Route("Persistence")]
public class PersistenceController : ControllerBase
{
	private readonly ILogger<PersistenceController> _logger;

	public PersistenceController(ILogger<PersistenceController> logger)
	{
		_logger = logger;
	}

	[HttpGet("GetBlobUrl.ashx")]
	public IActionResult GetBlob(int userId)
	{
		base.Response.Headers.CacheControl = "no-cache";
		byte[] blob = DataPersistenceService.Instance.GetBlob(userId);
		base.Response.Headers["Content-Encoding"] = "gzip";
		return File(blob, "application/octet-stream");
	}

	[HttpPost("SetBlob.ashx")]
	[RequestSizeLimit(DataPersistenceService.MaxBlobSize)]
	public async Task<IActionResult> SetBlob(int userId)
	{
		base.Response.Headers.CacheControl = "no-cache";
		if (userId <= 0)
		{
			return BadRequest();
		}
		if (base.Request.ContentLength > DataPersistenceService.MaxBlobSize)
		{
			return StatusCode(413);
		}

		byte[] blob = await ReadBlobAsync(base.Request.Body, base.HttpContext.RequestAborted);
		string[] encodings = base.Request.Headers.ContentEncoding
			.SelectMany(value => value.Split(','))
			.Select(value => value.Trim())
			.Where(value => value.Length > 0)
			.ToArray();
		if (encodings.Length > 1 || encodings.Length == 1 && !string.Equals(encodings[0], "gzip", StringComparison.OrdinalIgnoreCase))
		{
			return StatusCode(415);
		}
		bool alreadyCompressed = encodings.Length == 1;
		if (alreadyCompressed && !await IsValidGzipAsync(blob, base.HttpContext.RequestAborted))
		{
			return StatusCode(415);
		}
		DataPersistenceService.Instance.SaveBlob(userId, blob, alreadyCompressed);
		return Ok();
	}

	private static async Task<bool> IsValidGzipAsync(byte[] blob, CancellationToken cancellationToken)
	{
		try
		{
			using MemoryStream source = new MemoryStream(blob, writable: false);
			using GZipStream gzip = new GZipStream(source, CompressionMode.Decompress);
			byte[] chunk = new byte[81920];
			int total = 0;
			while (true)
			{
				int read = await gzip.ReadAsync(chunk.AsMemory(), cancellationToken);
				if (read == 0)
				{
					return true;
				}
				total += read;
				if (total > DataPersistenceService.MaxBlobSize)
				{
					return false;
				}
			}
		}
		catch (InvalidDataException)
		{
			return false;
		}
	}

	private static async Task<byte[]> ReadBlobAsync(Stream stream, CancellationToken cancellationToken)
	{
		using MemoryStream buffer = new MemoryStream();
		byte[] chunk = new byte[81920];
		while (true)
		{
			int read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
			if (read == 0)
			{
				return buffer.ToArray();
			}
			if (buffer.Length + read > DataPersistenceService.MaxBlobSize)
			{
				throw new BadHttpRequestException("Request body is too large", 413);
			}
			await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
		}
	}
}
