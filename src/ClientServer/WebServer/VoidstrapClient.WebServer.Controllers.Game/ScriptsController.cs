using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VoidstrapClient.Common;
using VoidstrapClient.Common.Enums;
using VoidstrapClient.WebServer.Models;

namespace VoidstrapClient.WebServer.Controllers.Game;

[ApiController]
[Route("Game")]
public class ScriptsController : ControllerBase
{
	private readonly ILogger<ScriptsController> _logger;

	public ScriptsController(ILogger<ScriptsController> logger)
	{
		_logger = logger;
	}

	private static string GetScriptFromPath(string path)
	{
		if (!System.IO.File.Exists(path))
		{
			return "";
		}
		return Config.ReadTextFile(path, 8388608);
	}

	private static bool TryEscapeScriptString(string? value, int maximumLength, out string escaped)
	{
		value ??= "";
		if (value.Length > maximumLength || value.Any(char.IsControl) && value.Any(character => character is not '\r' and not '\n' and not '\t'))
		{
			escaped = "";
			return false;
		}
		escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
		return true;
	}

	private string GetBaseUrl()
	{
		string host = base.Request.Host.HasValue ? base.Request.Host.Value : "www.roblox.com";
		return base.Request.Scheme + "://" + host + "/";
	}

	[HttpGet("Join.ashx")]
	public IActionResult Join([FromQuery] int userId = 0, [FromQuery] string? userName = "Player", [FromQuery] string? serverIp = "localhost", [FromQuery] ushort serverPort = 53640, [FromQuery] bool testMode = true, [FromQuery] ChatStyle chatStyle = ChatStyle.Classic)
	{
		if (userName == null)
		{
			userName = "Player";
		}
		if (serverIp == null)
		{
			serverIp = "localhost";
		}
		if (userId < 0 || serverPort == 0 || !TryEscapeScriptString(userName, 64, out string safeUserName) || !TryEscapeScriptString(serverIp, 255, out string safeServerIp))
		{
			return BadRequest();
		}
		if (!TryEscapeScriptString(UrlConstructor.GetCharacterAppearanceUrl(), 16384, out string safeCharacter) || !TryEscapeScriptString(GetBaseUrl(), 2048, out string safeBaseUrl))
		{
			return BadRequest();
		}
		string joinPath = System.IO.File.Exists(ClientPaths.JoinJson) ? ClientPaths.JoinJson : ClientPaths.JoinScript;
		string scriptFromPath = GetScriptFromPath(joinPath);
		scriptFromPath = scriptFromPath.Replace("{userid}", userId.ToString()).Replace("{username}", safeUserName).Replace("{serverip}", safeServerIp)
			.Replace("{serverport}", serverPort.ToString())
			.Replace("{testmode}", testMode.ToString().ToLowerInvariant())
			.Replace("{chatstyle}", chatStyle.ToString())
			.Replace("{charapp}", safeCharacter)
			.Replace("{baseurl}", safeBaseUrl)
			.Replace("{membershiptype}", Config.Instance.User.Player.Membership.ToString());
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("Studio.ashx")]
	public IActionResult Studio()
	{
		string scriptFromPath = GetScriptFromPath(ClientPaths.StudioScript);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("GameServer.ashx")]
	public IActionResult GameServer()
	{
		string scriptFromPath = GetScriptFromPath(ClientPaths.GameServerScript);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("Host.ashx")]
	public IActionResult Host(ushort serverPort = 53640)
	{
		if (serverPort == 0 || !TryEscapeScriptString(GetBaseUrl(), 2048, out string safeBaseUrl))
		{
			return BadRequest();
		}
		string scriptFromPath = GetScriptFromPath(ClientPaths.HostScript);
		scriptFromPath = scriptFromPath.Replace("{port}", serverPort.ToString()).Replace("{baseurl}", safeBaseUrl);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("Visit.ashx")]
	public IActionResult Visit()
	{
		if (!TryEscapeScriptString(UrlConstructor.GetCharacterAppearanceUrl(), 16384, out string safeCharacter))
		{
			return BadRequest();
		}
		string scriptFromPath = GetScriptFromPath(ClientPaths.VisitScript);
		scriptFromPath = scriptFromPath.Replace("{userid}", 0.ToString()).Replace("{username}", "Player").Replace("{charapp}", safeCharacter)
			.Replace("{membershiptype}", Config.Instance.User.Player.Membership.ToString());
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("LoadPlaceInfo.ashx")]
	public IActionResult LoadPlaceInfo()
	{
		string scriptFromPath = GetScriptFromPath(ClientPaths.LoadPlaceInfoScript);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("PlaceSpecificScript.ashx")]
	public IActionResult PlaceSpecificScript([FromQuery] int placeId)
	{
		if (placeId <= 0)
		{
			return BadRequest();
		}
		string scriptFromPath = GetScriptFromPath(ClientPaths.PlaceSpecificScript);
		scriptFromPath = scriptFromPath.Replace("{id}", placeId.ToString());
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("AutoSave.ashx")]
	public IActionResult AutoSave()
	{
		string scriptFromPath = GetScriptFromPath(ClientPaths.AutoSaveScript);
		scriptFromPath = scriptFromPath.Replace("{enabled}", Config.Instance.User.Launch.AutoSaveEnabled.ToString().ToLowerInvariant()).Replace("{changesperplayer}", Config.Instance.User.Launch.AutoSaveChangesPerPlayer.ToString()).Replace("{savecheckinterval}", Config.Instance.User.Launch.AutoSaveCheckInterval.ToString())
			.Replace("{savecooldown}", Config.Instance.User.Launch.AutoSaveCooldown.ToString())
			.Replace("{debug}", Config.Instance.User.Launch.AutoSaveDebug.ToString().ToLowerInvariant());
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("EggHuntScript.ashx")]
	public IActionResult EggHunt()
	{
		string scriptFromPath = GetScriptFromPath(ClientPaths.EggHuntScript);
		scriptFromPath = scriptFromPath.Replace("{enabled}", Config.Instance.User.Launch.EggHuntMode.ToString().ToLowerInvariant());
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("RenderCharacter.ashx")]
	public IActionResult RenderCharacter([FromQuery] string? charapp = null, [FromQuery] string? outputDir = null)
	{
		if (charapp == null)
		{
			charapp = "";
		}
		if (outputDir == null)
		{
			outputDir = "";
		}
		if (!Config.Instance.IsRenderMode)
		{
			return Forbid();
		}
		if (!TryEscapeScriptString(charapp, 65536, out string safeCharacter) || !TryEscapeScriptString(outputDir, 2048, out string safeOutput))
			return BadRequest();
		string scriptFromPath = GetScriptFromPath(Path.Combine(PathHelper.SharedScripts, "render_character.lua"));
		scriptFromPath = scriptFromPath.Replace("{charapp}", safeCharacter).Replace("{out}", safeOutput);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("RenderHat.ashx")]
	public IActionResult RenderHat([FromQuery] string? url = null, [FromQuery] string? outputDir = null)
	{
		if (url == null)
		{
			url = "";
		}
		if (outputDir == null)
		{
			outputDir = "";
		}
		if (!Config.Instance.IsRenderMode)
		{
			return Forbid();
		}
		if (!TryEscapeScriptString(url, 16384, out string safeUrl) || !TryEscapeScriptString(outputDir, 2048, out string safeOutput))
			return BadRequest();
		string scriptFromPath = GetScriptFromPath(Path.Combine(PathHelper.SharedScripts, "render_hat.lua"));
		scriptFromPath = scriptFromPath.Replace("{url}", safeUrl).Replace("{out}", safeOutput);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("RenderClothes.ashx")]
	public IActionResult RenderClothes([FromQuery] string? url = null, [FromQuery] string? outputDir = null)
	{
		if (url == null)
		{
			url = "";
		}
		if (outputDir == null)
		{
			outputDir = "";
		}
		if (!Config.Instance.IsRenderMode)
		{
			return Forbid();
		}
		if (!TryEscapeScriptString(url, 16384, out string safeUrl) || !TryEscapeScriptString(outputDir, 2048, out string safeOutput))
			return BadRequest();
		string scriptFromPath = GetScriptFromPath(Path.Combine(PathHelper.SharedScripts, "render_clothes.lua"));
		scriptFromPath = scriptFromPath.Replace("{url}", safeUrl).Replace("{out}", safeOutput);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("RenderHead.ashx")]
	public IActionResult RenderHead([FromQuery] string? url = null, [FromQuery] string? outputDir = null)
	{
		if (url == null)
		{
			url = "";
		}
		if (outputDir == null)
		{
			outputDir = "";
		}
		if (!Config.Instance.IsRenderMode)
		{
			return Forbid();
		}
		if (!TryEscapeScriptString(url, 16384, out string safeUrl) || !TryEscapeScriptString(outputDir, 2048, out string safeOutput))
			return BadRequest();
		string scriptFromPath = GetScriptFromPath(Path.Combine(PathHelper.SharedScripts, "render_head.lua"));
		scriptFromPath = scriptFromPath.Replace("{url}", safeUrl).Replace("{out}", safeOutput);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("RenderBodyPart.ashx")]
	public IActionResult RenderBodyPart([FromQuery] string? url = null, [FromQuery] string? outputDir = null)
	{
		if (url == null)
		{
			url = "";
		}
		if (outputDir == null)
		{
			outputDir = "";
		}
		if (!Config.Instance.IsRenderMode)
		{
			return Forbid();
		}
		if (!TryEscapeScriptString(url, 16384, out string safeUrl) || !TryEscapeScriptString(outputDir, 2048, out string safeOutput))
			return BadRequest();
		string scriptFromPath = GetScriptFromPath(Path.Combine(PathHelper.SharedScripts, "render_bodypart.lua"));
		scriptFromPath = scriptFromPath.Replace("{url}", safeUrl).Replace("{out}", safeOutput);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("RenderGear.ashx")]
	public IActionResult RenderGear([FromQuery] string? url = null, [FromQuery] string? outputDir = null)
	{
		if (url == null)
		{
			url = "";
		}
		if (outputDir == null)
		{
			outputDir = "";
		}
		if (!Config.Instance.IsRenderMode)
		{
			return Forbid();
		}
		if (!TryEscapeScriptString(url, 16384, out string safeUrl) || !TryEscapeScriptString(outputDir, 2048, out string safeOutput))
			return BadRequest();
		string scriptFromPath = GetScriptFromPath(Path.Combine(PathHelper.SharedScripts, "render_gear.lua"));
		scriptFromPath = scriptFromPath.Replace("{url}", safeUrl).Replace("{out}", safeOutput);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("RenderModel.ashx")]
	public IActionResult RenderModel([FromQuery] string? url = null, [FromQuery] string? outputDir = null)
	{
		if (url == null)
		{
			url = "";
		}
		if (outputDir == null)
		{
			outputDir = "";
		}
		if (!Config.Instance.IsRenderMode)
		{
			return Forbid();
		}
		if (!TryEscapeScriptString(url, 16384, out string safeUrl) || !TryEscapeScriptString(outputDir, 2048, out string safeOutput))
			return BadRequest();
		string scriptFromPath = GetScriptFromPath(Path.Combine(PathHelper.SharedScripts, "render_model.lua"));
		scriptFromPath = scriptFromPath.Replace("{url}", safeUrl).Replace("{out}", safeOutput);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}

	[HttpGet("RenderPackage.ashx")]
	public IActionResult RenderPackage([FromQuery] ulong id = 0uL, [FromQuery] string? outputDir = null)
	{
		if (outputDir == null)
		{
			outputDir = "";
		}
		if (!Config.Instance.IsRenderMode)
		{
			return Forbid();
		}
		if (!TryEscapeScriptString(outputDir, 2048, out string safeOutput))
			return BadRequest();
		string scriptFromPath = GetScriptFromPath(Path.Combine(PathHelper.SharedScripts, "render_package.lua"));
		AvatarItem byId = AvatarItems.GetById(id);
		if (byId == null)
		{
			return BadRequest();
		}
		string text = "";
		for (int i = 0; i < byId.Items.Count; i++)
		{
			AvatarItem byId2 = AvatarItems.GetById(byId.Items[i]);
			if (byId2 == null)
			{
				return BadRequest();
			}
			string text2 = ((i != 0) ? "," : "");
			text2 += $"\"http://www.roblox.com/asset/?id={byId2.Id}&version={byId2.AssetVersion}\"";
			text += text2;
		}
		scriptFromPath = scriptFromPath.Replace("{pkg}", text).Replace("{out}", safeOutput);
		return Ok(ScriptSigner.Sign(scriptFromPath, 0uL));
	}
}
