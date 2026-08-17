using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.Utility;

namespace Voidstrap.Models.SettingTasks;

public class EmojiModPresetTask : EnumBaseTask<EmojiType>
{
	private string _filePath => Path.Combine(Paths.Mods, "content", "fonts", "TwemojiMozilla.ttf");

	private IEnumerable<KeyValuePair<EmojiType, string>>? QueryCurrentValue()
	{
		if (!File.Exists(_filePath))
		{
			return null;
		}
		using FileStream stream = File.OpenRead(_filePath);
		string hash = Convert.ToHexString(App.ComputeSha256(stream));
		return EmojiTypeEx.Hashes.Where<KeyValuePair<EmojiType, string>>((KeyValuePair<EmojiType, string> x) => x.Value == hash);
	}

	public EmojiModPresetTask()
		: base("ModPreset", "EmojiFont")
	{
		IEnumerable<KeyValuePair<EmojiType, string>> enumerable = QueryCurrentValue();
		if (enumerable != null)
		{
			OriginalState = enumerable.FirstOrDefault().Key;
		}
	}

	public override void Execute()
	{
		ExecuteAsync().ConfigureAwait(false).GetAwaiter().GetResult();
	}

	public override async Task ExecuteAsync()
	{
		IEnumerable<KeyValuePair<EmojiType, string>> enumerable = QueryCurrentValue();
		if (NewState != EmojiType.Default && (enumerable == null || enumerable.FirstOrDefault().Key != NewState))
		{
			try
			{
				string? directory = Path.GetDirectoryName(_filePath);
				if (directory == null)
				{
					throw new InvalidDataException("The emoji font folder is unavailable");
				}
				Directory.CreateDirectory(directory);
				string temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".download";
				try
				{
					await ResilientDownload.DownloadAsync(App.HttpClient, [NewState.GetUrl()], temporary, EmojiTypeEx.Sizes[NewState], CancellationToken.None, "sha256:" + NewState.GetHash()).ConfigureAwait(false);
					Filesystem.AssertReadOnly(_filePath);
					File.Move(temporary, _filePath, true);
				}
				finally
				{
					if (File.Exists(temporary))
					{
						File.Delete(temporary);
					}
				}
				OriginalState = NewState;
				return;
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("EmojiModPresetTask::Execute", ex);
				Frontend.ShowConnectivityDialog(string.Format(Strings.Dialog_Connectivity_UnableToConnect, "GitHub"), Strings.Menu_Mods_Presets_EmojiType_Error + "\n\n" + Strings.Dialog_Connectivity_TryAgainLater, MessageBoxImage.Exclamation, ex);
				return;
			}
		}
		if (enumerable != null && enumerable.Any())
		{
			Filesystem.AssertReadOnly(_filePath);
			File.Delete(_filePath);
			OriginalState = NewState;
		}
	}
}
