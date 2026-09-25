using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.Processing;
using Voidstrap.AppData;
using Voidstrap.Integrations;

namespace Voidstrap.Utility;

public enum ModGeneratorFill
{
	Solid,
	Gradient,
	Image
}

public sealed class ModGeneratorSettings
{
	public ModGeneratorFill Fill { get; set; } = ModGeneratorFill.Solid;

	public string SolidColor { get; set; } = "#FF3B5C";

	public List<string> GradientColors { get; set; } = ["#FF3B5C", "#7B5CFF"];

	public double GradientAngle { get; set; } = 90.0;

	public string? FillImage { get; set; }

	public int IconScale { get; set; }

	public bool KeepColoredIcons { get; set; } = true;

	public bool KeepShading { get; set; } = true;

	public bool RecolorShapes { get; set; }

	public bool RecolorIconFont { get; set; } = true;

	public bool IncludeUiTextures { get; set; }

	public string? LogoImage { get; set; }

	public string ModName { get; set; } = "Generated UI theme";

	public string RobloxVersion { get; set; } = "";

	public DateTime GeneratedUtc { get; set; }
}

public sealed class ModGeneratorProgress
{
	public ModGeneratorProgress(double fraction, string message)
	{
		Fraction = fraction;
		Message = message;
	}

	public double Fraction { get; }

	public string Message { get; }
}

public sealed class ModGeneratorPreview
{
	public required BitmapSource Before { get; init; }

	public required BitmapSource After { get; init; }

	public int NamedIcons { get; init; }

	public string? IconFont { get; init; }

	public Color FontColor { get; init; }

	public Brush? FontBrush { get; init; }
}

public static class ModGenerator
{
	private const string LogIdent = "ModGenerator";

	private const string SettingsFileName = "generator.json";

	private const string FillImageName = "fill.png";

	private const int FontGradientBands = 24;

	private const string LogoImageName = "logo.png";

	private const double ColorfulFraction = 0.15;

	private const double StaleIndexRatio = 0.1;

	private const int FillImageLimit = 512;

	private const int PreviewCell = 64;

	private const int PreviewColumns = 6;

	private const int PreviewRows = 4;

	private static readonly int Workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

	private static readonly SemaphoreSlim GenerateGate = new SemaphoreSlim(1, 1);

	private static readonly string[] LogoTargets =
	[
		"ExtraContent/textures/ui/InGameMenu/roblox_logo.png",
		"content/textures/loading/robloxlogo.png",
		"ExtraContent/textures/ui/LuaApp/ExternalSite/roblox.png",
		"ExtraContent/textures/ui/LuaApp/ExternalSite/roblox@2x.png",
		"ExtraContent/textures/ui/LuaApp/ExternalSite/roblox@3x.png"
	];

	private static readonly string[] CuratedTextures =
	[
		"content/textures/ui/Settings/Help/EscapeIcon.png",
		"content/textures/ui/Settings/Help/LeaveIcon.png",
		"content/textures/ui/Settings/Help/ResetIcon.png",
		"content/textures/ui/Settings/Dropdown/DropDown.png",
		"content/textures/ui/Settings/Dropdown/DropDown@2x.png",
		"content/textures/ui/Settings/Slider/Left@2x.png",
		"content/textures/ui/Settings/Slider/Left.png",
		"content/textures/ui/Settings/Slider/Right@2x.png",
		"content/textures/ui/Settings/Slider/Right.png",
		"content/textures/ui/Settings/Slider/Less.png",
		"content/textures/ui/Settings/Slider/More.png",
		"content/textures/ui/InspectMenu/Button_outline.png",
		"content/textures/ui/InspectMenu/Button_outline@2x.png",
		"content/textures/ui/InspectMenu/Button_outline@3x.png",
		"content/textures/ui/InspectMenu/caret_tail_left.png",
		"content/textures/ui/InspectMenu/caret-tail-left@2x.png",
		"content/textures/ui/InspectMenu/caret_tail_left@3x.png",
		"content/textures/ui/InspectMenu/ico_favorite.png",
		"content/textures/ui/InspectMenu/ico_favorite@2x.png",
		"content/textures/ui/InspectMenu/ico_favorite@3x.png",
		"content/textures/ui/InspectMenu/ico_favorite_off.png",
		"content/textures/ui/InspectMenu/ico_favorite_off@2x.png",
		"content/textures/ui/InspectMenu/ico_favorite_off@3x.png",
		"content/textures/ui/InspectMenu/ico_inspect.png",
		"content/textures/ui/InspectMenu/ico_inspect@2x.png",
		"content/textures/ui/InspectMenu/ico_inspect@3x.png",
		"content/textures/ui/InspectMenu/x.png",
		"content/textures/ui/InspectMenu/x@2x.png",
		"content/textures/ui/InspectMenu/x@3x.png",
		"content/textures/ui/InspectMenu/ico_alert_tilt.png",
		"content/textures/ui/InspectMenu/ico_alert_tilt@2x.png",
		"content/textures/ui/InspectMenu/ico_alert_tilt@3x.png",
		"content/textures/loading/cancelButton.png",
		"content/textures/ui/mouseLock_on.png",
		"content/textures/ui/mouseLock_on@2x.png",
		"content/textures/ui/SelectionBox.png",
		"content/textures/ui/SelectionBox@2x.png",
		"content/textures/ui/traildot.png",
		"content/textures/ui/waypoint.png",
		"ExtraContent/textures/ui/LuaChat/icons/ic-chat-large.png",
		"ExtraContent/textures/ui/LuaChat/icons/ic-chat-large@2x.png",
		"ExtraContent/textures/ui/LuaChat/icons/ic-chat-large@3x.png",
		"ExtraContent/textures/ui/LuaChat/icons/ic-friends.png",
		"ExtraContent/textures/ui/LuaChat/icons/ic-friends@2x.png",
		"ExtraContent/textures/ui/LuaChat/icons/ic-friends@3x.png",
		"ExtraContent/textures/ui/LuaChatV2/common_search.png",
		"ExtraContent/textures/ui/LuaChatV2/common_search@2x.png",
		"ExtraContent/textures/ui/LuaChatV2/common_search@3x.png",
		"ExtraContent/textures/ui/LuaChatV2/actions_checkbox.png",
		"ExtraContent/textures/ui/LuaChatV2/ic-add-friends.png",
		"ExtraContent/textures/ui/LuaChatV2/ic-friend-empty-border.png"
	];

	private static readonly string[] TextureRoots =
	[
		"ExtraContent/textures/ui",
		"content/textures/ui"
	];

	private static readonly string[] StructuralPrefixes =
	[
		"chat_bubble/", "component_assets/", "squircles/", "gradient/", "LuaApp/buttons/", "LuaApp/dropdown/", "AE/Graphic/"
	];

	private static readonly string[] StructuralParts = ["/9-slice/", "/9Slice/"];

	private static readonly string[] StructuralWords =
	[
		"slider", "shadow", "hover", "glow", "mask", "toggle", "selector", "highlight", "background", "outline", "rounded", "capsule", "knob", "divider"
	];

	private static readonly string[] ArtPrefixes =
	[
		"icons/graphic/", "icons/brands/", "icons/logo/", "reactions/", "icons/controls/voice/"
	];

	private static readonly string[] ArtParts = ["goldrobux", "robuxcoin", "/social/"];

	private static readonly string[] PreviewGroups =
	[
		"icons/menu/", "icons/navigation/", "icons/common/", "icons/actions/", "icons/status/", "icons/controls/"
	];

	private static string Root => Path.Combine(Paths.Base, "ModGenerator");

	private static string OriginalsRoot => Path.Combine(Root, "Originals");

	private sealed class Painter
	{
		public ModGeneratorFill Fill;

		public Color Solid;

		public (double Offset, Color Color)[] Stops = [];

		public double Dx = 1.0;

		public double Dy;

		public byte[]? Image;

		public int ImageWidth;

		public int ImageHeight;

		public bool KeepColored;

		public bool KeepShading;

		public bool RecolorShapes;

		public byte[]? Logo;

		public int LogoWidth;

		public int LogoHeight;

		public Color FontColor;

		public Color[] FontBands = [];

		public Brush? FontBrush;
	}

	private struct Region
	{
		public int MinX;

		public int MinY;

		public int MaxX;

		public int MaxY;

		public int Opaque;

		public double Colored;

		public double Weight;

		public double LumaSum;

		public double LumaSquares;

		public double MaxLuma;

		public double LumaWeighted;
	}

	private sealed class Tally
	{
		public int Icons;

		public int Sheets;

		public int Textures;

		public int Fonts;

		public int Skipped;
	}

	public static bool TryParseColor(string? text, out Color color)
	{
		color = Colors.Transparent;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string value = text.Trim().TrimStart('#');
		if (value.Length == 3)
		{
			value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);
		}
		if (value.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
		{
			return false;
		}
		color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
		return true;
	}

	public static string? FindModId()
	{
		if (!Directory.Exists(Root))
		{
			return null;
		}
		HashSet<string> live = new HashSet<string>(ManagedModStore.Load().Select(record => record.Id), StringComparer.OrdinalIgnoreCase);
		foreach (string folder in Directory.EnumerateDirectories(Root))
		{
			string id = Path.GetFileName(folder);
			if (live.Contains(id) && File.Exists(Path.Combine(folder, SettingsFileName)))
			{
				return id;
			}
		}
		return null;
	}

	public static ModGeneratorSettings? LoadSettings(string modId)
	{
		string path = Path.Combine(Root, modId, SettingsFileName);
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			return JsonSerializer.Deserialize<ModGeneratorSettings>(File.ReadAllText(path));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The saved generator settings could not be read: " + ex.Message);
			return null;
		}
	}

	public static async Task<string> GenerateAsync(ModGeneratorSettings settings, IProgress<ModGeneratorProgress>? progress, CancellationToken token)
	{
		await GenerateGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			return await Task.Run(() => GenerateCore(settings, FindModId(), progress, token), token).ConfigureAwait(false);
		}
		finally
		{
			GenerateGate.Release();
		}
	}

	public static async Task RefreshOutdatedAsync(string versionDirectory, string versionGuid, CancellationToken token)
	{
		string? modId = FindModId();
		if (modId == null)
		{
			return;
		}
		ModGeneratorSettings? settings = LoadSettings(modId);
		if (settings == null || string.Equals(settings.RobloxVersion, versionGuid, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		App.Logger?.WriteLine(LogIdent, "Roblox updated, regenerating " + settings.ModName + " for the new sprite sheets");
		await GenerateGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			await Task.Run(() => GenerateCore(settings, modId, null, token, versionDirectory, versionGuid), token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The generated mod could not be refreshed: " + ex.Message);
		}
		finally
		{
			GenerateGate.Release();
		}
	}

	public static ModGeneratorPreview? RenderPreview(ModGeneratorSettings settings)
	{
		try
		{
			RobloxPlayerData data = new RobloxPlayerData();
			(string versionDirectory, string versionGuid) = ResolveInstall(data);
			if (!Directory.Exists(Path.Combine(versionDirectory, "ExtraContent")))
			{
				App.Logger?.WriteLine(LogIdent, "No installed Roblox client was found for the preview in " + versionDirectory);
				return null;
			}
			HashSet<string> manifest = CurrentManifest(data);
			Painter painter = BuildPainter(settings, null);
			string? fontSource = null;
			try
			{
				string? font = FindIconFonts(versionDirectory).FirstOrDefault();
				fontSource = font == null ? null : ResolveSource(versionDirectory, versionGuid, font, manifest);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The icon font preview is unavailable: " + ex.Message);
			}
			IReadOnlyDictionary<string, List<IconRect>> index = new Dictionary<string, List<IconRect>>();
			ModGeneratorPreview? grid = null;
			try
			{
				index = RobloxIconIndex.Load(versionDirectory, versionGuid);
				grid = RenderIconGrid(index, painter, versionDirectory, versionGuid, manifest);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The named icon preview is unavailable: " + ex.Message);
			}
			if (grid != null)
			{
				return new ModGeneratorPreview
				{
					Before = grid.Before,
					After = grid.After,
					NamedIcons = index.Values.Sum(list => list.Count),
					IconFont = fontSource,
					FontColor = painter.FontColor,
					FontBrush = painter.FontBrush
				};
			}
			string? sheet = FindSheets(versionDirectory, 0)
				.OrderBy(relative => relative.Contains("_2x_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
				.ThenBy(relative => relative, StringComparer.OrdinalIgnoreCase)
				.FirstOrDefault();
			if (sheet == null)
			{
				return null;
			}
			(byte[] pixels, int width, int height) = Load(ResolveSource(versionDirectory, versionGuid, sheet, manifest));
			int crop = Math.Min(320, Math.Min(width, height));
			byte[] before = Crop(pixels, width, 0, 0, crop, crop);
			byte[] after = (byte[])before.Clone();
			PaintComponents(after, crop, crop, painter, ScaleOf(sheet));
			return new ModGeneratorPreview
			{
				Before = Create(before, crop, crop),
				After = Create(after, crop, crop),
				IconFont = fontSource,
				FontColor = painter.FontColor,
				FontBrush = painter.FontBrush
			};
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The preview could not be rendered: " + ex.Message);
			return null;
		}
	}

	private static ModGeneratorPreview? RenderIconGrid(IReadOnlyDictionary<string, List<IconRect>> index, Painter painter, string versionDirectory, string versionGuid, HashSet<string> manifest)
	{
		List<(string Sheet, IconRect Icon)> picks = [];
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (string group in PreviewGroups)
		{
			foreach ((string sheet, List<IconRect> icons) in index.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (!sheet.Contains("img_set_2x_", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				foreach (IconRect icon in icons)
				{
					if (picks.Count >= PreviewColumns * PreviewRows)
					{
						break;
					}
					if (!icon.Name.StartsWith(group, StringComparison.Ordinal) || icon.Width < 24 || icon.Height < 24 || icon.Width > PreviewCell || icon.Height > PreviewCell)
					{
						continue;
					}
					string family = icon.Name.Split('_')[0];
					if (seen.Add(family))
					{
						picks.Add((sheet, icon));
					}
				}
			}
		}
		if (picks.Count < 6)
		{
			return null;
		}
		int width = PreviewColumns * PreviewCell;
		int height = (picks.Count + PreviewColumns - 1) / PreviewColumns * PreviewCell;
		byte[] canvas = new byte[width * height * 4];
		List<IconRect> placed = new List<IconRect>(picks.Count);
		Dictionary<string, (byte[] Pixels, int Width, int Height)> sheets = new Dictionary<string, (byte[], int, int)>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < picks.Count; i++)
		{
			(string sheet, IconRect icon) = picks[i];
			if (!sheets.TryGetValue(sheet, out (byte[] Pixels, int Width, int Height) source))
			{
				source = Load(ResolveSource(versionDirectory, versionGuid, sheet, manifest));
				sheets[sheet] = source;
			}
			if (icon.X + icon.Width > source.Width || icon.Y + icon.Height > source.Height)
			{
				continue;
			}
			int x = i % PreviewColumns * PreviewCell + (PreviewCell - icon.Width) / 2;
			int y = i / PreviewColumns * PreviewCell + (PreviewCell - icon.Height) / 2;
			for (int row = 0; row < icon.Height; row++)
			{
				Buffer.BlockCopy(source.Pixels, ((icon.Y + row) * source.Width + icon.X) * 4, canvas, ((y + row) * width + x) * 4, icon.Width * 4);
			}
			placed.Add(icon with { X = x, Y = y });
		}
		byte[] after = (byte[])canvas.Clone();
		PaintIndexed(after, width, height, placed, painter, 2, null);
		return new ModGeneratorPreview
		{
			Before = Create(canvas, width, height),
			After = Create(after, width, height)
		};
	}

	private static string GenerateCore(ModGeneratorSettings settings, string? existingId, IProgress<ModGeneratorProgress>? progress, CancellationToken token, string? versionDirectory = null, string? versionGuid = null)
	{
		RobloxPlayerData data = new RobloxPlayerData();
		if (versionDirectory == null || versionGuid == null)
		{
			(string folder, string guid) = ResolveInstall(data);
			versionDirectory ??= folder;
			versionGuid ??= guid;
		}
		if (!Directory.Exists(Path.Combine(versionDirectory, "ExtraContent")))
		{
			throw new InvalidOperationException(RobloxInstallCompression.IsCompressed(data)
				? "Roblox is compressed and could not be unpacked. Check that there is free disk space, then generate the mod again."
				: "Roblox is not installed yet. Launch Roblox once, then generate the mod.");
		}
		progress?.Report(new ModGeneratorProgress(0.02, "Reading the icon index from this Roblox build"));
		string modId = existingId ?? ManagedModStore.Create(string.IsNullOrWhiteSpace(settings.ModName) ? "Generated UI theme" : settings.ModName.Trim()).Id;
		string stateFolder = Path.Combine(Root, modId);
		Directory.CreateDirectory(stateFolder);
		StoreImage(settings.FillImage, Path.Combine(stateFolder, FillImageName));
		StoreImage(settings.LogoImage, Path.Combine(stateFolder, LogoImageName));
		settings.FillImage = File.Exists(Path.Combine(stateFolder, FillImageName)) ? Path.Combine(stateFolder, FillImageName) : null;
		settings.LogoImage = File.Exists(Path.Combine(stateFolder, LogoImageName)) ? Path.Combine(stateFolder, LogoImageName) : null;

		string output = ManagedModStore.GetFolder(modId);
		string staging = output + ".generating";
		if (Directory.Exists(staging))
		{
			Directory.Delete(staging, recursive: true);
		}
		Directory.CreateDirectory(staging);

		HashSet<string> manifest = CurrentManifest(data);
		Painter painter = BuildPainter(settings, stateFolder);
		IReadOnlyDictionary<string, List<IconRect>> index = RobloxIconIndex.Load(versionDirectory, versionGuid);
		List<string> sheets = [.. FindSheets(versionDirectory, settings.IconScale)];
		List<string> curated = [.. FindCuratedTextures(versionDirectory, settings.IconScale)];
		HashSet<string> curatedSet = new HashSet<string>(curated, StringComparer.OrdinalIgnoreCase);
		List<string> textures = [.. curated];
		if (settings.IncludeUiTextures)
		{
			textures.AddRange(FindUiTextures(versionDirectory, settings.IconScale).Where(texture => !curatedSet.Contains(texture)));
		}
		if (sheets.Count == 0)
		{
			throw new InvalidOperationException("No Roblox UI sprite sheets were found in the installed client.");
		}

		Tally tally = new Tally();
		List<(string Relative, bool Sheet)> targets = [.. sheets.Select(sheet => (sheet, true)), .. textures.Select(texture => (texture, false))];
		int done = 0;
		Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, target =>
		{
			try
			{
				string source = ResolveSource(versionDirectory, versionGuid, target.Relative, manifest);
				(byte[] pixels, int width, int height) = Load(source);
				int scale = ScaleOf(target.Relative);
				int painted;
				if (target.Sheet && index.TryGetValue(target.Relative, out List<IconRect>? icons) && IsIndexUsable(pixels, width, height, icons))
				{
					painted = PaintIndexed(pixels, width, height, icons, painter, scale, target.Relative);
				}
				else
				{
					painted = PaintComponents(pixels, width, height, painter, scale, curatedSet.Contains(target.Relative));
				}
				if (painted > 0)
				{
					string destination = Path.Combine(staging, target.Relative.Replace('/', Path.DirectorySeparatorChar));
					Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
					Save(destination, pixels, width, height);
					if (target.Sheet)
					{
						Interlocked.Add(ref tally.Icons, painted);
						Interlocked.Increment(ref tally.Sheets);
					}
					else
					{
						Interlocked.Increment(ref tally.Textures);
					}
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Interlocked.Increment(ref tally.Skipped);
				App.Logger?.WriteLine(LogIdent, "Skipped " + target.Relative + ": " + ex.Message);
			}
			int count = Interlocked.Increment(ref done);
			progress?.Report(new ModGeneratorProgress(0.05 + 0.8 * count / targets.Count, "Recolouring " + count + " of " + targets.Count + " images"));
		});
		token.ThrowIfCancellationRequested();

		if (settings.RecolorIconFont)
		{
			progress?.Report(new ModGeneratorProgress(0.88, "Recolouring the Roblox icon font"));
			foreach (string font in FindIconFonts(versionDirectory))
			{
				try
				{
					byte[] original = File.ReadAllBytes(ResolveSource(versionDirectory, versionGuid, font, manifest));
					string destination = Path.Combine(staging, font.Replace('/', Path.DirectorySeparatorChar));
					Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
					File.WriteAllBytes(destination, ColorFont.Colorize(original, painter.FontBands, painter.Dx, painter.Dy));
					tally.Fonts++;
				}
				catch (Exception ex)
				{
					tally.Skipped++;
					App.Logger?.WriteLine(LogIdent, "The icon font " + font + " could not be recoloured: " + ex.Message);
				}
			}
		}

		if (painter.Logo != null)
		{
			progress?.Report(new ModGeneratorProgress(0.92, "Adding the custom logo"));
			WriteLogos(painter, versionDirectory, versionGuid, manifest, staging);
		}

		progress?.Report(new ModGeneratorProgress(0.96, "Installing the mod"));
		if (Directory.Exists(output))
		{
			Directory.Delete(output, recursive: true);
		}
		Directory.Move(staging, output);

		settings.RobloxVersion = versionGuid;
		settings.GeneratedUtc = DateTime.UtcNow;
		JsonFile.SerializeAtomic(Path.Combine(stateFolder, SettingsFileName), settings, JsonOptions.Indented);
		PruneOriginals(versionGuid);
		string summary = "Recoloured " + tally.Icons.ToString("N0", CultureInfo.CurrentCulture) + " icons in " + tally.Sheets + " sheets"
			+ (tally.Fonts > 0 ? ", " + tally.Fonts + (tally.Fonts == 1 ? " icon font" : " icon fonts") : "")
			+ (tally.Textures > 0 ? ", " + tally.Textures.ToString("N0", CultureInfo.CurrentCulture) + " textures" : "");
		App.Logger?.WriteLine(LogIdent, "Generated " + summary + " for Roblox " + versionGuid + (tally.Skipped > 0 ? ", skipped " + tally.Skipped : ""));
		return summary + (tally.Skipped > 0 ? ", " + tally.Skipped + " skipped" : "");
	}

	private static IEnumerable<string> FindSheets(string versionDirectory, int scale)
	{
		string root = Path.Combine(versionDirectory, "ExtraContent");
		if (!Directory.Exists(root))
		{
			yield break;
		}
		foreach (string file in Directory.EnumerateFiles(root, "img_set_*.png", SearchOption.AllDirectories))
		{
			string name = Path.GetFileName(file);
			if (scale != 0 && !name.StartsWith("img_set_" + scale + "x", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			yield return Path.GetRelativePath(versionDirectory, file).Replace('\\', '/');
		}
	}

	private static IEnumerable<string> FindCuratedTextures(string versionDirectory, int scale)
	{
		foreach (string relative in CuratedTextures)
		{
			string name = Path.GetFileNameWithoutExtension(relative);
			bool sized = name.Length > 3 && name[^3] == '@' && char.IsDigit(name[^2]) && name[^1] == 'x';
			if (scale != 0 && sized && ScaleOf(relative) != scale)
			{
				continue;
			}
			if (File.Exists(Path.Combine(versionDirectory, relative.Replace('/', Path.DirectorySeparatorChar))))
			{
				yield return relative;
			}
		}
	}

	private static IEnumerable<string> FindUiTextures(string versionDirectory, int scale)
	{
		HashSet<string> logos = new HashSet<string>(LogoTargets, StringComparer.OrdinalIgnoreCase);
		foreach (string rootName in TextureRoots)
		{
			string root = Path.Combine(versionDirectory, rootName.Replace('/', Path.DirectorySeparatorChar));
			if (!Directory.Exists(root))
			{
				continue;
			}
			foreach (string file in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories))
			{
				string name = Path.GetFileNameWithoutExtension(file);
				string relative = Path.GetRelativePath(versionDirectory, file).Replace('\\', '/');
				if (name.StartsWith("img_set_", StringComparison.OrdinalIgnoreCase) || relative.Contains("/ImageSet/", StringComparison.OrdinalIgnoreCase) || logos.Contains(relative))
				{
					continue;
				}
				if (scale != 0 && ScaleOf(relative) != scale)
				{
					continue;
				}
				yield return relative;
			}
		}
	}

	private static IEnumerable<string> FindIconFonts(string versionDirectory)
	{
		string root = Path.Combine(versionDirectory, "ExtraContent", "LuaPackages");
		if (!Directory.Exists(root))
		{
			return [];
		}
		return Directory.EnumerateFiles(root, "BuilderIcons*.*", SearchOption.AllDirectories)
			.Where(file => file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
			.Select(file => Path.GetRelativePath(versionDirectory, file).Replace('\\', '/'))
			.OrderBy(file => file.Contains("Regular", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
			.ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static HashSet<string> CurrentManifest(RobloxPlayerData data)
	{
		HashSet<string> manifest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string entry in data.State.ModManifest)
		{
			manifest.Add(entry.Replace('\\', '/'));
		}
		return manifest;
	}

	private static (string Folder, string Guid) ResolveInstall(RobloxPlayerData data)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			(string ClientDirectory, string VersionGuid)? sober = Task.Run(() => Bootstrapper.PrepareSoberClientTreeAsync(CancellationToken.None)).GetAwaiter().GetResult();
			if (sober is { } tree)
				return (tree.ClientDirectory, tree.VersionGuid);
		}
		RobloxInstallCompression.EnsureExtracted(data);
		string folder = data.Directory;
		string guid = data.State.VersionGuid;
		if (Directory.Exists(Path.Combine(folder, "ExtraContent")))
		{
			return (folder, guid);
		}
		try
		{
			string root = data.VersionsRoot;
			string? newest = Directory.Exists(root)
				? Directory.EnumerateDirectories(root)
					.Where(candidate => Directory.Exists(Path.Combine(candidate, "ExtraContent")))
					.OrderByDescending(Directory.GetLastWriteTimeUtc)
					.FirstOrDefault()
				: null;
			if (newest != null)
			{
				return (newest, Path.GetFileName(newest));
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The installed Roblox versions could not be listed: " + ex.Message);
		}
		return (folder, guid);
	}

	private static string ResolveSource(string versionDirectory, string versionGuid, string relative, HashSet<string> manifest)
	{
		string safeGuid = string.IsNullOrWhiteSpace(versionGuid) ? "unknown" : versionGuid;
		string cached = Path.Combine(OriginalsRoot, safeGuid, relative.Replace('/', Path.DirectorySeparatorChar));
		if (File.Exists(cached))
		{
			return cached;
		}
		string installed = Path.Combine(versionDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(installed))
		{
			throw new FileNotFoundException("The original file is missing", relative);
		}
		if (manifest.Contains(relative))
		{
			return installed;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
		File.Copy(installed, cached, overwrite: true);
		return cached;
	}

	private static void PruneOriginals(string versionGuid)
	{
		try
		{
			if (!Directory.Exists(OriginalsRoot))
			{
				return;
			}
			foreach (string folder in Directory.EnumerateDirectories(OriginalsRoot))
			{
				if (!string.Equals(Path.GetFileName(folder), versionGuid, StringComparison.OrdinalIgnoreCase))
				{
					Directory.Delete(folder, recursive: true);
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Old original sprite sheets could not be removed: " + ex.Message);
		}
	}

	private static void StoreImage(string? source, string destination)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			if (File.Exists(destination))
			{
				File.Delete(destination);
			}
			return;
		}
		if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (!File.Exists(source))
		{
			throw new FileNotFoundException("The selected image no longer exists", source);
		}
		(byte[] pixels, int width, int height) = Load(source);
		Save(destination, pixels, width, height);
	}

	private static void WriteLogos(Painter painter, string versionDirectory, string versionGuid, HashSet<string> manifest, string staging)
	{
		foreach (string relative in LogoTargets)
		{
			try
			{
				string source = ResolveSource(versionDirectory, versionGuid, relative, manifest);
				(_, int width, int height) = Load(source);
				byte[] canvas = new byte[width * height * 4];
				DrawLogo(canvas, width, new IconRect("logo", 0, 0, width, height), painter);
				string target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				Save(target, canvas, width, height);
			}
			catch (FileNotFoundException)
			{
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The logo could not be written to " + relative + ": " + ex.Message);
			}
		}
	}

	private static Painter BuildPainter(ModGeneratorSettings settings, string? stateFolder)
	{
		Painter painter = new Painter
		{
			Fill = settings.Fill,
			KeepColored = settings.KeepColoredIcons,
			KeepShading = settings.KeepShading,
			RecolorShapes = settings.RecolorShapes
		};
		if (!TryParseColor(settings.SolidColor, out painter.Solid))
		{
			painter.Solid = Colors.White;
		}
		List<Color> colors = [];
		foreach (string text in settings.GradientColors ?? [])
		{
			if (TryParseColor(text, out Color color))
			{
				colors.Add(color);
			}
		}
		if (colors.Count == 0)
		{
			colors.Add(painter.Solid);
		}
		if (colors.Count == 1)
		{
			colors.Add(colors[0]);
		}
		painter.Stops = [.. colors.Select((color, index) => ((double)index / (colors.Count - 1), color))];
		double radians = settings.GradientAngle * Math.PI / 180.0;
		painter.Dx = Math.Cos(radians);
		painter.Dy = Math.Sin(radians);
		painter.FontColor = settings.Fill == ModGeneratorFill.Gradient ? colors[0] : painter.Solid;
		painter.FontBands = [painter.FontColor];
		if (settings.Fill == ModGeneratorFill.Gradient && colors.Distinct().Count() > 1)
		{
			painter.FontBands = [.. Enumerable.Range(0, FontGradientBands).Select(band => Sample(painter.Stops, (band + 0.5) / FontGradientBands))];
			double lowest = Math.Min(0, painter.Dx) + Math.Min(0, painter.Dy);
			double range = Math.Max(0, painter.Dx) + Math.Max(0, painter.Dy) - lowest;
			System.Windows.Point start = new System.Windows.Point(lowest * painter.Dx, lowest * painter.Dy);
			LinearGradientBrush brush = new LinearGradientBrush { GradientStops = new GradientStopCollection(painter.Stops.Select(stop => new GradientStop(stop.Color, stop.Offset))), StartPoint = start, EndPoint = new System.Windows.Point(start.X + range * painter.Dx, start.Y + range * painter.Dy) };
			brush.Freeze();
			painter.FontBrush = brush;
		}
		if (settings.Fill == ModGeneratorFill.Image)
		{
			string? path = settings.FillImage;
			if (stateFolder != null && File.Exists(Path.Combine(stateFolder, FillImageName)))
			{
				path = Path.Combine(stateFolder, FillImageName);
			}
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			{
				throw new InvalidOperationException("Pick an image to fill the icons with first.");
			}
			(byte[] image, int width, int height) = Load(path);
			(painter.Image, painter.ImageWidth, painter.ImageHeight) = Shrink(image, width, height, FillImageLimit);
			painter.FontColor = AverageColor(painter.Image, painter.ImageWidth, painter.ImageHeight);
			painter.FontBands = [painter.FontColor];
		}
		string? logoPath = settings.LogoImage;
		if (stateFolder != null && File.Exists(Path.Combine(stateFolder, LogoImageName)))
		{
			logoPath = Path.Combine(stateFolder, LogoImageName);
		}
		if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
		{
			(painter.Logo, painter.LogoWidth, painter.LogoHeight) = Load(logoPath);
		}
		return painter;
	}

	private static int ScaleOf(string relative)
	{
		string name = Path.GetFileNameWithoutExtension(relative);
		int marker = name.IndexOf("img_set_", StringComparison.OrdinalIgnoreCase);
		if (marker >= 0 && marker + 8 < name.Length && char.IsDigit(name[marker + 8]))
		{
			return name[marker + 8] - '0';
		}
		int at = name.LastIndexOf('@');
		if (at >= 0 && at + 1 < name.Length && char.IsDigit(name[at + 1]))
		{
			return name[at + 1] - '0';
		}
		return 1;
	}

	private static bool IsIndexUsable(byte[] pixels, int width, int height, List<IconRect> icons)
	{
		if (icons.Count == 0)
		{
			return false;
		}
		int broken = 0;
		foreach (IconRect icon in icons)
		{
			if (icon.X < 0 || icon.Y < 0 || icon.Width <= 0 || icon.Height <= 0 || icon.X + icon.Width > width || icon.Y + icon.Height > height || !HasPixels(pixels, width, icon))
			{
				broken++;
			}
		}
		return broken <= icons.Count * StaleIndexRatio;
	}

	private static bool HasPixels(byte[] pixels, int width, IconRect icon)
	{
		for (int y = icon.Y; y < icon.Y + icon.Height; y++)
		{
			int offset = (y * width + icon.X) * 4 + 3;
			for (int x = 0; x < icon.Width; x++, offset += 4)
			{
				if (pixels[offset] != 0)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool IsLogoIcon(string name)
	{
		return name.StartsWith("icons/logo/letterform", StringComparison.Ordinal)
			|| name.EndsWith("/ic_logo", StringComparison.Ordinal)
			|| name.Contains("logo/block", StringComparison.Ordinal);
	}

	private static bool IsStructural(string name)
	{
		string leaf = name[(name.LastIndexOf('/') + 1)..];
		return StructuralPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
			|| StructuralParts.Any(part => name.Contains(part, StringComparison.Ordinal))
			|| StructuralWords.Any(word => leaf.Contains(word, StringComparison.OrdinalIgnoreCase))
			|| leaf.Equals("circle", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsIconName(string name)
	{
		return name.StartsWith("icons/", StringComparison.Ordinal)
			|| name.Contains("/icons/", StringComparison.OrdinalIgnoreCase)
			|| name.StartsWith("truncate_arrows/", StringComparison.Ordinal)
			|| name.StartsWith("LuaApp/category/", StringComparison.Ordinal);
	}

	private static bool IsArt(string name)
	{
		return ArtPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
			|| ArtParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
	}

	private static int PaintIndexed(byte[] pixels, int width, int height, List<IconRect> icons, Painter painter, int scale, string? sheet)
	{
		int painted = 0;
		foreach (IconRect icon in icons)
		{
			if (icon.X < 0 || icon.Y < 0 || icon.X + icon.Width > width || icon.Y + icon.Height > height)
			{
				continue;
			}
			if (painter.Logo != null && IsLogoIcon(icon.Name))
			{
				DrawLogo(pixels, width, icon, painter);
				painted++;
				continue;
			}
			bool glyph = IsIconName(icon.Name);
			if ((!painter.RecolorShapes && !glyph && IsStructural(icon.Name)) || (painter.KeepColored && IsArt(icon.Name)))
			{
				continue;
			}
			Region region = Measure(pixels, width, icon);
			if (region.Opaque == 0 && region.Weight <= 0.0)
			{
				continue;
			}
			if (ShouldKeep(pixels, width, painter, scale, region, !glyph))
			{
				continue;
			}
			PaintRegion(pixels, width, null, 0, icon.X, icon.Y, icon.X + icon.Width - 1, icon.Y + icon.Height - 1, region.MaxLuma, painter);
			painted++;
		}
		return painted;
	}

	private static Region Measure(byte[] pixels, int width, IconRect icon)
	{
		Region region = new Region { MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue };
		for (int y = icon.Y; y < icon.Y + icon.Height; y++)
		{
			for (int x = icon.X; x < icon.X + icon.Width; x++)
			{
				Accumulate(pixels, (y * width + x) * 4, x, y, ref region);
			}
		}
		return region;
	}

	private static void Accumulate(byte[] pixels, int offset, int x, int y, ref Region region)
	{
		byte alpha = pixels[offset + 3];
		if (alpha == 0)
		{
			return;
		}
		if (x < region.MinX) region.MinX = x;
		if (x > region.MaxX) region.MaxX = x;
		if (y < region.MinY) region.MinY = y;
		if (y > region.MaxY) region.MaxY = y;
		double weight = alpha / 255.0;
		if (Math.Max(pixels[offset + 2], Math.Max(pixels[offset + 1], pixels[offset])) >= 64 && Saturation(pixels[offset + 2], pixels[offset + 1], pixels[offset]) >= 0.4)
		{
			region.Colored += weight;
		}
		region.Weight += weight;
		double luma = Luma(pixels, offset);
		region.LumaWeighted += luma * weight;
		if (luma > region.MaxLuma)
		{
			region.MaxLuma = luma;
		}
		if (alpha >= 200)
		{
			region.LumaSum += luma;
			region.LumaSquares += luma * luma;
			region.Opaque++;
		}
	}

	private static int PaintComponents(byte[] pixels, int width, int height, Painter painter, int scale = 1, bool force = false)
	{
		scale = Math.Max(1, scale);
		int[] labels = new int[width * height];
		int[] stack = new int[width * height];
		int nextLabel = 0;
		int painted = 0;
		for (int start = 0; start < labels.Length; start++)
		{
			if (labels[start] != 0 || pixels[start * 4 + 3] == 0)
			{
				continue;
			}
			int label = ++nextLabel;
			int top = 0;
			stack[top++] = start;
			labels[start] = label;
			Region region = new Region { MinX = width, MinY = height, MaxX = 0, MaxY = 0 };
			while (top > 0)
			{
				int index = stack[--top];
				int x = index % width;
				int y = index / width;
				Accumulate(pixels, index * 4, x, y, ref region);
				for (int dy = -1; dy <= 1; dy++)
				{
					int ny = y + dy;
					if (ny < 0 || ny >= height)
					{
						continue;
					}
					for (int dx = -1; dx <= 1; dx++)
					{
						int nx = x + dx;
						if ((dx == 0 && dy == 0) || nx < 0 || nx >= width)
						{
							continue;
						}
						int neighbour = ny * width + nx;
						if (labels[neighbour] == 0 && pixels[neighbour * 4 + 3] != 0)
						{
							labels[neighbour] = label;
							stack[top++] = neighbour;
						}
					}
				}
			}
			if (!force && ShouldKeep(pixels, width, painter, scale, region, true))
			{
				continue;
			}
			PaintRegion(pixels, width, labels, label, region.MinX, region.MinY, region.MaxX, region.MaxY, region.MaxLuma, painter);
			painted++;
		}
		return painted;
	}

	private static bool ShouldKeep(byte[] pixels, int width, Painter painter, int scale, Region region, bool shapes)
	{
		int boxWidth = region.MaxX - region.MinX + 1;
		int boxHeight = region.MaxY - region.MinY + 1;
		if (boxWidth <= 0 || boxHeight <= 0)
		{
			return true;
		}
		if (shapes && !painter.RecolorShapes)
		{
			if (region.Opaque == 0 && region.MaxLuma < 48.0)
			{
				return true;
			}
			int shortSide = Math.Min(boxWidth, boxHeight);
			double fill = region.Opaque / (double)(boxWidth * boxHeight);
			bool corners = pixels[(region.MinY * width + region.MinX) * 4 + 3] >= 200
				&& pixels[(region.MinY * width + region.MaxX) * 4 + 3] >= 200
				&& pixels[(region.MaxY * width + region.MinX) * 4 + 3] >= 200
				&& pixels[(region.MaxY * width + region.MaxX) * 4 + 3] >= 200;
			if ((corners && shortSide >= 12 * scale) || (fill >= 0.7 && shortSide >= 40 * scale) || (fill >= 0.72 && shortSide >= 10 * scale))
			{
				return true;
			}
		}
		if (!painter.KeepColored)
		{
			return false;
		}
		if (region.Weight > 0.0 && region.Colored / region.Weight >= ColorfulFraction)
		{
			return true;
		}
		if (!shapes)
		{
			return false;
		}
		if (region.Opaque == 0)
		{
			return region.Weight > 0.0 && region.LumaWeighted / region.Weight < 200.0;
		}
		double mean = region.LumaSum / region.Opaque;
		double spread = Math.Sqrt(Math.Max(0.0, region.LumaSquares / region.Opaque - mean * mean));
		return spread > 18.0 || mean < 200.0;
	}

	private static void PaintRegion(byte[] pixels, int width, int[]? labels, int label, int minX, int minY, int maxX, int maxY, double maxLuma, Painter painter)
	{
		int boxWidth = maxX - minX + 1;
		int boxHeight = maxY - minY + 1;
		double spanX = Math.Max(1, boxWidth - 1);
		double spanY = Math.Max(1, boxHeight - 1);
		double lowest = Math.Min(0, painter.Dx) + Math.Min(0, painter.Dy);
		double highest = Math.Max(0, painter.Dx) + Math.Max(0, painter.Dy);
		double range = highest - lowest;
		bool shade = painter.KeepShading && maxLuma >= 40.0;
		for (int y = minY; y <= maxY; y++)
		{
			for (int x = minX; x <= maxX; x++)
			{
				int index = y * width + x;
				int offset = index * 4;
				if (labels != null ? labels[index] != label : pixels[offset + 3] == 0)
				{
					continue;
				}
				double factor = shade ? Math.Clamp(Luma(pixels, offset) / maxLuma, 0.0, 1.0) : 1.0;
				double u = (x - minX) / spanX;
				double v = (y - minY) / spanY;
				Color color = painter.Fill switch
				{
					ModGeneratorFill.Gradient => Sample(painter.Stops, range <= 0 ? 0 : (u * painter.Dx + v * painter.Dy - lowest) / range),
					ModGeneratorFill.Image => SampleImage(painter, u, v, boxWidth, boxHeight),
					_ => painter.Solid
				};
				pixels[offset] = (byte)Math.Round(color.B * factor);
				pixels[offset + 1] = (byte)Math.Round(color.G * factor);
				pixels[offset + 2] = (byte)Math.Round(color.R * factor);
			}
		}
	}

	private static void DrawLogo(byte[] pixels, int width, IconRect icon, Painter painter)
	{
		for (int y = icon.Y; y < icon.Y + icon.Height; y++)
		{
			Array.Clear(pixels, (y * width + icon.X) * 4, icon.Width * 4);
		}
		double scale = Math.Min((double)icon.Width / painter.LogoWidth, (double)icon.Height / painter.LogoHeight);
		int drawWidth = Math.Clamp((int)Math.Round(painter.LogoWidth * scale), 1, icon.Width);
		int drawHeight = Math.Clamp((int)Math.Round(painter.LogoHeight * scale), 1, icon.Height);
		byte[] fitted = Resize(painter.Logo!, painter.LogoWidth, painter.LogoHeight, drawWidth, drawHeight);
		int left = icon.X + (icon.Width - drawWidth) / 2;
		int top = icon.Y + (icon.Height - drawHeight) / 2;
		for (int row = 0; row < drawHeight; row++)
		{
			Buffer.BlockCopy(fitted, row * drawWidth * 4, pixels, ((top + row) * width + left) * 4, drawWidth * 4);
		}
	}

	private static Color Sample((double Offset, Color Color)[] stops, double t)
	{
		t = Math.Clamp(t, 0.0, 1.0);
		for (int i = 0; i < stops.Length - 1; i++)
		{
			(double startOffset, Color start) = stops[i];
			(double endOffset, Color end) = stops[i + 1];
			if (t <= endOffset || i == stops.Length - 2)
			{
				double local = endOffset <= startOffset ? 0.0 : Math.Clamp((t - startOffset) / (endOffset - startOffset), 0.0, 1.0);
				return Color.FromRgb(
					(byte)Math.Round(start.R + (end.R - start.R) * local),
					(byte)Math.Round(start.G + (end.G - start.G) * local),
					(byte)Math.Round(start.B + (end.B - start.B) * local));
			}
		}
		return stops[^1].Color;
	}

	private static Color SampleImage(Painter painter, double u, double v, int boxWidth, int boxHeight)
	{
		byte[] image = painter.Image!;
		double boxRatio = (double)boxWidth / Math.Max(1, boxHeight);
		double imageRatio = (double)painter.ImageWidth / Math.Max(1, painter.ImageHeight);
		double scaleX = 1.0, scaleY = 1.0;
		if (imageRatio > boxRatio)
		{
			scaleX = boxRatio / imageRatio;
		}
		else
		{
			scaleY = imageRatio / boxRatio;
		}
		double sx = Math.Clamp((0.5 - scaleX / 2.0 + u * scaleX) * (painter.ImageWidth - 1), 0, painter.ImageWidth - 1);
		double sy = Math.Clamp((0.5 - scaleY / 2.0 + v * scaleY) * (painter.ImageHeight - 1), 0, painter.ImageHeight - 1);
		int x0 = (int)sx;
		int y0 = (int)sy;
		int x1 = Math.Min(x0 + 1, painter.ImageWidth - 1);
		int y1 = Math.Min(y0 + 1, painter.ImageHeight - 1);
		double fx = sx - x0;
		double fy = sy - y0;
		double Channel(int channel)
		{
			double top = image[(y0 * painter.ImageWidth + x0) * 4 + channel] * (1 - fx) + image[(y0 * painter.ImageWidth + x1) * 4 + channel] * fx;
			double bottom = image[(y1 * painter.ImageWidth + x0) * 4 + channel] * (1 - fx) + image[(y1 * painter.ImageWidth + x1) * 4 + channel] * fx;
			return top * (1 - fy) + bottom * fy;
		}
		return Color.FromRgb((byte)Math.Round(Channel(2)), (byte)Math.Round(Channel(1)), (byte)Math.Round(Channel(0)));
	}

	private static Color AverageColor(byte[] pixels, int width, int height)
	{
		double red = 0, green = 0, blue = 0, weight = 0;
		for (int offset = 0; offset < width * height * 4; offset += 4)
		{
			double alpha = pixels[offset + 3] / 255.0;
			blue += pixels[offset] * alpha;
			green += pixels[offset + 1] * alpha;
			red += pixels[offset + 2] * alpha;
			weight += alpha;
		}
		return weight <= 0 ? Colors.White : Color.FromRgb((byte)Math.Round(red / weight), (byte)Math.Round(green / weight), (byte)Math.Round(blue / weight));
	}

	private static double Luma(byte[] pixels, int offset)
	{
		return 0.299 * pixels[offset + 2] + 0.587 * pixels[offset + 1] + 0.114 * pixels[offset];
	}

	private static double Saturation(byte red, byte green, byte blue)
	{
		int max = Math.Max(red, Math.Max(green, blue));
		int min = Math.Min(red, Math.Min(green, blue));
		return max == 0 ? 0.0 : (max - min) / (double)max;
	}

	private static (byte[] Pixels, int Width, int Height) Shrink(byte[] pixels, int width, int height, int limit)
	{
		int longest = Math.Max(width, height);
		if (longest <= limit)
		{
			return (pixels, width, height);
		}
		double scale = (double)limit / longest;
		int newWidth = Math.Max(1, (int)Math.Round(width * scale));
		int newHeight = Math.Max(1, (int)Math.Round(height * scale));
		return (Resize(pixels, width, height, newWidth, newHeight), newWidth, newHeight);
	}

	private static byte[] Resize(byte[] pixels, int width, int height, int newWidth, int newHeight)
	{
		if (width == newWidth && height == newHeight)
		{
			return (byte[])pixels.Clone();
		}
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(pixels, width, height);
		image.Mutate(context => context.Resize(newWidth, newHeight, KnownResamplers.Lanczos3));
		byte[] result = new byte[newWidth * newHeight * 4];
		image.CopyPixelDataTo(result);
		return result;
	}

	private static byte[] Crop(byte[] pixels, int width, int left, int top, int cropWidth, int cropHeight)
	{
		byte[] result = new byte[cropWidth * cropHeight * 4];
		for (int y = 0; y < cropHeight; y++)
		{
			Buffer.BlockCopy(pixels, ((top + y) * width + left) * 4, result, y * cropWidth * 4, cropWidth * 4);
		}
		return result;
	}

	private static (byte[] Pixels, int Width, int Height) Load(string path)
	{
		using FileStream stream = File.OpenRead(path);
		BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
		BitmapSource frame = decoder.Frames[0];
		if (frame.Format != PixelFormats.Bgra32)
		{
			frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
		}
		int width = frame.PixelWidth;
		int height = frame.PixelHeight;
		byte[] pixels = new byte[width * height * 4];
		frame.CopyPixels(pixels, width * 4, 0);
		return (pixels, width, height);
	}

	private static void Save(string path, byte[] pixels, int width, int height)
	{
		PngBitmapEncoder encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(Create(pixels, width, height)));
		string temporary = path + ".tmp";
		using (FileStream stream = File.Create(temporary))
		{
			encoder.Save(stream);
		}
		File.Move(temporary, path, overwrite: true);
	}

	private static BitmapSource Create(byte[] pixels, int width, int height)
	{
		BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
		bitmap.Freeze();
		return bitmap;
	}
}
