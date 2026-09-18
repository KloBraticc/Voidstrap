using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace Voidstrap.UI.Elements.Bootstrapper;

public static class AudioPlayerHelper
{
	private static readonly string[] LinuxPlayers = new[]
	{
		"gst-play-1.0",
		"ffplay",
		"mpv",
		"pw-play",
		"paplay"
	};

	private static MediaPlayer? _player;
	private static Process? _linuxPlayer;

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

	public static bool IsSupported
	{
		get
		{
			if (Voidstrap.Utility.Platform.IsWindows)
				return true;

			return Voidstrap.Utility.Platform.IsLinux && ResolveLinuxPlayer() is not null;
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
			string? text = Directory.Exists(Paths.Media)
				? Directory.GetFiles(Paths.Media, "startup_audio.*").FirstOrDefault()
				: null;
			if (text == null)
			{
				return;
			}
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				PlayLinuxStartupAudio(text);
				return;
			}
			MediaPlayer? player = Player;
			player?.Stop();
			player?.Open(new Uri(text, UriKind.Absolute));
			if (player != null) { player.Volume = 0.3; }
			player?.Play();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("AudioPlayerHelper::PlayStartupAudio", ex);
		}
	}

	public static void StopAudio()
	{
		StopLinuxStartupAudio();

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

	private static void PlayLinuxStartupAudio(string file)
	{
		StopLinuxStartupAudio();

		(string Name, string Executable)? resolved = ResolveLinuxPlayer();
		if (resolved is null)
		{
			App.Logger?.WriteLine(
				"AudioPlayerHelper::PlayLinuxStartupAudio",
				"No supported audio player was found, install the GStreamer tools to enable the startup sound");
			return;
		}

		try
		{
			ProcessStartInfo info = CreateLinuxPlayerStartInfo(resolved.Value.Name, resolved.Value.Executable, file);
			Process? process = Process.Start(info);
			if (process is null)
			{
				return;
			}

			_linuxPlayer = process;
			process.EnableRaisingEvents = true;
			process.Exited += OnLinuxPlayerExited;
			_ = process.StandardOutput.ReadToEndAsync();
			_ = process.StandardError.ReadToEndAsync();
			App.Logger?.WriteLine(
				"AudioPlayerHelper::PlayLinuxStartupAudio",
				"Playing the startup sound with " + resolved.Value.Name);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("AudioPlayerHelper::PlayLinuxStartupAudio", ex);
		}
	}

	private static void OnLinuxPlayerExited(object? sender, EventArgs e)
	{
		if (sender is not Process process)
		{
			return;
		}

		process.Exited -= OnLinuxPlayerExited;
		if (ReferenceEquals(_linuxPlayer, process))
		{
			_linuxPlayer = null;
		}

		try
		{
			process.Dispose();
		}
		catch (Exception)
		{
		}
	}

	private static void StopLinuxStartupAudio()
	{
		Process? process = _linuxPlayer;
		_linuxPlayer = null;
		if (process is null)
		{
			return;
		}

		process.Exited -= OnLinuxPlayerExited;
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (Exception)
		{
		}

		try
		{
			process.Dispose();
		}
		catch (Exception)
		{
		}
	}

	private static (string Name, string Executable)? ResolveLinuxPlayer()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return null;
		}

		try
		{
			Voidstrap.Core.SystemProcessService service = new();
			foreach (string name in LinuxPlayers)
			{
				string? executable = service.FindExecutable(name);
				if (!string.IsNullOrWhiteSpace(executable))
				{
					return (name, executable);
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("AudioPlayerHelper::ResolveLinuxPlayer", "Audio unavailable: " + ex.Message);
		}

		return null;
	}

	private static ProcessStartInfo CreateLinuxPlayerStartInfo(string name, string executable, string file)
	{
		ProcessStartInfo info = new(executable)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		switch (name)
		{
			case "gst-play-1.0":
				info.ArgumentList.Add("--no-interactive");
				info.ArgumentList.Add("--quiet");
				info.ArgumentList.Add("--volume=0.3");
				break;
			case "ffplay":
				info.ArgumentList.Add("-nodisp");
				info.ArgumentList.Add("-autoexit");
				info.ArgumentList.Add("-loglevel");
				info.ArgumentList.Add("quiet");
				info.ArgumentList.Add("-volume");
				info.ArgumentList.Add("30");
				break;
			case "mpv":
				info.ArgumentList.Add("--no-video");
				info.ArgumentList.Add("--really-quiet");
				info.ArgumentList.Add("--volume=30");
				break;
			case "pw-play":
				info.ArgumentList.Add("--volume=0.3");
				break;
		}

		info.ArgumentList.Add(file);
		return info;
	}
}
