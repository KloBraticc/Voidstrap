using System.Collections.Generic;
using System.Linq;
using Voidstrap.Enums;
using Voidstrap.Utility;

namespace Voidstrap.Models.SettingTasks;

public sealed class CursorPresetTask : EnumModPresetTask<CursorType>
{
	public CursorPresetTask()
		: base("CursorType", CreateMap())
	{
	}

	internal static Dictionary<CursorType, Dictionary<string, string>> CreateMap()
	{
		return new Dictionary<CursorType, Dictionary<string, string>>
		{
			{
				CursorType.VoidstrapDefault,
				VoidstrapDefaultCursor.Files.ToDictionary(item => item.Key, item => item.Value)
			},
			{
				CursorType.DotCursor,
				CreateCursorMap("DotCursor")
			},
			{
				CursorType.WhiteDotCursor,
				CreateCursorMap("WhiteDotCursor")
			},
			{
				CursorType.VerySmallWhiteDot,
				CreateCursorMap("VerySmallWhiteDot")
			},
			{
				CursorType.StoofsCursor,
				CreateCursorMap("StoofsCursor")
			},
			{
				CursorType.CleanCursor,
				CreateCursorMap("CleanCursor")
			},
			{
				CursorType.FPSCursor,
				CreateCursorMap("FPSCursor")
			},
			{
				CursorType.From2006,
				CreateCursorMap("From2006")
			},
			{
				CursorType.From2013,
				CreateCursorMap("From2013")
			}
		};
	}

	private static Dictionary<string, string> CreateCursorMap(string preset)
	{
		return new Dictionary<string, string>
		{
			{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor." + preset + ".ArrowCursor.png" },
			{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor." + preset + ".ArrowFarCursor.png" },
			{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor." + preset + ".ArrowCursorDecalDrag.png" }
		};
	}
}
