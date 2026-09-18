using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Integrations;
using Voidstrap.Integrations.CommunityMods;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.ClassicTopBar;

internal sealed class ClassicTopBarState
{
	public string NoGuiModId { get; set; } = "";

	public string Ps4ButtonsModId { get; set; } = "";

	public Dictionary<string, string> RobloxSettings { get; set; } = new(StringComparer.Ordinal);
}

internal static partial class ClassicTopBarMod
{
	public const long InterfaceModId = 711267;

	public const long InterfaceFileId = 1817207;

	public const string InterfaceAuthor = "HockuTM";

	public const string InterfaceUrl = "https://gamebanana.com/mods/711267";

	public const long NoGuiModId = 620117;

	public const long NoGuiFileId = 1770017;

	public const string NoGuiAuthor = "Einshine";

	public const string NoGuiUrl = "https://gamebanana.com/mods/620117";

	public const string NoGuiModName = "No TopBar (NoGuiRBX)";

	private const string LogIdent = "ClassicTopBarMod";

	private static readonly string[] RequiredAssets =
	{
		"Burger.png", "BurgerOpen.png", "Chat.png", "ChatOpen.png", "Backpack.png", "BackpackOpen.png",
		"Resume.png", "Reset.png", "Settings.png", "Help.png", "Screenshot.png", "Report.png", "Record.png",
		"Leave.png", "Cancel.png", "Confirm.png", "Back.png", "ButtonOff.png", "ButtonOn.png"
	};

	private static readonly string[] ImageExtensions = { ".png" };

	private static readonly string[] NoGuiExtensions = { ".ttf", ".otf", ".xml" };

	private static readonly Dictionary<string, string> NoGuiSettings = new(StringComparer.Ordinal)
	{
		["PreferredTransparency"] = "225",
		["ChatVisible"] = "false",
		["PlayerListVisible"] = "false"
	};

	private static readonly Dictionary<string, string> RobloxDefaults = new(StringComparer.Ordinal)
	{
		["PreferredTransparency"] = "1",
		["ChatVisible"] = "true",
		["PlayerListVisible"] = "true"
	};

	private static readonly EnumerationOptions SearchOptions = new EnumerationOptions
	{
		RecurseSubdirectories = true,
		IgnoreInaccessible = true,
		MatchCasing = MatchCasing.CaseInsensitive,
		AttributesToSkip = FileAttributes.ReparsePoint
	};

	private static readonly int ParallelLimit = Math.Max(2, Environment.ProcessorCount);

	private static readonly ConcurrentDictionary<long, byte[]> BlankImages = new ConcurrentDictionary<long, byte[]>();

	private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

	public static string Root => Path.Combine(Paths.Data, "ClassicTopBar");

	public static string AssetFolder => Path.Combine(Root, "Interface");

	private static string NoGuiFolder => Path.Combine(Root, "NoGui");

	private static string StatePath => Path.Combine(Root, "State.json");

	private static string RobloxSettingsPath => App.GlobalSettings.FileLocation;

	public static bool AssetsReady => RequiredAssets.All(name => File.Exists(Path.Combine(AssetFolder, name)));

	public static string UserNamePath => Path.Combine(Root, "UserName.txt");

	public static string ReadUserName()
	{
		try
		{
			if (File.Exists(UserNamePath))
			{
				string saved = File.ReadAllText(UserNamePath).Trim();
				if (saved.Length > 0)
				{
					return saved;
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The saved name could not be read: " + ex.Message);
		}
		return "Player";
	}

	public static async Task<string> RefreshUserNameAsync(CancellationToken token)
	{
		try
		{
			RobloxAccount? account = await RobloxCookie.GetAccountAsync(token).ConfigureAwait(false);
			if (account != null && account.Username.Length > 0)
			{
				WriteUserName(account.Username);
				return account.Username;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The Roblox name could not be fetched: " + ex.Message);
		}
		return ReadUserName();
	}

	public static void WriteUserName(string name)
	{
		try
		{
			Directory.CreateDirectory(Root);
			File.WriteAllText(UserNamePath, (name ?? "").Trim());
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The name could not be saved: " + ex.Message);
		}
	}

	public static string? ResolveAsset(string name)
	{
		string path = Path.Combine(AssetFolder, name);
		return File.Exists(path) ? path : null;
	}

	public static async Task EnableAsync(IProgress<string>? progress, CancellationToken token)
	{
		await Gate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			Directory.CreateDirectory(Root);
			if (!AssetsReady)
			{
				progress?.Report("Downloading the 2014 to 2015 interface");
				await FetchInterfaceAsync(progress, token).ConfigureAwait(false);
			}
			ClassicTopBarState state = LoadState();
			try
			{
				token.ThrowIfCancellationRequested();
				progress?.Report("Applying the hidden topbar mod");
				await ApplyNoGuiAsync(state, progress, token).ConfigureAwait(false);
				token.ThrowIfCancellationRequested();
				SaveState(state);
			}
			catch (OperationCanceledException)
			{
				RevertNoGui(state);
				SaveState(state);
				throw;
			}
			App.Logger?.WriteLine(LogIdent, "The classic topbar is ready");
		}
		finally
		{
			Gate.Release();
		}
	}

	public const long Ps4ButtonsModId = 596647;

	public const long Ps4ButtonsFileId = 1445838;

	public const string Ps4ButtonsAuthor = "ArthuPortal";

	public const string Ps4ButtonsUrl = "https://gamebanana.com/mods/596647";

	public static async Task SetPs4ButtonsAsync(bool enabled, IProgress<string>? progress, CancellationToken token)
	{
		await Gate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			Directory.CreateDirectory(Root);
			ClassicTopBarState state = LoadState();
			string installedId = "";
			string enabledExistingId = "";
			bool enabledExistingState = false;
			try
			{
				token.ThrowIfCancellationRequested();
				if (enabled)
				{
					if (TryGetManagedMod(state.Ps4ButtonsModId, out ManagedModRecord? existing))
					{
						enabledExistingId = existing!.Id;
						enabledExistingState = existing.Enabled;
						ManagedModStore.SetEnabled(existing!.Id, true);
					}
					else
					{
						progress?.Report("Downloading the PS4 button icons");
						installedId = await GameBananaCatalog.InstallModAsync(Ps4ButtonsModId, Ps4ButtonsFileId, progress, token).ConfigureAwait(false);
						state.Ps4ButtonsModId = installedId;
					}
				}
				else if (TryGetManagedMod(state.Ps4ButtonsModId, out ManagedModRecord? record))
				{
					try
					{
						ManagedModStore.Delete(record!.Id);
					}
					catch (Exception ex)
					{
						App.Logger?.WriteLine(LogIdent, "The PS4 button mod could not be removed: " + ex.Message);
					}
					state.Ps4ButtonsModId = "";
				}
				else
				{
					state.Ps4ButtonsModId = "";
				}
				if (enabled)
				{
					token.ThrowIfCancellationRequested();
				}
				SaveState(state);
			}
			catch (OperationCanceledException)
			{
				if (installedId.Length > 0)
				{
					GameBananaCatalog.TryDeleteRecord(installedId);
				}
				if (enabledExistingId.Length > 0)
				{
					ManagedModStore.SetEnabled(enabledExistingId, enabledExistingState);
				}
				throw;
			}
			App.Logger?.WriteLine(LogIdent, enabled ? "The PS4 button icons are installed" : "The PS4 button icons were removed");
		}
		finally
		{
			Gate.Release();
		}
	}

	public static async Task SetHideCoreGuiAsync(bool enabled, IProgress<string>? progress, CancellationToken token)
	{
		await Gate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			Directory.CreateDirectory(Root);
			ClassicTopBarState state = LoadState();
			try
			{
				token.ThrowIfCancellationRequested();
				if (enabled)
				{
					progress?.Report("Hiding the Roblox interface");
					await ApplyNoGuiAsync(state, progress, token).ConfigureAwait(false);
				}
				else
				{
					RevertNoGui(state);
				}
				if (enabled)
				{
					token.ThrowIfCancellationRequested();
				}
				SaveState(state);
			}
			catch (OperationCanceledException)
			{
				if (enabled)
				{
					RevertNoGui(state);
					SaveState(state);
				}
				throw;
			}
			App.Logger?.WriteLine(LogIdent, enabled ? "Every CoreGui image is hidden" : "The CoreGui images were put back");
		}
		finally
		{
			Gate.Release();
		}
	}

	public static async Task DisableAsync(CancellationToken token)
	{
		await Gate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			ClassicTopBarState state = LoadState();
			RevertNoGui(state);
			SaveState(state);
			App.Logger?.WriteLine(LogIdent, "The classic topbar was turned off and Roblox was restored");
		}
		finally
		{
			Gate.Release();
		}
	}

	private static async Task FetchInterfaceAsync(IProgress<string>? progress, CancellationToken token)
	{
		string staging = Path.Combine(Root, "Staging");
		TryDeleteDirectory(staging);
		try
		{
			await GameBananaCatalog.FetchFileAsync(InterfaceModId, InterfaceFileId, staging, ImageExtensions, progress, token).ConfigureAwait(false);
			Directory.CreateDirectory(AssetFolder);
			int copied = 0;
			foreach (string name in RequiredAssets)
			{
				token.ThrowIfCancellationRequested();
				string? source = Directory
					.EnumerateFiles(staging, name, SearchOptions)
					.FirstOrDefault();
				if (source == null)
				{
					continue;
				}
				File.Copy(source, Writable(Path.Combine(AssetFolder, name)), overwrite: true);
				token.ThrowIfCancellationRequested();
				copied++;
			}
			if (!AssetsReady)
			{
				throw new InvalidDataException("The interface package is missing " + (RequiredAssets.Length - copied) + " of its images.");
			}
			App.Logger?.WriteLine(LogIdent, "Copied " + copied + " interface images from GameBanana mod " + InterfaceModId);
		}
		finally
		{
			TryDeleteDirectory(staging);
		}
	}

	private static readonly string[] CoreGuiTextureFolders =
	{
		"TopBar", "MenuBar", "InGameMenu", "Chat", "PlayerList", "Backpack", "Emotes",
		"Settings", "Menu", "Capture", "ScreenshotHud", "VoiceChat", "PurchasePrompt",
		"ErrorPrompt", "InspectMenu", "Moments", "common"
	};

	private static readonly string[] IconFontFolder =
	{
		"ExtraContent", "LuaPackages", "Packages", "_Index", "BuilderIcons", "BuilderIcons", "Font"
	};

	private static readonly string[] FoundationFolder =
	{
		"ExtraContent", "LuaPackages", "Packages", "_Index", "FoundationImages", "FoundationImages", "SpriteSheets"
	};

	public static string? FindRobloxInstall()
	{
		try
		{
			string? player = RobloxInstallCompression.PlayerFolder();
			if (player != null && File.Exists(Path.Combine(player, "RobloxPlayerBeta.exe")))
			{
				return player;
			}
			string root = Paths.Versions;
			if (!Directory.Exists(root))
			{
				return null;
			}
			return Directory
				.EnumerateDirectories(root)
				.Where(folder => File.Exists(Path.Combine(folder, "RobloxPlayerBeta.exe")))
				.OrderByDescending(folder => Directory.GetLastWriteTimeUtc(folder))
				.FirstOrDefault();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The Roblox install could not be found: " + ex.Message);
			return null;
		}
	}

	private static async Task ApplyNoGuiAsync(ClassicTopBarState state, IProgress<string>? progress, CancellationToken token)
	{
		if (!Directory.Exists(NoGuiFolder) || !Directory.EnumerateFiles(NoGuiFolder, "*.ttf", SearchOptions).Any())
		{
			TryDeleteDirectory(NoGuiFolder);
			await GameBananaCatalog.FetchFileAsync(NoGuiModId, NoGuiFileId, NoGuiFolder, NoGuiExtensions, progress, token).ConfigureAwait(false);
		}

		string? blankFont = Directory
			.EnumerateFiles(NoGuiFolder, "*.ttf", SearchOptions)
			.OrderByDescending(file => new FileInfo(file).Length)
			.FirstOrDefault();
		if (blankFont == null)
		{
			throw new InvalidDataException("The hidden topbar package no longer contains an icon font.");
		}

		if (!TryGetManagedMod(state.NoGuiModId, out ManagedModRecord? record))
		{
			record = ManagedModStore.Create(NoGuiModName);
			state.NoGuiModId = record.Id;
		}
		string modRoot = ManagedModStore.GetFolder(record!.Id);
		DiscardDirectory(Path.Combine(modRoot, "ExtraContent"));
		DiscardDirectory(Path.Combine(modRoot, "content"));

		string? install = FindRobloxInstall();
		int fonts = 0;
		int images = 0;
		if (install == null)
		{
			App.Logger?.WriteLine(LogIdent, "No Roblox install was found yet, falling back to the packaged files");
			string? packaged = Directory.EnumerateDirectories(NoGuiFolder, "ExtraContent", SearchOptions).FirstOrDefault();
			if (packaged != null)
			{
				CopyTree(packaged, Path.Combine(modRoot, "ExtraContent"), token);
				fonts = Directory.EnumerateFiles(packaged, "*", SearchOption.AllDirectories).Count();
			}
		}
		else
		{
			progress?.Report("Blanking the topbar icon fonts");
			fonts = BlankIconFonts(install, modRoot, blankFont, token);
			progress?.Report("Blanking the interface icons");
			images = BlankSpriteSheets(install, modRoot, token);
			if (App.Settings.Prop.ClassicTopBarHideAllCoreGui)
			{
				progress?.Report("Blanking every CoreGui image");
				images += BlankCoreGuiTextures(install, modRoot, token);
			}
		}

		token.ThrowIfCancellationRequested();
		ManagedModStore.SetEnabled(record.Id, true);
		App.Logger?.WriteLine(LogIdent, "Prepared the hidden topbar mod with " + fonts + " fonts and " + images + " images");
		TryPatchRobloxSettings(state);
		token.ThrowIfCancellationRequested();
	}

	private static int BlankIconFonts(string install, string modRoot, string blankFont, CancellationToken token)
	{
		string source = Path.Combine(new[] { install }.Concat(IconFontFolder).ToArray());
		if (!Directory.Exists(source))
		{
			return 0;
		}
		string destination = Path.Combine(new[] { modRoot }.Concat(IconFontFolder).ToArray());
		Directory.CreateDirectory(destination);
		int written = 0;
		foreach (string file in Directory.EnumerateFiles(source))
		{
			token.ThrowIfCancellationRequested();
			string extension = Path.GetExtension(file);
			if (!string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			File.Copy(blankFont, Writable(Path.Combine(destination, Path.GetFileName(file))), overwrite: true);
			token.ThrowIfCancellationRequested();
			written++;
		}
		return written;
	}

	private static int BlankSpriteSheets(string install, string modRoot, CancellationToken token)
	{
		string source = Path.Combine(new[] { install }.Concat(FoundationFolder).ToArray());
		if (!Directory.Exists(source))
		{
			return 0;
		}
		string destination = Path.Combine(new[] { modRoot }.Concat(FoundationFolder).ToArray());
		Directory.CreateDirectory(destination);
		List<(string Source, string Target)> work = new List<(string, string)>();
		foreach (string file in Directory.EnumerateFiles(source, "*.png"))
		{
			work.Add((file, Path.Combine(destination, Path.GetFileName(file))));
		}
		return WriteBlankCopies(work, token);
	}

	private static int BlankCoreGuiTextures(string install, string modRoot, CancellationToken token)
	{
		string root = Path.Combine(install, "content", "textures", "ui");
		if (!Directory.Exists(root))
		{
			return 0;
		}
		List<(string Source, string Target)> work = new List<(string, string)>();
		HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string folder in CoreGuiTextureFolders)
		{
			token.ThrowIfCancellationRequested();
			string source = Path.Combine(root, folder);
			if (!Directory.Exists(source))
			{
				continue;
			}
			foreach (string file in Directory.EnumerateFiles(source, "*.png", SearchOption.AllDirectories))
			{
				string target = Path.Combine(modRoot, Path.GetRelativePath(install, file));
				string? directory = Path.GetDirectoryName(target);
				if (directory != null && folders.Add(directory))
				{
					Directory.CreateDirectory(directory);
				}
				work.Add((file, target));
			}
		}
		return WriteBlankCopies(work, token);
	}

	private static int WriteBlankCopies(IReadOnlyList<(string Source, string Target)> work, CancellationToken token)
	{
		int written = 0;
		ParallelOptions options = new ParallelOptions { MaxDegreeOfParallelism = ParallelLimit, CancellationToken = token };
		Parallel.ForEach(work, options, item =>
		{
			token.ThrowIfCancellationRequested();
			if (!TryReadPngSize(item.Source, out int width, out int height))
			{
				return;
			}
			byte[]? bytes = BlankImage(width, height);
			if (bytes == null)
			{
				return;
			}
			try
			{
				File.WriteAllBytes(Writable(item.Target), bytes);
				token.ThrowIfCancellationRequested();
				Interlocked.Increment(ref written);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger?.WriteLine(LogIdent, "The image " + Path.GetFileName(item.Target) + " could not be written: " + ex.Message);
			}
		});
		return written;
	}

	private static byte[]? BlankImage(int width, int height)
	{
		long key = ((long)width << 32) | (uint)height;
		if (BlankImages.TryGetValue(key, out byte[]? cached))
		{
			return cached;
		}
		try
		{
			using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> blank = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
			using MemoryStream buffer = new MemoryStream();
			blank.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder
			{
				CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed
			});
			byte[] bytes = buffer.ToArray();
			BlankImages[key] = bytes;
			return bytes;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "A blank " + width + "x" + height + " image could not be made: " + ex.Message);
			return null;
		}
	}

	private static bool TryReadPngSize(string path, out int width, out int height)
	{
		width = 0;
		height = 0;
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			Span<byte> header = stackalloc byte[24];
			if (stream.Read(header) != header.Length)
			{
				return false;
			}
			if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
			{
				return false;
			}
			width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
			height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
			return width > 0 && height > 0 && width <= 8192 && height <= 8192;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The image " + Path.GetFileName(path) + " could not be measured: " + ex.Message);
			return false;
		}
	}

	private static void RevertNoGui(ClassicTopBarState state)
	{
		if (TryGetManagedMod(state.NoGuiModId, out ManagedModRecord? record))
		{
			try
			{
				ManagedModStore.Delete(record!.Id);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The hidden topbar mod could not be removed: " + ex.Message);
			}
		}
		state.NoGuiModId = "";
		TryRestoreRobloxSettings(state);
	}

	private static bool TryGetManagedMod(string id, out ManagedModRecord? record)
	{
		record = null;
		if (string.IsNullOrEmpty(id))
		{
			return false;
		}
		record = ManagedModStore.Load().FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
		return record != null;
	}

	private static void PatchRobloxSettings(ClassicTopBarState state)
	{
		string path = RobloxSettingsPath;
		if (!File.Exists(path))
		{
			App.Logger?.WriteLine(LogIdent, "Roblox has no saved settings file yet, the topbar will hide after the next launch");
			return;
		}
		string text = File.ReadAllText(path);
		string patched = text;
		foreach (KeyValuePair<string, string> setting in NoGuiSettings)
		{
			Match match = SettingPattern(setting.Key).Match(patched);
			if (!match.Success)
			{
				continue;
			}
			if (!state.RobloxSettings.ContainsKey(setting.Key))
			{
				string current = match.Groups[2].Value;
				state.RobloxSettings[setting.Key] = string.Equals(current, setting.Value, StringComparison.OrdinalIgnoreCase)
					&& RobloxDefaults.TryGetValue(setting.Key, out string? fallback)
					? fallback
					: current;
			}
			patched = SettingPattern(setting.Key).Replace(patched, match.Groups[1].Value + setting.Value + match.Groups[3].Value, 1);
		}
		if (!string.Equals(patched, text, StringComparison.Ordinal))
		{
			WriteRobloxSettings(path, patched);
			App.Logger?.WriteLine(LogIdent, "Patched the Roblox interface settings to hide the topbar");
		}
	}

	private static void RestoreRobloxSettings(ClassicTopBarState state)
	{
		string path = RobloxSettingsPath;
		if (!File.Exists(path))
		{
			state.RobloxSettings.Clear();
			return;
		}
		string text = File.ReadAllText(path);
		string patched = text;
		foreach (KeyValuePair<string, string> setting in NoGuiSettings)
		{
			Match match = SettingPattern(setting.Key).Match(patched);
			if (!match.Success)
			{
				continue;
			}
			string current = match.Groups[2].Value;
			string? target = null;
			if (state.RobloxSettings.TryGetValue(setting.Key, out string? saved))
			{
				target = saved;
			}
			else if (string.Equals(current, setting.Value, StringComparison.OrdinalIgnoreCase)
				&& RobloxDefaults.TryGetValue(setting.Key, out string? fallback))
			{
				target = fallback;
				App.Logger?.WriteLine(LogIdent, "No saved value for " + setting.Key + ", putting it back to the Roblox default");
			}
			if (target == null || string.Equals(current, target, StringComparison.Ordinal))
			{
				continue;
			}
			patched = SettingPattern(setting.Key).Replace(patched, match.Groups[1].Value + target + match.Groups[3].Value, 1);
		}
		if (!string.Equals(patched, text, StringComparison.Ordinal))
		{
			WriteRobloxSettings(path, patched);
			App.Logger?.WriteLine(LogIdent, "Restored the Roblox interface settings");
		}
		state.RobloxSettings.Clear();
	}

	public static async Task RestoreRobloxInterfaceAsync(CancellationToken token)
	{
		await Gate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			ClassicTopBarState state = LoadState();
			TryRestoreRobloxSettings(state);
			SaveState(state);
		}
		finally
		{
			Gate.Release();
		}
	}

	private static void WriteRobloxSettings(string path, string content)
	{
		string temporary = path + ".voidstrap.tmp";
		FileAttributes? original = null;
		try
		{
			if (File.Exists(path))
			{
				FileAttributes attributes = File.GetAttributes(path);
				if ((attributes & FileAttributes.ReadOnly) != 0)
				{
					original = attributes;
					File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
				}
			}
			if (File.Exists(temporary))
			{
				File.SetAttributes(temporary, FileAttributes.Normal);
			}
			File.WriteAllText(temporary, content);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (original is FileAttributes restore)
			{
				try
				{
					File.SetAttributes(path, restore);
				}
				catch (Exception ex)
				{
					App.Logger?.WriteLine(LogIdent, "The Roblox settings file could not be locked again: " + ex.Message);
				}
			}
			try
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The temporary settings file could not be removed: " + ex.Message);
			}
		}
	}

	private static void TryPatchRobloxSettings(ClassicTopBarState state)
	{
		try
		{
			PatchRobloxSettings(state);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The Roblox interface settings could not be patched, the topbar mod is still applied: " + ex.Message);
		}
	}

	private static void TryRestoreRobloxSettings(ClassicTopBarState state)
	{
		try
		{
			RestoreRobloxSettings(state);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The Roblox interface settings could not be restored: " + ex.Message);
		}
	}

	private static Regex SettingPattern(string name)
	{
		return new Regex("(<(?:bool|float|int|token) name=\"" + Regex.Escape(name) + "\">)([^<]*)(</(?:bool|float|int|token)>)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
	}

	private static void CopyTree(string source, string destination, CancellationToken token)
	{
		Directory.CreateDirectory(destination);
		foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
		{
			token.ThrowIfCancellationRequested();
			Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
		}
		foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			token.ThrowIfCancellationRequested();
			string target = Path.Combine(destination, Path.GetRelativePath(source, file));
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Copy(file, Writable(target), overwrite: true);
			token.ThrowIfCancellationRequested();
		}
	}

	public static HashSet<string> FeatureModIds()
	{
		ClassicTopBarState state = LoadState();
		HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
		if (state.NoGuiModId.Length > 0)
		{
			ids.Add(state.NoGuiModId);
		}
		if (state.Ps4ButtonsModId.Length > 0)
		{
			ids.Add(state.Ps4ButtonsModId);
		}
		return ids;
	}

	private static ClassicTopBarState LoadState()
	{
		try
		{
			if (File.Exists(StatePath))
			{
				return JsonSerializer.Deserialize<ClassicTopBarState>(File.ReadAllText(StatePath)) ?? new ClassicTopBarState();
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The saved state could not be read: " + ex.Message);
		}
		return new ClassicTopBarState();
	}

	private static void SaveState(ClassicTopBarState state)
	{
		try
		{
			Directory.CreateDirectory(Root);
			File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions.Indented));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The state could not be saved: " + ex.Message);
		}
	}

	private static void DiscardDirectory(string path)
	{
		try
		{
			ManagedModStore.Discard(path);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The folder " + path + " could not be removed: " + ex.Message);
		}
	}

	private static string Writable(string path)
	{
		if (File.Exists(path))
		{
			ManagedModStore.ClearReadOnlyFile(path);
		}
		return path;
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				ManagedModStore.ClearReadOnly(path);
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The folder " + path + " could not be removed: " + ex.Message);
		}
	}
}
