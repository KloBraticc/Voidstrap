using System;
using System.Collections.Generic;
using System.IO;

namespace Voidstrap.Integrations.CommunityMods;

internal static class RobloxContentPlacer
{
	private static readonly char Separator = Path.DirectorySeparatorChar;

	private static readonly HashSet<string> ContentRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"content",
		"ExtraContent",
		"PlatformContent",
		"shaders"
	};

	private static readonly Dictionary<string, string> FolderAnchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["KeyboardMouse"] = "content\\textures\\Cursors\\KeyboardMouse",
		["DragDetector"] = "content\\textures\\Cursors\\DragDetector",
		["Cursors"] = "content\\textures\\Cursors",
		["families"] = "content\\fonts\\families",
		["particles"] = "content\\textures\\particles",
		["sky"] = "PlatformContent\\pc\\textures\\sky",
		["textures"] = "content\\textures",
		["sounds"] = "content\\sounds",
		["fonts"] = "content\\fonts",
		["music"] = "content\\music",
		["models"] = "content\\models",
		["avatar"] = "content\\avatar"
	};

	private static readonly Dictionary<string, string> KnownFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["ArrowCursor.png"] = "content\\textures\\Cursors\\KeyboardMouse",
		["ArrowFarCursor.png"] = "content\\textures\\Cursors\\KeyboardMouse",
		["IBeamCursor.png"] = "content\\textures\\Cursors\\KeyboardMouse",
		["ArrowCursorDecalDrag.png"] = "content\\textures\\Cursors\\KeyboardMouse",
		["ActivatedCursor.png"] = "content\\textures\\Cursors\\DragDetector",
		["HoverCursor.png"] = "content\\textures\\Cursors\\DragDetector",
		["MouseLockedCursor.png"] = "content\\textures",
		["sky512_bk.tex"] = "PlatformContent\\pc\\textures\\sky",
		["sky512_dn.tex"] = "PlatformContent\\pc\\textures\\sky",
		["sky512_ft.tex"] = "PlatformContent\\pc\\textures\\sky",
		["sky512_lf.tex"] = "PlatformContent\\pc\\textures\\sky",
		["sky512_rt.tex"] = "PlatformContent\\pc\\textures\\sky",
		["sky512_up.tex"] = "PlatformContent\\pc\\textures\\sky",
		["oof.ogg"] = "content\\sounds",
		["ouch.ogg"] = "content\\sounds"
	};

	private static readonly Dictionary<string, string> KnownPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["img_set_"] = "ExtraContent\\LuaPackages\\Packages\\_Index\\FoundationImages\\FoundationImages\\SpriteSheets"
	};

	public static string? Resolve(string relative)
	{
		if (string.IsNullOrWhiteSpace(relative))
		{
			return null;
		}
		string[] segments = relative.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0)
		{
			return null;
		}

		for (int index = 0; index < segments.Length - 1; index++)
		{
			if (ContentRoots.Contains(segments[index]))
			{
				return string.Join(Separator, segments[index..]);
			}
		}

		string? candidate = null;
		for (int index = segments.Length - 2; index >= 0 && candidate == null; index--)
		{
			if (FolderAnchors.TryGetValue(segments[index], out string? anchor))
			{
				candidate = anchor + Separator + string.Join(Separator, segments[(index + 1)..]);
			}
		}

		string name = segments[^1];
		if (candidate == null && KnownFiles.TryGetValue(name, out string? folder))
		{
			candidate = folder + Separator + name;
		}
		if (candidate == null)
		{
			foreach (KeyValuePair<string, string> prefix in KnownPrefixes)
			{
				if (name.StartsWith(prefix.Key, StringComparison.OrdinalIgnoreCase))
				{
					candidate = prefix.Value + Separator + name;
					break;
				}
			}
		}
		if (candidate != null && Voidstrap.Utility.RobloxLayoutRepair.ExistsInClient(candidate))
		{
			return candidate;
		}
		string? fromClient = Voidstrap.Utility.RobloxLayoutRepair.ResolveUniqueName(name);
		return fromClient ?? candidate;
	}
}
