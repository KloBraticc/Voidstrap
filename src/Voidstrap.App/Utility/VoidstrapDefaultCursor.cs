using Voidstrap.Enums;

namespace Voidstrap.Utility;

internal static class VoidstrapDefaultCursor
{
	private const string LegacyShiftlockHash = "22f8f40bc1289be24fe30afd32ea75de";

	internal static IReadOnlyDictionary<string, string> Files { get; } = new Dictionary<string, string>
	{
		{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.BibataModernIce.ArrowCursor.png" },
		{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.BibataModernIce.ArrowFarCursor.png" },
		{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.BibataModernIce.ArrowCursorDecalDrag.png" },
		{ "content\\textures\\Cursors\\KeyboardMouse\\IBeamCursor.png", "Cursor.BibataModernIce.IBeamCursor.png" }
	};

	internal static void Apply()
	{
		EnsureSelection();
		RemoveLegacyShiftlock();
		if (App.Settings.Prop.CursorType != CursorType.VoidstrapDefault)
			return;

		foreach ((string relativePath, string resourceName) in Files)
		{
			string destination = Path.Combine(Paths.Mods, relativePath);
			try
			{
				byte[] resource = Resource.Get(resourceName);
				if (File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(resource))
					continue;

				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				Filesystem.AssertReadOnly(destination);
				File.WriteAllBytes(destination, resource);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("VoidstrapDefaultCursor", "Could not apply " + Path.GetFileName(destination) + ": " + ex.Message);
			}
		}
	}

	private static void RemoveLegacyShiftlock()
	{
		string destination = Path.Combine(Paths.Mods, "content", "textures", "MouseLockedCursor.png");
		try
		{
			if (!File.Exists(destination) || !string.Equals(MD5Hash.FromFile(destination), LegacyShiftlockHash, StringComparison.OrdinalIgnoreCase))
				return;
			Filesystem.AssertReadOnly(destination);
			File.Delete(destination);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("VoidstrapDefaultCursor", "Could not remove the previous shift lock cursor: " + ex.Message);
		}
	}

	internal static void EnsureSelection()
	{
		if (App.Settings.Prop.HasSelectedCursorType)
			return;
		App.Settings.Prop.CursorType = CursorType.VoidstrapDefault;
		App.Settings.Prop.HasSelectedCursorType = true;
		App.Settings.SaveDeferred();
	}
}
