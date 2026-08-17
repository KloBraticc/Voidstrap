using System;
using System.IO;
using System.Security.Cryptography;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.Utility;

namespace Voidstrap.Models.SettingTasks;

public class FontModPresetTask : StringBaseTask
{
	public FontModPresetTask()
		: base("ModPreset", "TextFont")
	{
		if (File.Exists(Paths.CustomFont))
		{
			OriginalState = Paths.CustomFont;
		}
	}

	public string? GetFileHash()
	{
		if (!File.Exists(Paths.CustomFont))
		{
			return null;
		}
		using FileStream inputStream = File.OpenRead(Paths.CustomFont);
		using MD5 mD = MD5.Create();
		return MD5Hash.Stringify(mD.ComputeHash(inputStream));
	}

	public static bool HasFont => File.Exists(Paths.CustomFont);

	public static void Rescale(string? pendingSource = null)
	{
		if (File.Exists(Paths.CustomFontSource))
		{
			Write(Paths.CustomFontSource);
		}
		else if (!string.IsNullOrEmpty(pendingSource) && File.Exists(pendingSource))
		{
			Write(pendingSource);
		}
	}

	private static bool Write(string sourcePath)
	{
		try
		{
			FileInfo source = new(sourcePath);
			if (!source.Exists || source.Length < 12 || source.Length > GoogleFontsService.MaximumFontBytes)
			{
				App.Logger.WriteLine("FontModPresetTask::Write", "The selected font size is invalid");
				return false;
			}
			string? directoryName = Path.GetDirectoryName(Paths.CustomFont);
			if (directoryName != null)
				Directory.CreateDirectory(directoryName);
			Filesystem.AssertReadOnly(Paths.CustomFont);
			FontScaler.TryScale(sourcePath, Paths.CustomFont, App.Settings.Prop.CustomFontScale);
			FileInfo output = new(Paths.CustomFont);
			return output.Exists && output.Length > 0 && output.Length <= GoogleFontsService.MaximumFontBytes;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("FontModPresetTask::Write", "Could not write the selected font: " + ex.Message);
			return false;
		}
	}

	private static void RememberSource(string sourcePath)
	{
		try
		{
			string? directoryName = Path.GetDirectoryName(Paths.CustomFontSource);
			if (directoryName != null)
			{
				Directory.CreateDirectory(directoryName);
			}
			File.Copy(sourcePath, Paths.CustomFontSource, overwrite: true);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("FontModPresetTask::RememberSource", "Could not keep a copy of the font: " + ex.Message);
		}
	}

	public override void Execute()
	{
		if (!string.IsNullOrEmpty(NewState) && !string.Equals(NewState, Paths.CustomFont, StringComparison.InvariantCultureIgnoreCase))
		{
			if (!File.Exists(NewState))
				throw new FileNotFoundException("The selected font source is no longer available", NewState);
			RememberSource(NewState);
			if (!Write(NewState))
				throw new IOException("The selected font could not be written");
		}
		else if (string.IsNullOrEmpty(NewState))
		{
			if (File.Exists(Paths.CustomFont))
			{
				Filesystem.AssertReadOnly(Paths.CustomFont);
				File.Delete(Paths.CustomFont);
			}
			CustomFontMod.RemoveGeneratedFamilies();
			if (File.Exists(Paths.CustomFontSource))
			{
				try
				{
					File.Delete(Paths.CustomFontSource);
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("FontModPresetTask::Execute", "Could not remove the stored font: " + ex.Message);
				}
			}
		}
		OriginalState = NewState;
	}
}
