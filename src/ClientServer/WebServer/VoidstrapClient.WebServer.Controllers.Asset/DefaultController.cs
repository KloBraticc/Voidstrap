using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer.Controllers.Asset;

[ApiController]
[Route("Asset")]
[Route("Asset/Default.ashx")]
[Route("Data/Get.ashx")]
public class DefaultController : ControllerBase
{
	private const ulong PlaceId = 1818uL;

	private readonly ILogger<DefaultController> _logger;

	public DefaultController(ILogger<DefaultController> logger)
	{
		_logger = logger;
	}

	private static string? GetAssetFileWithId(ulong id)
	{
		string path = Path.Combine(PathHelper.Clients, Config.Instance.Client.ClientName, "assets");
		Directory.CreateDirectory(path);
		Directory.CreateDirectory(PathHelper.Character);
		string searchPattern = $"{id}.*";
		string? file = FindFirstRegularFile(path, searchPattern);
		if (file != null)
		{
			return file;
		}
		file = FindFirstRegularFile(PathHelper.Character, searchPattern);
		if (file != null)
		{
			return file;
		}
		foreach (string assetPackDirectory in Common.AssetPackDirectories)
		{
			if (Directory.Exists(assetPackDirectory))
			{
				file = FindFirstRegularFile(assetPackDirectory, searchPattern);
				if (file != null)
				{
					return file;
				}
			}
		}
		return null;
	}

	private static string? FindFirstRegularFile(string directory, string searchPattern)
	{
		foreach (string file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
		{
			try
			{
				if ((System.IO.File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
				{
					return file;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static string ProcessScript(string scriptText, ulong id)
	{
		scriptText = scriptText.Replace("{experimentalplayerlistenabled}", Config.Instance.User.Launch.ExperimentalPlayerlistEnabled.ToString().ToLowerInvariant());
		if (Config.Instance.Client.SignAssetScripts)
		{
			scriptText = ScriptSigner.Sign(scriptText, id);
		}
		return scriptText;
	}

	private static bool IsRedirectStatusCode(HttpStatusCode code)
	{
		if (code == HttpStatusCode.Found || (uint)(code - 307) <= 1u)
		{
			return true;
		}
		return false;
	}

	private async Task<IActionResult> ProxyRemoteAsync(string url, bool head, CancellationToken cancellationToken)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromMinutes(2));
		try
		{
			using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, url);
			using HttpResponseMessage response = await Common.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
			if (!response.IsSuccessStatusCode)
				return StatusCode((int)response.StatusCode);
			Response.StatusCode = (int)response.StatusCode;
			Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
			if (response.Content.Headers.ContentLength.HasValue)
				Response.ContentLength = response.Content.Headers.ContentLength.Value;
			if (response.Content.Headers.ContentEncoding.Count > 0)
				Response.Headers.ContentEncoding = string.Join(", ", response.Content.Headers.ContentEncoding);
			if (!head)
			{
				await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token);
				await stream.CopyToAsync(Response.Body, deadline.Token);
			}
			return new EmptyResult();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			return Response.HasStarted ? new EmptyResult() : StatusCode(StatusCodes.Status504GatewayTimeout);
		}
		catch (HttpRequestException)
		{
			return Response.HasStarted ? new EmptyResult() : StatusCode(StatusCodes.Status502BadGateway);
		}
	}

	private async Task<IActionResult> RedirectToRoblox(bool head, CancellationToken cancellationToken)
	{
		string url = $"https://assetdelivery.roblox.com/v1/asset/{base.Request.QueryString}";
		if (Config.Instance.Client.ClientWillDieIfAHttpRedirectHappens && string.IsNullOrEmpty(SecureSettings.Default.RobloxCookie))
			return await ProxyRemoteAsync(url, head, cancellationToken);
		if (Config.Instance.Client.ClientWillDieIfAHttpRedirectHappens || !string.IsNullOrEmpty(SecureSettings.Default.RobloxCookie))
		{
			using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(TimeSpan.FromSeconds(30));
			try
			{
				using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, url);
				using HttpResponseMessage httpResponseMessage = await Common.HttpClientAssetDelivery.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
				if (!IsRedirectStatusCode(httpResponseMessage.StatusCode) && !httpResponseMessage.IsSuccessStatusCode)
				{
					Logger.Instance.Warn($"Asset delivery secure: got {(int)httpResponseMessage.StatusCode}");
					return Config.Instance.Client.ClientWillDieIfAHttpRedirectHappens ? await ProxyRemoteAsync(url, head, cancellationToken) : Redirect(url);
				}
				Uri? location = httpResponseMessage.Headers.Location;
				if (location == null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps)
				{
					Logger.Instance.Warn("Asset delivery secure: could not find Location header");
					return Config.Instance.Client.ClientWillDieIfAHttpRedirectHappens ? await ProxyRemoteAsync(url, head, cancellationToken) : Redirect(url);
				}
				if (Config.Instance.Client.ClientWillDieIfAHttpRedirectHappens)
					return await ProxyRemoteAsync(location.AbsoluteUri, head, cancellationToken);
				return Redirect(location.AbsoluteUri);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception value) when (value is OperationCanceledException or HttpRequestException)
			{
				if (Config.Instance.Client.ClientWillDieIfAHttpRedirectHappens)
					return await ProxyRemoteAsync(url, head, cancellationToken);
				return Redirect(url);
			}
		}
		return Redirect(url);
	}

	private async Task<IActionResult> ServeLocalFileAsync(string path, bool compressed, bool isLua, ulong id, bool head, CancellationToken cancellationToken)
	{
		if (isLua)
		{
			Response.ContentType = "text/plain; charset=utf-8";
			if (head)
				return new EmptyResult();
			string script;
			try
			{
				script = await Config.ReadTextFileAsync(path, 8388608, cancellationToken);
			}
			catch (FileNotFoundException)
			{
				return NotFound();
			}
			catch (InvalidDataException)
			{
				return StatusCode(StatusCodes.Status413PayloadTooLarge);
			}
			return Content(ProcessScript(script, id), "text/plain", Encoding.UTF8);
		}
		if (!compressed)
			return PhysicalFile(path, "application/octet-stream", true);
		if (!Config.Instance.Client.ClientWillDieIfAHttpRedirectHappens)
		{
			Response.Headers.ContentEncoding = "gzip";
			return PhysicalFile(path, "application/octet-stream", true);
		}
		Response.ContentType = "application/octet-stream";
		if (head)
			return new EmptyResult();
		FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		return File(new GZipStream(input, CompressionMode.Decompress), "application/octet-stream");
	}

	private static bool TryResolveWithin(string root, string relativePath, out string path)
	{
		path = "";
		if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
			return false;
		string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
		if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
			return false;
		path = candidate;
		return true;
	}

	[HttpGet]
	[HttpHead]
	public async Task<IActionResult> Get([FromQuery] ulong id = 0uL, [FromQuery] ulong assetVersionId = 0uL, [FromQuery] int version = 0)
	{
		CancellationToken cancellationToken = HttpContext.RequestAborted;
		bool head = HttpMethods.IsHead(Request.Method);
		if (assetVersionId != 0L && AssetVersionIdHelper.Map.ContainsKey(assetVersionId))
		{
			id = AssetVersionIdHelper.Map[assetVersionId];
		}
		if (id == 0L || version != 0)
			return await RedirectToRoblox(head, cancellationToken);
		if (id == 1818)
		{
			if (Config.Instance.User.Launch.SelectedMap == null)
			{
				return StatusCode(500);
			}
			if (!TryResolveWithin(Utils.GetMapsDirectory(), Config.Instance.User.Launch.SelectedMap, out string text) || !System.IO.File.Exists(text))
			{
				return StatusCode(500);
			}
			return await ServeLocalFileAsync(text, text.EndsWith(".gz", StringComparison.OrdinalIgnoreCase), false, id, head, cancellationToken);
		}
		LocalAssetHelper.AssetResult assetData = LocalAssetHelper.Instance.GetAssetData(id);
		if (assetData.Success)
			return await ServeLocalFileAsync(assetData.FilePath, assetData.Compressed, assetData.IsLua, id, head, cancellationToken);
		string assetFileWithId = GetAssetFileWithId(id);
		if (assetFileWithId == null)
			return await RedirectToRoblox(head, cancellationToken);
		return await ServeLocalFileAsync(assetFileWithId, assetFileWithId.EndsWith(".gz", StringComparison.OrdinalIgnoreCase), assetFileWithId.EndsWith(".lua", StringComparison.OrdinalIgnoreCase), id, head, cancellationToken);
	}
}
