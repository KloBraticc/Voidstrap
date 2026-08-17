using System;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace Voidstrap.UI.Elements.Bootstrapper;

public static class AudioPlayerHelper
{
	private static MediaPlayer? _player;

	private static MediaPlayer? Player
	{
		get
		{
			if (!Voidstrap.Utility.Platform.IsWindows)
			{
				return null;
			}
			try
			{
				return _player ??= new MediaPlayer();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("AudioPlayerHelper::Player", "Audio unavailable: " + ex.Message);
				return null;
			}
		}
	}

	public static void PlayStartupAudio()
	{
		if (App.Settings.Prop.BootstrapperStyle == Voidstrap.Enums.BootstrapperStyle.CustomDialog)
		{
			StopAudio();
			return;
		}
		try
		{
			string text = Directory.GetFiles(Paths.Media, "startup_audio.*").FirstOrDefault();
			if (text != null)
			{
				MediaPlayer? player = Player;
				player?.Stop();
				player?.Open(new Uri(text, UriKind.Absolute));
				if (player != null) { player.Volume = 0.3; }
				player?.Play();
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("AudioPlayerHelper::PlayStartupAudio", ex);
		}
	}

	public static void StopAudio()
	{
		MediaPlayer? player = _player;
		_player = null;
		try
		{
			player?.Stop();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("AudioPlayerHelper::StopAudio", ex);
		}
		try
		{
			player?.Close();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("AudioPlayerHelper::StopAudio", ex);
		}
	}
}
