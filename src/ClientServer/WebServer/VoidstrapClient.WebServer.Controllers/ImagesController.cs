using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer.Controllers;

[ApiController]
[Route("Images")]
public class ImagesController : ControllerBase
{
	private readonly ILogger<ImagesController> _logger;

	public ImagesController(ILogger<ImagesController> logger)
	{
		_logger = logger;
	}

	[HttpGet("{*path}")]
	public IActionResult Get(string path)
	{
		try
		{
			string root = Path.GetFullPath(Path.Combine(PathHelper.Data, "wwwimgs"));
			string candidate = Path.GetFullPath(Path.Combine(root, path));
			string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			if (!candidate.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(candidate))
			{
				return NotFound();
			}
			string current = root;
			foreach (string segment in Path.GetRelativePath(root, candidate).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
			{
				current = Path.Combine(current, segment);
				if ((System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
				{
					return NotFound();
				}
			}
			return PhysicalFile(candidate, "image/png", enableRangeProcessing: true);
		}
		catch (System.Exception)
		{
			return NotFound();
		}
	}
}
