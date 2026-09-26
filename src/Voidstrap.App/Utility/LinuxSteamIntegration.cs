using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class LinuxSteamIntegration
{
	private const string LOG_IDENT = "LinuxSteamIntegration";
	public const string RobloxShortcutName = "Roblox (Voidstrap)";
	public const string VoidstrapShortcutName = "Voidstrap";
	private static readonly TimeSpan SteamExitTimeout = TimeSpan.FromSeconds(30);

	public static bool IsSteamInstalled => Platform.IsLinux && LinuxSteamLibrary.FindSteamRoots().Count > 0;

	public static async Task<string> AddToSteamAsync(byte[]? iconPng, bool closeSteam, CancellationToken cancellationToken)
	{
		IReadOnlyList<string> roots = LinuxSteamLibrary.FindSteamRoots();
		if (roots.Count == 0)
			return "Steam was not found on this computer";

		bool steamWasRunning = LinuxSteamLibrary.IsSteamRunning();
		if (steamWasRunning)
		{
			if (!closeSteam)
				return "Steam has to be closed while Voidstrap updates your library";
			App.Logger?.WriteLine(LOG_IDENT, "Closing Steam so its library file can be updated");
			if (!await CloseSteamAsync(roots, cancellationToken).ConfigureAwait(false))
				return "Steam did not close in time, close it yourself and try again";
		}

		List<string> summaries = [];
		try
		{
			foreach (string root in roots)
			{
				IReadOnlyList<SteamShortcutRequest> requests = BuildRequests(root, iconPng);
				SteamShortcutResult result = LinuxSteamLibrary.AddOrUpdate(root, requests);
				App.Logger?.WriteLine(LOG_IDENT, root + ": " + result.Message);
				if (result.Success)
					summaries.Add(result.Message);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException(LOG_IDENT, ex);
			summaries.Clear();
			summaries.Add("Voidstrap could not update the Steam library: " + ex.Message);
		}
		finally
		{
			if (steamWasRunning)
				StartSteam(roots[0]);
		}

		if (summaries.Count == 0)
			return "No signed in Steam account was found, open Steam and sign in once first";
		return RobloxShortcutName + " and " + VoidstrapShortcutName + " are in your Steam library. " + string.Join(". ", summaries);
	}

	private static IReadOnlyList<SteamShortcutRequest> BuildRequests(string steamRoot, byte[]? iconPng)
	{
		string executable;
		string startDirectory;
		string prefix;
		string flatpakAppId = string.Empty;
		if (LinuxFlatpakHost.IsSandboxed)
		{
			executable = Quote("/usr/bin/flatpak");
			startDirectory = Quote("/usr/bin");
			flatpakAppId = LinuxFlatpakHost.CurrentApplicationId;
			prefix = "run " + flatpakAppId;
		}
		else
		{
			string path = LinuxDesktopEntry.ResolveLaunchPath(Paths.Application);
			if (LinuxSteamLibrary.IsFlatpakSteam(steamRoot))
			{
				executable = Quote("/usr/bin/flatpak-spawn");
				startDirectory = Quote(Path.GetDirectoryName(path) ?? "/");
				prefix = "--host " + Quote(path);
			}
			else
			{
				executable = Quote(path);
				startDirectory = Quote(Path.GetDirectoryName(path) ?? "/");
				prefix = string.Empty;
			}
		}

		return
		[
			new SteamShortcutRequest(RobloxShortcutName, executable, startDirectory, Join(prefix, "-player"), flatpakAppId, BuildArtwork(iconPng, true)),
			new SteamShortcutRequest(VoidstrapShortcutName, executable, startDirectory, Join(prefix, string.Empty), flatpakAppId, BuildArtwork(iconPng, false))
		];
	}

	private static IReadOnlyDictionary<string, byte[]> BuildArtwork(byte[]? iconPng, bool playBadge)
	{
		Dictionary<string, byte[]> art = [];
		if (iconPng is null || iconPng.Length == 0)
			return art;
		try
		{
			using Image<Rgba32> icon = Image.Load<Rgba32>(iconPng);
			art["p.png"] = Render(icon, 600, 900, 360, 0.42, playBadge, true);
			art[".png"] = Render(icon, 920, 430, 280, 0.5, playBadge, true);
			art["_hero.png"] = Render(icon, 1920, 620, 0, 0.5, false, true);
			art["_logo.png"] = Render(icon, 640, 360, 300, 0.5, playBadge, false);
			art["_icon.png"] = Render(icon, 256, 256, 256, 0.5, playBadge, false);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The Steam artwork could not be drawn: " + ex.Message);
		}
		return art;
	}

	private static byte[] Render(Image<Rgba32> icon, int width, int height, int iconSize, double verticalCenter, bool playBadge, bool background)
	{
		using Image<Rgba32> canvas = new(width, height, background ? new Rgba32(14, 15, 20, 255) : new Rgba32(0, 0, 0, 0));
		if (background)
		{
			canvas.ProcessPixelRows(rows =>
			{
				for (int y = 0; y < rows.Height; y++)
				{
					Span<Rgba32> row = rows.GetRowSpan(y);
					double fade = (double)y / Math.Max(1, rows.Height - 1);
					for (int x = 0; x < row.Length; x++)
					{
						double dx = (x - width * 0.5) / width;
						double dy = (y - height * verticalCenter) / height;
						double glow = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) * 1.8);
						row[x] = new Rgba32(
							(byte)Math.Clamp(22 - fade * 10 + glow * 40, 0, 255),
							(byte)Math.Clamp(24 - fade * 10 + glow * 30, 0, 255),
							(byte)Math.Clamp(32 - fade * 12 + glow * 70, 0, 255),
							255);
					}
				}
			});
		}

		if (iconSize > 0)
		{
			using Image<Rgba32> scaled = icon.Clone(context => context.Resize(iconSize, iconSize));
			int left = (width - iconSize) / 2;
			int top = (int)Math.Round(height * verticalCenter - iconSize / 2.0);
			canvas.Mutate(context => context.DrawImage(scaled, new Point(left, Math.Max(0, top)), 1f));
			if (playBadge)
				DrawPlayBadge(canvas, left + iconSize - iconSize / 4, Math.Max(0, top) + iconSize - iconSize / 4, Math.Max(18, iconSize / 5));
		}

		using MemoryStream buffer = new();
		canvas.SaveAsPng(buffer);
		return buffer.ToArray();
	}

	private static void DrawPlayBadge(Image<Rgba32> canvas, int centerX, int centerY, int radius)
	{
		canvas.ProcessPixelRows(rows =>
		{
			for (int y = Math.Max(0, centerY - radius); y < Math.Min(rows.Height, centerY + radius); y++)
			{
				Span<Rgba32> row = rows.GetRowSpan(y);
				for (int x = Math.Max(0, centerX - radius); x < Math.Min(row.Length, centerX + radius); x++)
				{
					double dx = x - centerX;
					double dy = y - centerY;
					double distance = Math.Sqrt(dx * dx + dy * dy);
					if (distance > radius)
						continue;
					double edge = Math.Clamp(radius - distance, 0, 1);
					bool inTriangle = dx > -radius * 0.28 && dx < radius * 0.42 && Math.Abs(dy) < (radius * 0.42 - dx) * 0.72;
					Rgba32 fill = inTriangle ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 176, 111, 255);
					Rgba32 current = row[x];
					row[x] = new Rgba32(
						(byte)(current.R + (fill.R - current.R) * edge),
						(byte)(current.G + (fill.G - current.G) * edge),
						(byte)(current.B + (fill.B - current.B) * edge),
						(byte)Math.Max(current.A, 255 * edge));
				}
			}
		});
	}

	private static async Task<bool> CloseSteamAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken)
	{
		bool flatpak = LinuxSteamLibrary.IsFlatpakSteam(roots[0]);
		RunDetached(flatpak ? "flatpak" : "steam", flatpak ? ["run", "com.valvesoftware.Steam", "-shutdown"] : ["-shutdown"]);
		DateTime deadline = DateTime.UtcNow + SteamExitTimeout;
		while (DateTime.UtcNow < deadline)
		{
			await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
			if (!LinuxSteamLibrary.IsSteamRunning())
			{
				await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
				return true;
			}
		}
		return false;
	}

	private static void StartSteam(string root)
	{
		bool flatpak = LinuxSteamLibrary.IsFlatpakSteam(root);
		App.Logger?.WriteLine(LOG_IDENT, "Opening Steam again");
		RunDetached(flatpak ? "flatpak" : "steam", flatpak ? ["run", "com.valvesoftware.Steam"] : []);
	}

	private static void RunDetached(string command, IReadOnlyList<string> arguments)
	{
		try
		{
			bool sandboxed = LinuxFlatpakHost.IsSandboxed;
			ProcessStartInfo info = new(sandboxed ? "flatpak-spawn" : "setsid")
			{
				UseShellExecute = false,
				CreateNoWindow = true
			};
			if (sandboxed)
				info.ArgumentList.Add("--host");
			else
				info.ArgumentList.Add("-f");
			info.ArgumentList.Add(command);
			foreach (string argument in arguments)
				info.ArgumentList.Add(argument);
			using Process? process = Process.Start(info);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "Could not run " + command + ": " + ex.Message);
		}
	}

	private static string Quote(string value)
	{
		return "\"" + value + "\"";
	}

	private static string Join(string prefix, string argument)
	{
		if (prefix.Length == 0)
			return argument;
		return argument.Length == 0 ? prefix : prefix + " " + argument;
	}
}
