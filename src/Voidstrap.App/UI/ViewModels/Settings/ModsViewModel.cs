using Point = System.Windows.Point;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using Voidstrap.AppData;
using Voidstrap.Enums;
using Voidstrap.Integrations;
using Voidstrap.Models.SettingTasks;
using Voidstrap.Resources;
using Voidstrap.Integrations.AssetProxy;
using Voidstrap.UI.Elements.ContextMenu;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.Utility;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Voidstrap.UI.ViewModels.Settings;

public class ModsViewModel : NotifyPropertyChangedViewModel
{
	public class SkyboxPack
	{
		public string Name { get; set; } = "";

		public Uri? DownloadUri { get; set; }

		public override string ToString()
		{
			return Name;
		}
	}

	public enum CrosshairShape
	{
		Cross,
		Dot,
		Circle,
		Image
	}

	private const string GitHubApiBase = "https://api.github.com/repos/KloBraticc/ModsHub-Reworked-/contents";

	private static readonly string RepoRoot = "https://api.github.com/repos/KloBraticc/SkyboxPackV2/contents";

	private static readonly HttpClient _http = CreateHttpClient();
	private static readonly SemaphoreSlim PreviewProbeGate = new SemaphoreSlim(8, 8);

	private static HttpClient CreateHttpClient()
	{
		HttpClient client = Voidstrap.Utility.VpnHttpClient.Create();
		client.DefaultRequestHeaders.UserAgent.ParseAdd("VoidstrapApp");
		return client;
	}

	private SkyboxPack? _selectedSkyboxPack;

	private GoogleFontOption? _selectedGoogleFont;

	private CancellationTokenSource? _fontManagerCts;

	private CancellationTokenSource? _fontPreviewCts;

	private CancellationTokenSource? _skyboxManagerCts;

	private CancellationTokenSource? _skyboxImportCts;

	private bool _fontManagerBusy;

	private bool _skyboxManagerBusy;

	private bool _customSkyboxBusy;

	private string _customSkyboxBack = string.Empty;

	private string _customSkyboxDown = string.Empty;

	private string _customSkyboxFront = string.Empty;

	private string _customSkyboxLeft = string.Empty;

	private string _customSkyboxRight = string.Empty;

	private string _customSkyboxUp = string.Empty;

	private string _fontManagerStatus = "Loading the font catalog...";

	private string _skyboxManagerStatus = "Loading skyboxes...";

	private string _activePreviewFontPath = string.Empty;

	private System.Windows.Media.FontFamily _activePreviewFontFamily = new("Segoe UI");

	private string _selectedPreviewFontFamilyName = string.Empty;

	private System.Windows.Media.FontFamily _selectedPreviewFontFamily = new("Segoe UI");

	private int _selectedCustomCursorSetIndex;

	private string _selectedCustomCursorSetName = string.Empty;

	private CrosshairShape _selectedShape;

	private string _cursorColorHex = "#00FF00";

	private string _cursorOutlineColorHex = "#000000";

	private int _cursorSize = 20;

	private int _crosshairThickness = 2;

	private int _gap = 4;

	private double _cursorOpacity = 1.0;

	private string _cursorCode;

	private ImageSource _cursorPreview;

	private bool _useImageCrosshair;

	private string _imageUrl;

	private readonly string _dir = Paths.UserData;

	private readonly string _file;

	private CancellationTokenSource? _deathSoundConversionCts;

	private long _deathSoundConversionGeneration;

	private string _shiftlockCursorSelectedPath = "";

	private string _arrowCursorSelectedPath = "";

	private string _arrowFarCursorSelectedPath = "";

	private string _iBeamCursorSelectedPath = "";

	private ImageSource? _shiftlockCursorPreview;

	private ImageSource? _arrowCursorPreview;

	private ImageSource? _arrowFarCursorPreview;

	private ImageSource? _iBeamCursorPreview;

	private bool _modExplorerVisible;

	private ModFile? _selectedModFile;

	private string _currentExplorerPath = "";

	private CancellationTokenSource? _explorerCts;

	private string? _robloxPlayerDirCache;

	private string _explorerStatusMessage = "";

	private string _explorerSearchText = "";

	private static readonly string[] HiddenExplorerItems = new string[9] { "AppSettings.xml", "RobloxPlayerBeta.exe", "RobloxPlayerBeta.dll", "WebView2Loader.dll", "RobloxCrashHandler.exe", "COPYRIGHT.txt", "WebView2RuntimeInstaller", "ssl", "Logs" };

	private string _cacheFilter = "All";

	private string _captureSearch = "";

	private bool _showAssetNames = true;

	private string _captureStatsText = "";

	private string _assetWarpStatus = "";

	private DispatcherTimer? _captureTimer;

	private bool _captureBrowserActive;

	private bool _resolving;

	private readonly HashSet<string> _attemptedNames = new HashSet<string>(StringComparer.Ordinal);

	private DateTime _nameCooldownUntil = DateTime.MinValue;

	private readonly List<ManagedModItem> _allManagedMods = [];

	private string _managedModSearchText = string.Empty;

	private string _managedModsSummary = "No managed mods";

	private bool _managedModsBusy;

	private string _managedModsEmptyTitle = "Your managed mod library is empty";

	private string _managedModsEmptyDescription = "Add a mod to create its indexed folder, then place its files inside.";

	private readonly SemaphoreSlim _managedModsLoadGate = new(1, 1);

	private readonly SemaphoreSlim _managedModsMutationGate = new(1, 1);

	public ObservableCollection<ModInfo> AvailableMods { get; set; } = new ObservableCollection<ModInfo>();

	public ObservableCollection<ManagedModItem> ManagedMods { get; } = [];

	public string ManagedModSearchText
	{
		get => _managedModSearchText;
		set
		{
			if (SetProperty(ref _managedModSearchText, value))
				ApplyManagedModFilter();
		}
	}

	public string ManagedModsSummary
	{
		get => _managedModsSummary;
		private set => SetProperty(ref _managedModsSummary, value);
	}

	public bool ManagedModsBusy
	{
		get => _managedModsBusy;
		private set => SetProperty(ref _managedModsBusy, value);
	}

	public string ManagedModsEmptyTitle
	{
		get => _managedModsEmptyTitle;
		private set => SetProperty(ref _managedModsEmptyTitle, value);
	}

	public string ManagedModsEmptyDescription
	{
		get => _managedModsEmptyDescription;
		private set => SetProperty(ref _managedModsEmptyDescription, value);
	}

	public bool IsWindows => Voidstrap.Utility.Platform.IsWindows;

	public string BrightnessDisplay
	{
		get
		{
			if (Brightness != 50.0)
			{
				return $"{Brightness:0}";
			}
			return "Disabled";
		}
	}

	public double Saturation
	{
		get
		{
			return App.Settings.Prop.Saturation;
		}
		set
		{
			double num = Math.Clamp(value, 0.0, 200.0);
			if (App.Settings.Prop.Saturation != num)
			{
				App.Settings.Prop.Saturation = num;
				OnPropertyChanged("Saturation");
				OnPropertyChanged("SaturationDisplay");
			}
		}
	}

	public string SaturationDisplay
	{
		get
		{
			if (Saturation != 100.0)
			{
				return $"{Saturation:0}";
			}
			return "Disabled";
		}
	}

	public double Contrast
	{
		get
		{
			return App.Settings.Prop.Contrast;
		}
		set
		{
			double num = Math.Clamp(value, 0.0, 200.0);
			if (App.Settings.Prop.Contrast != num)
			{
				App.Settings.Prop.Contrast = num;
				OnPropertyChanged("Contrast");
				OnPropertyChanged("ContrastDisplay");
			}
		}
	}

	public string ContrastDisplay
	{
		get
		{
			if (Contrast != 100.0)
			{
				return $"{Contrast:0}";
			}
			return "Disabled";
		}
	}

	public double ColorTemperature
	{
		get
		{
			return App.Settings.Prop.ColorTemperature;
		}
		set
		{
			double num = Math.Clamp(value, -100.0, 100.0);
			if (App.Settings.Prop.ColorTemperature != num)
			{
				App.Settings.Prop.ColorTemperature = num;
				OnPropertyChanged("ColorTemperature");
				OnPropertyChanged("ColorTemperatureDisplay");
			}
		}
	}

	public string ColorTemperatureDisplay
	{
		get
		{
			if (ColorTemperature != 0.0)
			{
				return $"{ColorTemperature:0}";
			}
			return "Disabled";
		}
	}

	public bool ColorBlindnessEnabled
	{
		get
		{
			return App.Settings.Prop.ColorBlindnessEnabled;
		}
		set
		{
			if (App.Settings.Prop.ColorBlindnessEnabled != value)
			{
				App.Settings.Prop.ColorBlindnessEnabled = value;
				OnPropertyChanged("ColorBlindnessEnabled");
			}
		}
	}

	public int ColorBlindnessType
	{
		get
		{
			return App.Settings.Prop.ColorBlindnessType;
		}
		set
		{
			int clamped = Math.Clamp(value, 0, 2);
			if (App.Settings.Prop.ColorBlindnessType != clamped)
			{
				App.Settings.Prop.ColorBlindnessType = clamped;
				OnPropertyChanged("ColorBlindnessType");
			}
		}
	}

	public double ColorBlindnessSeverity
	{
		get
		{
			return App.Settings.Prop.ColorBlindnessSeverity;
		}
		set
		{
			double clamped = Math.Clamp(value, 0.0, 100.0);
			if (App.Settings.Prop.ColorBlindnessSeverity != clamped)
			{
				App.Settings.Prop.ColorBlindnessSeverity = clamped;
				OnPropertyChanged("ColorBlindnessSeverity");
				OnPropertyChanged("ColorBlindnessSeverityDisplay");
			}
		}
	}

	public string ColorBlindnessSeverityDisplay
	{
		get
		{
			double val = ColorBlindnessSeverity;
			if (val <= 0.0) return "Disabled";
			if (val <= 25.0) return "Mild";
			if (val <= 50.0) return "Moderate";
			if (val <= 75.0) return "Strong";
			return "Full";
		}
	}

	public bool ColorBlindnessSimulate
	{
		get
		{
			return App.Settings.Prop.ColorBlindnessSimulate;
		}
		set
		{
			if (App.Settings.Prop.ColorBlindnessSimulate != value)
			{
				App.Settings.Prop.ColorBlindnessSimulate = value;
				OnPropertyChanged("ColorBlindnessSimulate");
			}
		}
	}


public ICommand PickCursorColorCommand { get; }

	public ICommand PickOutlineColorCommand { get; }

	public ICommand PickHomepageBackgroundColorCommand { get; }

	public ICommand PickHomepageBackgroundGradientColorCommand { get; }

	public ICommand ChooseHomepageBackgroundMediaCommand { get; }

	public ICommand ClearHomepageBackgroundMediaCommand { get; }

	public ICommand ToggleHomepageBackgroundMediaCommand { get; }

	public ICommand GenerateCursorCodeCommand { get; }

	public ICommand ApplyCursorCodeCommand { get; }

	public ObservableCollection<SkyboxPack> AvailableSkyboxPacks { get; } = new ObservableCollection<SkyboxPack>();

	public ObservableCollection<GoogleFontOption> AvailableGoogleFonts { get; private set; } = [];

	public ICommand RefreshSkyboxesCommand { get; }

	public ICommand ChooseSkyboxFaceCommand { get; }

	public ICommand ChooseSingleSkyboxImageCommand { get; }

	public ICommand ApplyCustomSkyboxCommand { get; }

	public ICommand RemoveCustomSkyboxCommand { get; }

	public ICommand RefreshGoogleFontsCommand { get; }

	public ICommand ApplyGoogleFontCommand { get; }

	public ICommand ChooseLocalFontCommand { get; }

	public ICommand RemoveCustomFontCommand { get; }

	public SkyboxPack? SelectedSkyboxPack
	{
		get
		{
			return _selectedSkyboxPack;
		}
		set
		{
			if (_selectedSkyboxPack != value)
			{
				_selectedSkyboxPack = value;
				OnPropertyChanged("SelectedSkyboxPack");
				if (_selectedSkyboxPack != null)
				{
					App.Settings.Prop.SkyboxName = _selectedSkyboxPack.Name;
				}
			}
		}
	}

	public GoogleFontOption? SelectedGoogleFont
	{
		get => _selectedGoogleFont;
		set
		{
			if (ReferenceEquals(_selectedGoogleFont, value))
				return;
			_selectedGoogleFont = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(FontManagerCanApply));
			OnPropertyChanged(nameof(FontPreviewVisible));
			QueueSelectedFontPreview();
		}
	}

	public bool FontManagerBusy
	{
		get => _fontManagerBusy;
		private set
		{
			if (_fontManagerBusy == value)
				return;
			_fontManagerBusy = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(FontManagerCanApply));
			OnPropertyChanged(nameof(FontManagerReady));
		}
	}

	public bool SkyboxManagerBusy
	{
		get => _skyboxManagerBusy;
		private set
		{
			if (_skyboxManagerBusy == value)
				return;
			_skyboxManagerBusy = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(SkyboxManagerReady));
		}
	}

	public string FontManagerStatus
	{
		get => _fontManagerStatus;
		private set
		{
			if (_fontManagerStatus == value)
				return;
			_fontManagerStatus = value;
			OnPropertyChanged();
		}
	}

	public string SkyboxManagerStatus
	{
		get => _skyboxManagerStatus;
		private set
		{
			if (_skyboxManagerStatus == value)
				return;
			_skyboxManagerStatus = value;
			OnPropertyChanged();
		}
	}

	public bool FontManagerCanApply => !FontManagerBusy && SelectedGoogleFont != null;

	public bool FontManagerReady => !FontManagerBusy;

	public bool SkyboxManagerReady => !SkyboxManagerBusy && AvailableSkyboxPacks.Count > 0;

	public bool CustomSkyboxBusy
	{
		get => _customSkyboxBusy;
		private set
		{
			if (_customSkyboxBusy == value)
				return;
			_customSkyboxBusy = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(CustomSkyboxCanApply));
			OnPropertyChanged(nameof(CustomSkyboxControlsEnabled));
		}
	}

	public bool CustomSkyboxControlsEnabled => !CustomSkyboxBusy;

	public bool CustomSkyboxCanApply => !CustomSkyboxBusy && GetCustomSkyboxSources().Values.All(File.Exists);

	public bool HasCustomSkybox => SkyboxImageConverter.HasCustomPack();

	public string CustomSkyboxBack => GetSkyboxFaceDisplayName(_customSkyboxBack);

	public string CustomSkyboxDown => GetSkyboxFaceDisplayName(_customSkyboxDown);

	public string CustomSkyboxFront => GetSkyboxFaceDisplayName(_customSkyboxFront);

	public string CustomSkyboxLeft => GetSkyboxFaceDisplayName(_customSkyboxLeft);

	public string CustomSkyboxRight => GetSkyboxFaceDisplayName(_customSkyboxRight);

	public string CustomSkyboxUp => GetSkyboxFaceDisplayName(_customSkyboxUp);

	public bool HasCustomFont => !string.IsNullOrEmpty(TextFontTask.NewState);

	public bool FontPreviewVisible => SelectedGoogleFont != null || HasCustomFont;

	public string ActiveFontName
	{
		get
		{
			if (!HasCustomFont)
				return "Roblox default";
			if (!string.IsNullOrWhiteSpace(App.Settings.Prop.CustomFontLocation))
				return App.Settings.Prop.CustomFontLocation;
			return Path.GetFileNameWithoutExtension(TextFontTask.NewState);
		}
	}

	public IEnumerable<Voidstrap.Enums.ModApplyTarget> ModApplyTargets { get; } = Enum.GetValues<Voidstrap.Enums.ModApplyTarget>();

	public Voidstrap.Enums.ModApplyTarget ModApplyTarget
	{
		get => App.Settings.Prop.ModApplyTarget;
		set
		{
			if (App.Settings.Prop.ModApplyTarget == value)
				return;
			App.Settings.Prop.ModApplyTarget = value;
			OnPropertyChanged(nameof(ModApplyTarget));
		}
	}

	public ICommand OpenModsFolderCommand => new RelayCommand(OpenModsFolder);

	public ICommand AddManagedModCommand { get; }

	public ICommand RefreshManagedModsCommand { get; }

	public ICommand OpenManagedModsRootCommand { get; }

	public ICommand OpenManagedModCommand { get; }

	public ICommand RenameManagedModCommand { get; }

	public ICommand RemoveManagedModCommand { get; }

	public ICommand ToggleManagedModCommand { get; }

	public ICommand CopyManagedModIdCommand { get; }

	public ICommand AddCustomCursorModCommand => new RelayCommand(AddCustomCursorMod);

	public ICommand RemoveCustomCursorModCommand => new RelayCommand(RemoveCustomCursorMod);

	public ICommand AddCustomShiftlockModCommand => new RelayCommand(AddCustomShiftlockMod);

	public ICommand RemoveCustomShiftlockModCommand => new RelayCommand(RemoveCustomShiftlockMod);

	public ICommand AddCustomDeathSoundCommand => new AsyncRelayCommand(AddCustomDeathSoundAsync);

	public ICommand RemoveCustomDeathSoundCommand => new RelayCommand(RemoveCustomDeathSound);

	public Visibility ChooseCustomFontVisibility
	{
		get
		{
			if (string.IsNullOrEmpty(TextFontTask.NewState))
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public Visibility DeleteCustomFontVisibility
	{
		get
		{
			if (string.IsNullOrEmpty(TextFontTask.NewState))
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public string DeleteCustomFontFontName
	{
		get
		{
			if (string.IsNullOrEmpty(TextFontTask.NewState))
			{
				return "";
			}
			return Path.GetFileName(TextFontTask.NewState);
		}
	}

	public System.Windows.Media.FontFamily FontPreviewFontFamily
	{
		get
		{
			GoogleFontOption? selected = SelectedGoogleFont;
			if (selected != null && string.Equals(selected.Family, _selectedPreviewFontFamilyName, StringComparison.OrdinalIgnoreCase))
			{
				return _selectedPreviewFontFamily;
			}
			return GetActivePreviewFontFamily();
		}
	}

	public System.Windows.Media.FontFamily DeleteCustomFontFontFamily => GetActivePreviewFontFamily();

	private System.Windows.Media.FontFamily GetActivePreviewFontFamily()
	{
		string path = TextFontTask.NewState ?? string.Empty;
		if (string.Equals(path, _activePreviewFontPath, StringComparison.OrdinalIgnoreCase))
			return _activePreviewFontFamily;
		_activePreviewFontPath = path;
		_activePreviewFontFamily = !string.IsNullOrEmpty(path) && File.Exists(path) && TryCreatePreviewFontFamily(path, out System.Windows.Media.FontFamily family)
			? family
			: new System.Windows.Media.FontFamily("Segoe UI");
		return _activePreviewFontFamily;
	}

	private static bool TryCreatePreviewFontFamily(string path, out System.Windows.Media.FontFamily family)
	{
		family = new System.Windows.Media.FontFamily("Segoe UI");
		try
		{
			FileInfo file = new(path);
			if (!file.Exists || file.Length < 12 || file.Length > GoogleFontsService.MaximumFontBytes)
				return false;
			if (!GoogleFontsService.TryReadFamilyName(path, out string familyName))
				return false;
			string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
			if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(directory))
				return false;

			family = new System.Windows.Media.FontFamily(new Uri(directory + Path.DirectorySeparatorChar, UriKind.Absolute), "./#" + familyName);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::FontPreview", ex);
		}
		return false;
	}

	private void QueueSelectedFontPreview()
	{
		CancellationTokenSource? previous = Interlocked.Exchange(ref _fontPreviewCts, null);
		previous?.Cancel();
		previous?.Dispose();
		_selectedPreviewFontFamilyName = string.Empty;
		_selectedPreviewFontFamily = GetActivePreviewFontFamily();
		OnPropertyChanged(nameof(FontPreviewFontFamily));

		GoogleFontOption? selected = SelectedGoogleFont;
		if (selected == null)
			return;

		CancellationTokenSource cancellation = new();
		_fontPreviewCts = cancellation;
		_ = LoadSelectedFontPreviewAsync(selected, cancellation);
	}

	private async Task LoadSelectedFontPreviewAsync(GoogleFontOption selected, CancellationTokenSource cancellation)
	{
		try
		{
			await Task.Delay(220, cancellation.Token);
			string path = await GoogleFontsService.DownloadAsync(selected, cancellation.Token);
			if (!ReferenceEquals(Volatile.Read(ref _fontPreviewCts), cancellation) || !ReferenceEquals(SelectedGoogleFont, selected))
				return;
			if (!TryCreatePreviewFontFamily(path, out System.Windows.Media.FontFamily family))
			{
				App.Logger.WriteLine("ModsViewModel::FontPreview", "The selected font has no previewable typeface");
				return;
			}
			_selectedPreviewFontFamilyName = selected.Family;
			_selectedPreviewFontFamily = family;
			OnPropertyChanged(nameof(FontPreviewFontFamily));
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
			return;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::FontPreview", ex);
		}
		finally
		{
			if (Interlocked.CompareExchange(ref _fontPreviewCts, null, cancellation) == cancellation)
			{
				cancellation.Dispose();
			}
		}
	}

	public ICommand ManageCustomFontCommand => new RelayCommand(ManageCustomFont);

	public IReadOnlyList<string> CustomFontScaleOptions { get; } = new string[13] { "0%", "25%", "50%", "75%", "90%", "100%", "125%", "150%", "200%", "250%", "300%", "400%", "500%" };

	public string CustomFontScale
	{
		get
		{
			return PercentToText(App.Settings.Prop.CustomFontScale);
		}
		set
		{
			double scale = TextToPercent(value, 0.0, 5.0);

			if (Math.Abs(scale - App.Settings.Prop.CustomFontScale) < 0.0001)
			{
				return;
			}

			App.Settings.Prop.CustomFontScale = scale;
			OnPropertyChanged("CustomFontScale");

			FontModPresetTask.Rescale(TextFontTask.NewState);
		}
	}

	public IReadOnlyList<string> CustomDeathSoundVolumeOptions { get; } = new string[12] { "25%", "50%", "75%", "90%", "100%", "125%", "150%", "200%", "250%", "300%", "400%", "500%" };

	public string CustomDeathSoundVolume
	{
		get
		{
			return PercentToText(App.Settings.Prop.CustomDeathSoundVolume);
		}
		set
		{
			double volume = TextToPercent(value, 0.25, 5.0);

			if (Math.Abs(volume - App.Settings.Prop.CustomDeathSoundVolume) < 0.0001)
			{
				return;
			}

			App.Settings.Prop.CustomDeathSoundVolume = volume;
			OnPropertyChanged("CustomDeathSoundVolume");

			ApplyDeathSoundVolume();
		}
	}

	public Visibility CustomDeathSoundVolumeVisibility => File.Exists(Paths.CustomDeathSoundSource) ? Visibility.Visible : Visibility.Collapsed;

	private static string PercentToText(double value)
	{
		return ((int)Math.Round(value * 100.0)).ToString(CultureInfo.InvariantCulture) + "%";
	}

	private static double TextToPercent(string? text, double minimum, double maximum)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return 1.0;
		}

		if (!int.TryParse(text.TrimEnd('%').Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
		{
			return 1.0;
		}

		return Math.Clamp(percent / 100.0, minimum, maximum);
	}

	public ICommand OpenCompatSettingsCommand => new RelayCommand(OpenCompatSettings);

	public ModPresetTask OldDeathSoundTask { get; } = new ModPresetTask("OldDeathSound", "content\\sounds\\oof.ogg", "Sounds.OldDeath.ogg");

	public ModPresetTask OldAvatarBackgroundTask { get; } = new ModPresetTask("OldAvatarBackground", "ExtraContent\\places\\Mobile.rbxl", "OldAvatarBackground.rbxl");

	public ModPresetTask OldCharacterSoundsTask { get; } = new ModPresetTask("OldCharacterSounds", new Dictionary<string, string>
	{
		{ "content\\sounds\\action_footsteps_plastic.mp3", "Sounds.OldWalk.mp3" },
		{ "content\\sounds\\action_jump.mp3", "Sounds.OldJump.mp3" },
		{ "content\\sounds\\action_get_up.mp3", "Sounds.OldGetUp.mp3" },
		{ "content\\sounds\\action_falling.mp3", "Sounds.Empty.mp3" },
		{ "content\\sounds\\action_jump_land.mp3", "Sounds.Empty.mp3" },
		{ "content\\sounds\\action_swim.mp3", "Sounds.Empty.mp3" },
		{ "content\\sounds\\impact_water.mp3", "Sounds.Empty.mp3" }
	});

	public EmojiModPresetTask EmojiFontTask { get; } = new EmojiModPresetTask();

	public EnumModPresetTask<Voidstrap.Enums.CursorType> CursorTypeTask { get; } = new EnumModPresetTask<Voidstrap.Enums.CursorType>("CursorType", new Dictionary<Voidstrap.Enums.CursorType, Dictionary<string, string>>
	{
		{
			Voidstrap.Enums.CursorType.DotCursor,
			new Dictionary<string, string>
			{
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.DotCursor.ArrowCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.DotCursor.ArrowFarCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.DotCursor.ArrowCursorDecalDrag.png" }
			}
		},
		{
			Voidstrap.Enums.CursorType.WhiteDotCursor,
			new Dictionary<string, string>
			{
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.WhiteDotCursor.ArrowCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.WhiteDotCursor.ArrowFarCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.WhiteDotCursor.ArrowCursorDecalDrag.png" }
			}
		},
		{
			Voidstrap.Enums.CursorType.VerySmallWhiteDot,
			new Dictionary<string, string>
			{
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.VerySmallWhiteDot.ArrowCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.VerySmallWhiteDot.ArrowFarCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.VerySmallWhiteDot.ArrowCursorDecalDrag.png" }
			}
		},
		{
			Voidstrap.Enums.CursorType.StoofsCursor,
			new Dictionary<string, string>
			{
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.StoofsCursor.ArrowCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.StoofsCursor.ArrowFarCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.StoofsCursor.ArrowCursorDecalDrag.png" }
			}
		},
		{
			Voidstrap.Enums.CursorType.CleanCursor,
			new Dictionary<string, string>
			{
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.CleanCursor.ArrowCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.CleanCursor.ArrowFarCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.CleanCursor.ArrowCursorDecalDrag.png" }
			}
		},
		{
			Voidstrap.Enums.CursorType.FPSCursor,
			new Dictionary<string, string>
			{
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.FPSCursor.ArrowCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.FPSCursor.ArrowFarCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.FPSCursor.ArrowCursorDecalDrag.png" }
			}
		},
		{
			Voidstrap.Enums.CursorType.From2006,
			new Dictionary<string, string>
			{
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.From2006.ArrowCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.From2006.ArrowFarCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.From2006.ArrowCursorDecalDrag.png" }
			}
		},
		{
			Voidstrap.Enums.CursorType.From2013,
			new Dictionary<string, string>
			{
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursor.png", "Cursor.From2013.ArrowCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowFarCursor.png", "Cursor.From2013.ArrowFarCursor.png" },
				{ "content\\textures\\Cursors\\KeyboardMouse\\ArrowCursorDecalDrag.png", "Cursor.From2013.ArrowCursorDecalDrag.png" }
			}
		}
	});

	public bool SkyboxEnabled
	{
		get
		{
			return App.Settings.Prop.SkyBoxDataSending;
		}
		set
		{
			if (App.Settings.Prop.SkyBoxDataSending == value)
				return;
			App.Settings.Prop.SkyBoxDataSending = value;
			OnPropertyChanged();
		}
	}

	public bool OverlaysEnabled
	{
		get
		{
			return App.Settings.Prop.OverlaysEnabled;
		}
		set
		{
			App.Settings.Prop.OverlaysEnabled = value;
		}
	}

	public bool HomepageBackgroundOverlayEnabled
	{
		get => App.Settings.Prop.HomepageBackgroundOverlayEnabled;
		set
		{
			if (App.Settings.Prop.HomepageBackgroundOverlayEnabled == value)
				return;
			App.Settings.Prop.HomepageBackgroundOverlayEnabled = value;
			App.Settings.SaveDeferred();
			OnPropertyChanged();
			Voidstrap.Integrations.Overlays.OverlayHub.Refresh();
			if (value)
				Frontend.ShowMessageBox("Roblox light mode is not supported. Make sure Roblox dark mode is selected.", MessageBoxImage.Warning);
		}
	}

	public string HomepageBackgroundOverlayColor
	{
		get => NormalizeHomepageColor(App.Settings.Prop.HomepageBackgroundOverlayColor);
		set
		{
			string color = NormalizeHomepageColor(value);
			if (string.Equals(App.Settings.Prop.HomepageBackgroundOverlayColor, color, StringComparison.OrdinalIgnoreCase))
				return;
			App.Settings.Prop.HomepageBackgroundOverlayColor = color;
			App.Settings.SaveDeferred();
			OnPropertyChanged();
		}
	}

	public string HomepageBackgroundOverlayMediaName
	{
		get
		{
			string path = HomepageBackgroundOverlayMediaPath;
			return path.Length > 0 ? Path.GetFileName(path) : "No file selected";
		}
	}

	private string? _homepageResolvedPath;

	public string HomepageBackgroundOverlayMediaPath
	{
		get
		{
			if (_homepageResolvedPath == null)
			{
				string path = App.Settings.Prop.HomepageBackgroundOverlayMediaPath ?? "";
				_homepageResolvedPath = path.Length > 0 && File.Exists(path) ? path : "";
			}
			return _homepageResolvedPath;
		}
	}

	public bool HasHomepageBackgroundMedia => HomepageBackgroundOverlayMediaPath.Length > 0;

	public string HomepageBackgroundMediaButtonText => HasHomepageBackgroundMedia ? "Remove" : "Choose file";

	private static readonly string[] HomepageVideoExtensions =
		[".mp4", ".m4v", ".webm", ".avi", ".mov", ".wmv", ".mpeg", ".mpg", ".mkv"];

	private const long MaxHomepagePreviewBytes = 64L * 1024L * 1024L;

	private const long MaxAnimatedPreviewBytes = 12L * 1024L * 1024L;

	private const int HomepagePreviewDecodeWidth = 480;

	private bool _homepagePreviewRequested;

	private System.Windows.Media.ImageSource? _homepagePreviewStill;

	private System.Windows.Media.ImageSource? _homepagePreviewAnimated;

	private Uri? _homepagePreviewVideoUri;

	private string _homepagePreviewMessage = "";

	private CancellationTokenSource? _homepagePreviewCancel;

	public System.Windows.Media.ImageSource? HomepageMediaStillSource
	{
		get
		{
			BeginHomepagePreview();
			return _homepagePreviewStill;
		}
	}

	public System.Windows.Media.ImageSource? HomepageMediaAnimatedSource
	{
		get
		{
			BeginHomepagePreview();
			return _homepagePreviewAnimated;
		}
	}

	public Uri? HomepageMediaPreviewVideoUri
	{
		get
		{
			BeginHomepagePreview();
			return _homepagePreviewVideoUri;
		}
	}

	public string HomepageMediaPreviewMessage
	{
		get
		{
			BeginHomepagePreview();
			return _homepagePreviewMessage;
		}
	}

	public bool HasHomepageMediaPreviewMessage => HomepageMediaPreviewMessage.Length > 0;

	public bool HomepageMediaIsVideo => HomepageMediaPreviewVideoUri != null;

	public bool HomepageMediaIsAnimated => HomepageMediaAnimatedSource != null;

	public bool HomepageMediaIsStill => HomepageMediaAnimatedSource == null && HomepageMediaStillSource != null;

	public void ReleaseHomepageMediaPreview()
	{
		CancellationTokenSource? cancel = _homepagePreviewCancel;
		_homepagePreviewCancel = null;
		if (cancel != null)
		{
			try
			{
				cancel.Cancel();
			}
			catch
			{
			}
			cancel.Dispose();
		}

		bool hadPreview = _homepagePreviewStill != null || _homepagePreviewAnimated != null || _homepagePreviewVideoUri != null || _homepagePreviewMessage.Length > 0;
		_homepagePreviewRequested = false;
		_homepagePreviewStill = null;
		_homepagePreviewAnimated = null;
		_homepagePreviewVideoUri = null;
		_homepagePreviewMessage = "";
		if (hadPreview)
			NotifyHomepagePreviewChanged();
	}

	private void NotifyHomepagePreviewChanged()
	{
		OnPropertyChanged(nameof(HomepageMediaStillSource));
		OnPropertyChanged(nameof(HomepageMediaAnimatedSource));
		OnPropertyChanged(nameof(HomepageMediaPreviewVideoUri));
		OnPropertyChanged(nameof(HomepageMediaPreviewMessage));
		OnPropertyChanged(nameof(HasHomepageMediaPreviewMessage));
		OnPropertyChanged(nameof(HomepageMediaIsVideo));
		OnPropertyChanged(nameof(HomepageMediaIsAnimated));
		OnPropertyChanged(nameof(HomepageMediaIsStill));
	}

	private void BeginHomepagePreview()
	{
		if (_homepagePreviewRequested)
			return;
		_homepagePreviewRequested = true;

		string path = HomepageBackgroundOverlayMediaPath;
		if (path.Length == 0)
			return;

		string extension = Path.GetExtension(path).ToLowerInvariant();
		if (HomepageVideoExtensions.Contains(extension))
		{
			try
			{
				_homepagePreviewVideoUri = new Uri(path, UriKind.Absolute);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ModsViewModel::HomepageMediaPreview", "The video preview could not be opened: " + ex.Message);
				_homepagePreviewMessage = "This file could not be previewed.";
			}
			Dispatcher.CurrentDispatcher.BeginInvoke(new Action(NotifyHomepagePreviewChanged), DispatcherPriority.Background);
			return;
		}

		CancellationTokenSource cancel = new();
		_homepagePreviewCancel = cancel;
		Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
		CancellationToken token = cancel.Token;

		Task.Run(() =>
		{
			System.Windows.Media.ImageSource? still = null;
			string message = "";
			long length = 0;
			try
			{
				length = new FileInfo(path).Length;
				if (length > MaxHomepagePreviewBytes)
					message = "This file is too large to preview.";
				else
					still = BuildStillPreview(path);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ModsViewModel::HomepageMediaPreview", "The preview could not be built: " + ex.Message);
				message = "This file could not be previewed.";
			}

			if (token.IsCancellationRequested)
				return;

			dispatcher.BeginInvoke(new Action(() =>
			{
				if (token.IsCancellationRequested)
					return;
				_homepagePreviewStill = still;
				_homepagePreviewMessage = message;
				NotifyHomepagePreviewChanged();
			}), DispatcherPriority.Background);

			if (still == null || extension != ".gif" || length > MaxAnimatedPreviewBytes || token.IsCancellationRequested)
				return;

			System.Windows.Media.ImageSource? animated = null;
			try
			{
				BitmapImage bitmap = new();
				bitmap.BeginInit();
				bitmap.UriSource = new Uri(path, UriKind.Absolute);
				bitmap.CacheOption = BitmapCacheOption.OnLoad;
				bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
				bitmap.EndInit();
				bitmap.Freeze();
				animated = bitmap;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ModsViewModel::HomepageMediaPreview", "The animated preview could not be built: " + ex.Message);
				return;
			}

			if (token.IsCancellationRequested)
				return;

			dispatcher.BeginInvoke(new Action(() =>
			{
				if (token.IsCancellationRequested)
					return;
				_homepagePreviewAnimated = animated;
				NotifyHomepagePreviewChanged();
			}), DispatcherPriority.ApplicationIdle);
		}, token);
	}

	private static System.Windows.Media.ImageSource BuildStillPreview(string path)
	{
		BitmapImage source = new();
		source.BeginInit();
		source.UriSource = new Uri(path, UriKind.Absolute);
		source.CacheOption = BitmapCacheOption.OnLoad;
		source.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
		source.DecodePixelWidth = HomepagePreviewDecodeWidth;
		source.EndInit();
		WriteableBitmap still = new(source);
		still.Freeze();
		return still;
	}

	public IReadOnlyList<string> HomepageBackgroundModes { get; } = ["Solid color", "Gradient", "Image or video"];

	public string SelectedHomepageBackgroundMode
	{
		get => Voidstrap.Integrations.Overlays.OverlaySettings.HomepageBackgroundMode switch
		{
			"Gradient" => "Gradient",
			"Media" => "Image or video",
			_ => "Solid color"
		};
		set
		{
			string mode = value switch
			{
				"Gradient" => "Gradient",
				"Image or video" => "Media",
				_ => "Solid"
			};
			if (App.Settings.Prop.HomepageBackgroundOverlayMode == mode)
				return;
			App.Settings.Prop.HomepageBackgroundOverlayMode = mode;
			App.Settings.Prop.HomepageBackgroundOverlayGradientEnabled = mode == "Gradient";
			App.Settings.SaveDeferred();
			OnPropertyChanged();
			OnPropertyChanged(nameof(ShowHomepageSolidColor));
			OnPropertyChanged(nameof(ShowHomepageGradient));
			OnPropertyChanged(nameof(ShowHomepageMedia));
			Voidstrap.Integrations.Overlays.OverlayHub.Restart();
		}
	}

	public bool ShowHomepageSolidColor => SelectedHomepageBackgroundMode == "Solid color";

	public bool ShowHomepageGradient => SelectedHomepageBackgroundMode == "Gradient";

	public bool ShowHomepageMedia => SelectedHomepageBackgroundMode == "Image or video";

	public bool HomepageBackgroundOverlayGradientEnabled
	{
		get => App.Settings.Prop.HomepageBackgroundOverlayGradientEnabled;
		set
		{
			if (App.Settings.Prop.HomepageBackgroundOverlayGradientEnabled == value)
				return;
			App.Settings.Prop.HomepageBackgroundOverlayGradientEnabled = value;
			App.Settings.SaveDeferred();
			OnPropertyChanged();
		}
	}

	public string HomepageBackgroundOverlayGradientColor
	{
		get => NormalizeHomepageColor(App.Settings.Prop.HomepageBackgroundOverlayGradientColor);
		set
		{
			string color = NormalizeHomepageColor(value);
			if (string.Equals(App.Settings.Prop.HomepageBackgroundOverlayGradientColor, color, StringComparison.OrdinalIgnoreCase))
				return;
			App.Settings.Prop.HomepageBackgroundOverlayGradientColor = color;
			App.Settings.SaveDeferred();
			OnPropertyChanged();
		}
	}

	public double HomepageBackgroundOverlayGradientAngle
	{
		get => Math.Clamp(App.Settings.Prop.HomepageBackgroundOverlayGradientAngle, 0, 360);
		set
		{
			double angle = Math.Clamp(Math.Round(value), 0, 360);
			if (Math.Abs(App.Settings.Prop.HomepageBackgroundOverlayGradientAngle - angle) < 0.1)
				return;
			App.Settings.Prop.HomepageBackgroundOverlayGradientAngle = angle;
			App.Settings.SaveDeferred();
			OnPropertyChanged();
			OnPropertyChanged(nameof(HomepageBackgroundOverlayGradientAngleDisplay));
		}
	}

	public string HomepageBackgroundOverlayGradientAngleDisplay => $"{HomepageBackgroundOverlayGradientAngle:0}°";

	public bool RiShadeEnabled
	{
		get
		{
			return App.Settings.Prop.RiShadeEnabled;
		}
		set
		{
			Voidstrap.Integrations.RiShade.RiShadeManager.SetEnabled(value);
			OnPropertyChanged(nameof(RiShadeEnabled));
		}
	}

	public string[] AntiAliasingMethodNames => Voidstrap.Integrations.AntiAliasing.AntiAliasingSettings.MethodNames;

	public int AntiAliasingMethodIndex
	{
		get
		{
			return Voidstrap.Integrations.AntiAliasing.AntiAliasingSettings.MethodIndex;
		}
		set
		{
			if (value < 0)
				return;
			Voidstrap.Integrations.AntiAliasing.AntiAliasingManager.SetMethod(value);
			OnPropertyChanged(nameof(AntiAliasingMethodIndex));
		}
	}

	public bool FrameGenEnabled
	{
		get
		{
			return Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0;
		}
		set
		{
			Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetMode(value ? 1 : 0);
			OnPropertyChanged(nameof(FrameGenEnabled));
			RefreshFrameGenWarning();
		}
	}

	public bool FrameGenOverlayShow
	{
		get
		{
			return App.Settings.Prop.FrameGenOverlayShow;
		}
		set
		{
			App.Settings.Prop.FrameGenOverlayShow = value;
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(FrameGenOverlayShow));
		}
	}

	public bool FrameGenUncap
	{
		get
		{
			return App.Settings.Prop.FrameGenUncap;
		}
		set
		{
			App.Settings.Prop.FrameGenUncap = value;
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(FrameGenUncap));
		}
	}

	private const int FrameGenBestBelowFps = Voidstrap.Integrations.Overlays.RobloxFpsCap.BestBelowFps;

	private bool _frameGenCapWarningOpen;

	public bool FrameGenCapWarningOpen
	{
		get
		{
			return _frameGenCapWarningOpen;
		}
		private set
		{
			if (_frameGenCapWarningOpen == value)
				return;
			_frameGenCapWarningOpen = value;
			OnPropertyChanged(nameof(FrameGenCapWarningOpen));
		}
	}

	private string _frameGenCapWarningMessage = "";

	public string FrameGenCapWarningMessage
	{
		get
		{
			return _frameGenCapWarningMessage;
		}
		private set
		{
			if (_frameGenCapWarningMessage == value)
				return;
			_frameGenCapWarningMessage = value;
			OnPropertyChanged(nameof(FrameGenCapWarningMessage));
		}
	}

	public void RefreshFrameGenWarning()
	{
		Voidstrap.Integrations.Overlays.RobloxFpsCap.EnsureStarted();
		bool fgOn = Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0;
		int hz = Voidstrap.Integrations.Overlays.OverlayDisplay.RefreshHz();
		int cap = Voidstrap.Integrations.Overlays.RobloxFpsCap.Cap;
		double measured = Voidstrap.Integrations.Overlays.RobloxFpsCap.RecentMeasuredBase();
		int suggest = Voidstrap.Integrations.Overlays.RobloxFpsCap.PickBestCap(hz, measured);
		bool capTooHigh = Voidstrap.Integrations.Overlays.RobloxFpsCap.IsUnlimited || cap > hz;
		bool capTooHighForGen = !capTooHigh && cap >= FrameGenBestBelowFps;
		bool capTooLow = !capTooHigh && !capTooHighForGen && cap > 0 && cap < hz / 2 && measured >= 20 && measured >= cap - 3 && suggest > cap;
		bool open = fgOn && (capTooHigh || capTooHighForGen || capTooLow);
		if (capTooHigh)
			FrameGenCapWarningMessage = $"Your Roblox FPS cap is {Voidstrap.Integrations.Overlays.RobloxFpsCap.Describe()}, at or above your {hz}Hz display. Cap Roblox at {suggest} instead.";
		else if (capTooHighForGen)
			FrameGenCapWarningMessage = $"Frame generation works best below {FrameGenBestBelowFps} fps. Your Roblox FPS cap is {cap}, which leaves little room to insert frames. Cap Roblox at {suggest} instead.";
		else if (capTooLow)
			FrameGenCapWarningMessage = $"Your Roblox FPS cap is {cap} and your game is reaching it, so real motion can feel slow. Try {suggest} instead.";
		FrameGenCapWarningOpen = open;
	}

	public ICommand FixFrameCapCommand => new RelayCommand(FixFrameCap);

	private void FixFrameCap()
	{
		int hz = Voidstrap.Integrations.Overlays.OverlayDisplay.RefreshHz();
		double measured = Voidstrap.Integrations.Overlays.RobloxFpsCap.RecentMeasuredBase();
		int previousCap = Voidstrap.Integrations.Overlays.RobloxFpsCap.Cap;
		int cap = Voidstrap.Integrations.Overlays.RobloxFpsCap.PickBestCap(hz, measured);
		Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetTargetCap(cap);
		RefreshFrameGenWarning();
		App.Logger.WriteLine("FrameGen", $"Fix FrameCap set the Roblox FPS cap to {cap} for a {hz}Hz display, measured base {measured:0} fps, previous cap {previousCap}");
		Frontend.ShowMessageBox($"Roblox FPS cap set to {cap}. Frame generation fills you back up toward {hz}.", MessageBoxImage.Information);
	}

	public double Brightness
	{
		get
		{
			return App.Settings.Prop.Brightness;
		}
		set
		{
			double num = Math.Clamp(value, 0.0, 100.0);
			if (App.Settings.Prop.Brightness != num)
			{
				App.Settings.Prop.Brightness = num;
				OnPropertyChanged("Brightness");
				OnPropertyChanged("BrightnessDisplay");
			}
		}
	}

	public bool ServerDetailsDisplay
	{
		get
		{
			return App.Settings.Prop.ShowServerDetailsUI;
		}
		set
		{
			App.Settings.Prop.ShowServerDetailsUI = value;
		}
	}

	public bool Crosshair
	{
		get
		{
			return App.Settings.Prop.Crosshair;
		}
		set
		{
			App.Settings.Prop.Crosshair = value;
		}
	}

	public System.Collections.Generic.IReadOnlyList<Voidstrap.Utility.ClockDisplay.ZoneOption> ClockTimeZones => Voidstrap.Utility.ClockDisplay.Options;

	public string ClockTimeZoneId
	{
		get
		{
			return App.Settings.Prop.ClockTimeZoneId ?? "";
		}
		set
		{
			App.Settings.Prop.ClockTimeZoneId = value ?? "";
			Voidstrap.Utility.ClockDisplay.Invalidate();
		}
	}

	public bool Clock24Hour
	{
		get
		{
			return App.Settings.Prop.Clock24Hour;
		}
		set
		{
			App.Settings.Prop.Clock24Hour = value;
		}
	}

	public bool CurrentTimeDisplay
	{
		get
		{
			return App.Settings.Prop.CurrentTimeDisplay;
		}
		set
		{
			if (App.Settings.Prop.CurrentTimeDisplay == value)
				return;
			App.Settings.Prop.CurrentTimeDisplay = value;
			OnPropertyChanged("CurrentTimeDisplay");
		}
	}

	public FontModPresetTask TextFontTask { get; } = new FontModPresetTask();

	public Visibility ChooseCustomCursorVisibility
	{
		get
		{
			InlineArray5<string> buffer = default(InlineArray5<string>);
			buffer[0] = Paths.Mods;
			buffer[1] = "Content";
			buffer[2] = "textures";
			buffer[3] = "Cursors";
			buffer[4] = "KeyboardMouse";
			return GetVisibility(Path.Combine(buffer), new string[3] { "ArrowCursor.png", "ArrowFarCursor.png", "MouseLockedCursor.png" }, checkExist: false);
		}
	}

	public Visibility DeleteCustomCursorVisibility
	{
		get
		{
			InlineArray5<string> buffer = default(InlineArray5<string>);
			buffer[0] = Paths.Mods;
			buffer[1] = "Content";
			buffer[2] = "textures";
			buffer[3] = "Cursors";
			buffer[4] = "KeyboardMouse";
			return GetVisibility(Path.Combine(buffer), new string[3] { "ArrowCursor.png", "ArrowFarCursor.png", "MouseLockedCursor.png" }, checkExist: true);
		}
	}

	public Visibility ChooseCustomShiftlockVisibility => GetVisibility(Path.Combine(Paths.Mods, "Content", "textures"), new string[1] { "MouseLockedCursor.png" }, checkExist: false);

	public Visibility DeleteCustomShiftlockVisibility => GetVisibility(Path.Combine(Paths.Mods, "Content", "textures"), new string[1] { "MouseLockedCursor.png" }, checkExist: true);

	public Visibility ChooseCustomDeathSoundVisibility => GetVisibility(Path.Combine(Paths.Mods, "Content", "sounds"), new string[1] { "oof.ogg" }, checkExist: false);

	public Visibility DeleteCustomDeathSoundVisibility => GetVisibility(Path.Combine(Paths.Mods, "Content", "sounds"), new string[1] { "oof.ogg" }, checkExist: true);

	public ObservableCollection<GradientStopViewModel> GradientStops { get; set; } = new ObservableCollection<GradientStopViewModel>();

	public ObservableCollection<CustomCursorSet> CustomCursorSets { get; } = new ObservableCollection<CustomCursorSet>();

	public int SelectedCustomCursorSetIndex
	{
		get
		{
			return _selectedCustomCursorSetIndex;
		}
		set
		{
			if (_selectedCustomCursorSetIndex != value)
			{
				_selectedCustomCursorSetIndex = value;
				OnPropertyChanged("SelectedCustomCursorSetIndex");
				OnPropertyChanged("SelectedCustomCursorSet");
				OnPropertyChanged("IsCustomCursorSetSelected");
				SelectedCustomCursorSetName = SelectedCustomCursorSet?.Name ?? "";
				SelectedCustomCursorSetIndex = value;
				NotifyCursorVisibilities();
				LoadCursorPathsForSelectedSet();
			}
		}
	}

	public CustomCursorSet? SelectedCustomCursorSet
	{
		get
		{
			if (SelectedCustomCursorSetIndex < 0 || SelectedCustomCursorSetIndex >= CustomCursorSets.Count)
			{
				return null;
			}
			return CustomCursorSets[SelectedCustomCursorSetIndex];
		}
	}

	public bool IsCustomCursorSetSelected => SelectedCustomCursorSet != null;

	public string SelectedCustomCursorSetName
	{
		get
		{
			return _selectedCustomCursorSetName;
		}
		set
		{
			if (_selectedCustomCursorSetName != value)
			{
				_selectedCustomCursorSetName = value;
				OnPropertyChanged("SelectedCustomCursorSetName");
			}
		}
	}

	public ICommand AddCustomCursorSetCommand => new RelayCommand(AddCustomCursorSet);

	public ICommand DeleteCustomCursorSetCommand => new RelayCommand(DeleteCustomCursorSet);

	public ICommand RenameCustomCursorSetCommand => new RelayCommand(RenameCustomCursorSet);

	public ICommand ApplyCursorSetCommand => new RelayCommand(ApplyCursorSet);

	public ICommand GetCurrentCursorSetCommand => new RelayCommand(GetCurrentCursorSet);

	public ICommand ExportCursorSetCommand => new RelayCommand(ExportCursorSet);

	public ICommand ImportCursorSetCommand => new RelayCommand(ImportCursorSet);

	public ICommand AddArrowCursorCommand => new RelayCommand(delegate
	{
		AddCursorImage("ArrowCursor.png", "Select Arrow Cursor PNG");
	});

	public ICommand AddArrowFarCursorCommand => new RelayCommand(delegate
	{
		AddCursorImage("ArrowFarCursor.png", "Select Arrow Far Cursor PNG");
	});

	public ICommand AddIBeamCursorCommand => new RelayCommand(delegate
	{
		AddCursorImage("IBeamCursor.png", "Select IBeam Cursor PNG");
	});

	public ICommand AddShiftlockCursorCommand => new RelayCommand(AddShiftlockCursor);

	public ICommand DeleteArrowCursorCommand => new RelayCommand(delegate
	{
		DeleteCursorImage("ArrowCursor.png");
	});

	public ICommand DeleteArrowFarCursorCommand => new RelayCommand(delegate
	{
		DeleteCursorImage("ArrowFarCursor.png");
	});

	public ICommand DeleteIBeamCursorCommand => new RelayCommand(delegate
	{
		DeleteCursorImage("IBeamCursor.png");
	});

	public ICommand DeleteShiftlockCursorCommand => new RelayCommand(delegate
	{
		DeleteCursorImage("MouseLockedCursor.png");
	});

	public RelayCommand DownloadCurCommand { get; }

	public RelayCommand DownloadPngCommand { get; }

	public CrosshairShape[] CrosshairShapes { get; } = new CrosshairShape[4]
	{
		CrosshairShape.Cross,
		CrosshairShape.Dot,
		CrosshairShape.Circle,
		CrosshairShape.Image
	};

	public CrosshairShape SelectedShape
	{
		get
		{
			return _selectedShape;
		}
		set
		{
			if (SetProperty(ref _selectedShape, value, "SelectedShape"))
			{
				UseImageCrosshair = value == CrosshairShape.Image;
				SaveIni();
				UpdatePreview();
			}
		}
	}

	public bool UseImageCrosshair
	{
		get
		{
			return _useImageCrosshair;
		}
		set
		{
			if (SetProperty(ref _useImageCrosshair, value, "UseImageCrosshair"))
			{
				SaveIni();
				UpdatePreview();
			}
		}
	}

	public string ImageUrl
	{
		get
		{
			return _imageUrl;
		}
		set
		{
			if (SetProperty(ref _imageUrl, value, "ImageUrl"))
			{
				SaveIni();
				UpdatePreview();
			}
		}
	}

	public string CursorColorHex
	{
		get
		{
			return _cursorColorHex;
		}
		set
		{
			SetProperty(ref _cursorColorHex, value, "CursorColorHex");
			SaveIni();
			UpdatePreview();
		}
	}

	public string CursorOutlineColorHex
	{
		get
		{
			return _cursorOutlineColorHex;
		}
		set
		{
			SetProperty(ref _cursorOutlineColorHex, value, "CursorOutlineColorHex");
			SaveIni();
			UpdatePreview();
		}
	}

	public int CursorSize
	{
		get
		{
			return _cursorSize;
		}
		set
		{
			SetProperty(ref _cursorSize, value, "CursorSize");
			SaveIni();
			UpdatePreview();
		}
	}

	public int CrosshairThickness
	{
		get
		{
			return _crosshairThickness;
		}
		set
		{
			SetProperty(ref _crosshairThickness, value, "CrosshairThickness");
			SaveIni();
			UpdatePreview();
		}
	}

	public int Gap
	{
		get
		{
			return _gap;
		}
		set
		{
			SetProperty(ref _gap, value, "Gap");
			SaveIni();
			UpdatePreview();
		}
	}

	public double CursorOpacity
	{
		get
		{
			return _cursorOpacity;
		}
		set
		{
			SetProperty(ref _cursorOpacity, value, "CursorOpacity");
			SaveIni();
			UpdatePreview();
		}
	}

	public string CursorCode
	{
		get
		{
			return _cursorCode;
		}
		set
		{
			SetProperty(ref _cursorCode, value, "CursorCode");
		}
	}

	public ImageSource CursorPreview
	{
		get
		{
			return _cursorPreview;
		}
		set
		{
			SetProperty(ref _cursorPreview, value, "CursorPreview");
		}
	}

	public string ShiftlockCursorSelectedPath
	{
		get
		{
			return _shiftlockCursorSelectedPath;
		}
		set
		{
			if (_shiftlockCursorSelectedPath != value)
			{
				_shiftlockCursorSelectedPath = value;
				OnPropertyChanged("ShiftlockCursorSelectedPath");
			}
		}
	}

	public string ArrowCursorSelectedPath
	{
		get
		{
			return _arrowCursorSelectedPath;
		}
		set
		{
			if (_arrowCursorSelectedPath != value)
			{
				_arrowCursorSelectedPath = value;
				OnPropertyChanged("ArrowCursorSelectedPath");
			}
		}
	}

	public string ArrowFarCursorSelectedPath
	{
		get
		{
			return _arrowFarCursorSelectedPath;
		}
		set
		{
			if (_arrowFarCursorSelectedPath != value)
			{
				_arrowFarCursorSelectedPath = value;
				OnPropertyChanged("ArrowFarCursorSelectedPath");
			}
		}
	}

	public string IBeamCursorSelectedPath
	{
		get
		{
			return _iBeamCursorSelectedPath;
		}
		set
		{
			if (_iBeamCursorSelectedPath != value)
			{
				_iBeamCursorSelectedPath = value;
				OnPropertyChanged("IBeamCursorSelectedPath");
			}
		}
	}

	public ImageSource? ShiftlockCursorPreview
	{
		get
		{
			return _shiftlockCursorPreview;
		}
		set
		{
			_shiftlockCursorPreview = value;
			OnPropertyChanged("ShiftlockCursorPreview");
		}
	}

	public ImageSource? ArrowCursorPreview
	{
		get
		{
			return _arrowCursorPreview;
		}
		set
		{
			_arrowCursorPreview = value;
			OnPropertyChanged("ArrowCursorPreview");
		}
	}

	public ImageSource? ArrowFarCursorPreview
	{
		get
		{
			return _arrowFarCursorPreview;
		}
		set
		{
			_arrowFarCursorPreview = value;
			OnPropertyChanged("ArrowFarCursorPreview");
		}
	}

	public ImageSource? IBeamCursorPreview
	{
		get
		{
			return _iBeamCursorPreview;
		}
		set
		{
			_iBeamCursorPreview = value;
			OnPropertyChanged("IBeamCursorPreview");
		}
	}

	public Visibility AddShiftlockCursorVisibility => GetCursorAddVisibility("MouseLockedCursor.png");

	public Visibility DeleteShiftlockCursorVisibility => GetCursorDeleteVisibility("MouseLockedCursor.png");

	public Visibility AddArrowCursorVisibility => GetCursorAddVisibility("ArrowCursor.png");

	public Visibility DeleteArrowCursorVisibility => GetCursorDeleteVisibility("ArrowCursor.png");

	public Visibility AddArrowFarCursorVisibility => GetCursorAddVisibility("ArrowFarCursor.png");

	public Visibility DeleteArrowFarCursorVisibility => GetCursorDeleteVisibility("ArrowFarCursor.png");

	public Visibility AddIBeamCursorVisibility => GetCursorAddVisibility("IBeamCursor.png");

	public Visibility DeleteIBeamCursorVisibility => GetCursorDeleteVisibility("IBeamCursor.png");

	public bool ModExplorerVisible
	{
		get
		{
			return _modExplorerVisible;
		}
		set
		{
			_modExplorerVisible = value;
			OnPropertyChanged("ModExplorerVisible");
			OnPropertyChanged("MainContentVisibility");
		}
	}

	public Visibility MainContentVisibility
	{
		get
		{
			if (!ModExplorerVisible)
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public ObservableCollection<ModFile> ModFiles { get; } = new ObservableCollection<ModFile>();

	public ModFile? SelectedModFile
	{
		get
		{
			return _selectedModFile;
		}
		set
		{
			_selectedModFile = value;
			OnPropertyChanged("SelectedModFile");
		}
	}

	public string CurrentExplorerPath
	{
		get
		{
			if (string.IsNullOrEmpty(_currentExplorerPath))
			{
				_currentExplorerPath = ResolveRobloxPlayerDir();
			}
			return _currentExplorerPath;
		}
		set
		{
			if (!IsSafeExplorerPath(value))
			{
				return;
			}
			_currentExplorerPath = value;
			OnPropertyChanged("CurrentExplorerPath");
			OnPropertyChanged("ExplorerPathDisplay");
		}
	}

	public string ExplorerPathDisplay
	{
		get
		{
			string robloxPlayerDir = ResolveRobloxPlayerDir();
			try
			{
				return Path.GetRelativePath(robloxPlayerDir, CurrentExplorerPath);
			}
			catch
			{
				return CurrentExplorerPath;
			}
		}
	}

	public ICommand ToggleModExplorerCommand => new RelayCommand(delegate
	{
		ModExplorerVisible = !ModExplorerVisible;
		if (ModExplorerVisible)
		{
			_currentExplorerPath = ResolveRobloxPlayerDir(forceRefresh: true);
			RefreshModFiles();
		}
	});

	public ICommand RefreshModFilesCommand => new RelayCommand(RefreshModFiles);

	public ICommand OpenModFileFolderCommand => new RelayCommand(OpenModFileFolder);

	public ICommand DeleteModFileCommand => new RelayCommand(DeleteModFile);

	public ICommand ShowFileDetailsCommand => new RelayCommand(ShowFileDetails);

	public ICommand ReplaceFileCommand => new RelayCommand(ReplaceFile);

	public ICommand RecolorImageCommand => new RelayCommand(RecolorImage);

	public ICommand AdjustImageCommand => new RelayCommand(AdjustImage);

	public ICommand ExportFileCommand => new RelayCommand(ExportFile);

	public ICommand GoBackCommand => new RelayCommand(delegate
	{
		string text = ResolveRobloxPlayerDir().TrimEnd(new char[2] { '\\', '/' });
		string text2 = CurrentExplorerPath.TrimEnd(new char[2] { '\\', '/' });
		if (text2.Equals(text, StringComparison.OrdinalIgnoreCase))
		{
			ModExplorerVisible = false;
		}
		else
		{
			CurrentExplorerPath = Path.GetDirectoryName(text2) ?? text;
			RefreshModFiles();
		}
	});

	public string ExplorerSearchText
	{
		get
		{
			return _explorerSearchText;
		}
		set
		{
			_explorerSearchText = value;
			OnPropertyChanged("ExplorerSearchText");
			RefreshModFiles();
		}
	}

	public bool DisableAllTextures
	{
		get
		{
			return App.Settings.Prop.AssetWarpDisableAllTextures;
		}
		set
		{
			if (App.Settings.Prop.AssetWarpDisableAllTextures != value)
			{
				App.Settings.Prop.AssetWarpDisableAllTextures = value;
				AssetProxyServer.ReconcileRuntimeState();
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
				OnPropertyChanged(nameof(DisableAllTextures));
			}
		}
	}

	public ObservableCollection<CapturedAsset> CapturedAssets { get; } = new ObservableCollection<CapturedAsset>();

	public string[] CacheFilters { get; } = new string[8] { "All", "Image", "Audio", "Texture", "Mesh", "Model", "Data", "Other" };

	public string CacheFilter
	{
		get
		{
			return _cacheFilter;
		}
		set
		{
			_cacheFilter = value ?? "All";
			OnPropertyChanged("CacheFilter");
			RebuildCaptures();
		}
	}

	public string CaptureSearch
	{
		get
		{
			return _captureSearch;
		}
		set
		{
			_captureSearch = value ?? "";
			OnPropertyChanged("CaptureSearch");
			RebuildCaptures();
		}
	}

	public bool ShowAssetNames
	{
		get
		{
			return _showAssetNames;
		}
		set
		{
			_showAssetNames = value;
			CapturedAsset.ShowNames = value;
			OnPropertyChanged("ShowAssetNames");
			foreach (CapturedAsset capturedAsset in CapturedAssets)
			{
				capturedAsset.RaiseLabelChanged();
			}
			if (value)
			{
				ResolveNamesAsync();
			}
		}
	}

	public string CaptureStatsText
	{
		get
		{
			return _captureStatsText;
		}
		set
		{
			_captureStatsText = value;
			OnPropertyChanged("CaptureStatsText");
		}
	}

	public string AssetWarpStatus
{
	get
	{
		return _assetWarpStatus;
	}
	set
	{
		_assetWarpStatus = value;
		OnPropertyChanged(nameof(AssetWarpStatus));
	}
}

	public ICommand RefreshCapturesCommand => new RelayCommand(delegate
	{
		_attemptedNames.Clear();
		_nameCooldownUntil = DateTime.MinValue;
		AssetCaptureStore.ResetResolveState();
		ScanCache();
		RebuildCaptures();
	});

	public ICommand ClearCapturesCommand => new RelayCommand(delegate
	{
		AssetCaptureStore.Clear();
		CapturedAssets.Clear();
		UpdateCaptureStats();
	});

	public ICommand DeleteAllCommand => new RelayCommand(delegate
	{
		_attemptedNames.Clear();
		_nameCooldownUntil = DateTime.MinValue;
		AssetCaptureStore.FullReset();
		CapturedAssets.Clear();
		UpdateCaptureStats();
	});

	public ICommand OpenCacheFolderCommand => new RelayCommand(delegate
	{
		OpenFolder(AssetCaptureStore.CacheDir);
	});

	public ICommand OpenExportFolderCommand => new RelayCommand(delegate
	{
		OpenFolder(ExportDir);
	});

	public ICommand CopyCapturedIdCommand => new RelayCommand<CapturedAsset>(CopyCapturedId);

	public ICommand ExportScrapedCommand => new RelayCommand<CapturedAsset>(ExportScraped);

	private static string ExportDir => Paths.AssetExport;

	private void OpenModsFolder()
	{
		Process.Start("explorer.exe", Paths.Mods);
	}

	private void ManageCustomFont()
	{
		if (!string.IsNullOrEmpty(TextFontTask.NewState))
			RemoveCustomFont();
		else
			_ = ChooseLocalFontAsync();
	}

	private async Task ChooseLocalFontAsync()
	{
		string sourcePath;
		try
		{
			Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
			{
				Filter = Strings.Menu_FontFiles + "|*.ttf;*.otf"
			};
			if (openFileDialog.ShowDialog() != true)
				return;
			sourcePath = openFileDialog.FileName;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ChooseCustomFontDialog", ex);
			Frontend.ShowMessageBox("The font picker could not be opened.", MessageBoxImage.Hand);
			return;
		}
		CancellationTokenSource cancellation = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _fontManagerCts, cancellation);
		previous?.Cancel();
		previous?.Dispose();
		FontManagerBusy = true;
		try
		{
			string importedPath = await GoogleFontsService.ImportLocalAsync(sourcePath, cancellation.Token);
			if (!ReferenceEquals(_fontManagerCts, cancellation))
				return;
			if (!TryCreatePreviewFontFamily(importedPath, out _))
			{
				Frontend.ShowMessageBox("That font could not be loaded safely.", MessageBoxImage.Hand);
				return;
			}
			App.Settings.Prop.CustomFontLocation = string.Empty;
			SelectedGoogleFont = null;
			TextFontTask.NewState = importedPath;
			FontManagerStatus = "Local font selected. Save settings to apply it.";
			NotifyCustomFontChanged();
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ChooseCustomFont", ex);
			Frontend.ShowMessageBox("That font could not be imported safely.", MessageBoxImage.Hand);
		}
		finally
		{
			if (Interlocked.CompareExchange(ref _fontManagerCts, null, cancellation) == cancellation)
				FontManagerBusy = false;
			cancellation.Dispose();
		}
	}

	private void RemoveCustomFont()
	{
		App.Settings.Prop.CustomFontLocation = string.Empty;
		TextFontTask.NewState = string.Empty;
		FontManagerStatus = "Roblox default selected. Save settings to finish removal.";
		NotifyCustomFontChanged();
	}

	private async Task ApplyGoogleFontAsync()
	{
		GoogleFontOption? selected = SelectedGoogleFont;
		if (selected == null)
			return;
		CancellationTokenSource cancellation = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _fontManagerCts, cancellation);
		previous?.Cancel();
		previous?.Dispose();
		FontManagerBusy = true;
		FontManagerStatus = "Downloading " + selected.Family + "...";
		try
		{
			string path = await GoogleFontsService.DownloadAsync(selected, cancellation.Token);
			if (!ReferenceEquals(_fontManagerCts, cancellation))
				return;
			App.Settings.Prop.CustomFontLocation = selected.Family;
			TextFontTask.NewState = path;
			FontManagerStatus = selected.Family + " selected. Save settings to apply it.";
			NotifyCustomFontChanged();
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ApplyGoogleFont", ex);
			FontManagerStatus = "Could not prepare this font. Try again.";
		}
		finally
		{
			if (Interlocked.CompareExchange(ref _fontManagerCts, null, cancellation) == cancellation)
				FontManagerBusy = false;
			cancellation.Dispose();
		}
	}

	private async Task LoadGoogleFontsAsync(bool force = false)
	{
		CancellationTokenSource cancellation = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _fontManagerCts, cancellation);
		previous?.Cancel();
		previous?.Dispose();
		FontManagerBusy = true;
		FontManagerStatus = "Loading the font catalog...";
		try
		{
			IReadOnlyList<GoogleFontOption> fonts = await GoogleFontsService.LoadCatalogAsync(force, cancellation.Token);
			if (!ReferenceEquals(_fontManagerCts, cancellation))
				return;
			AvailableGoogleFonts = new ObservableCollection<GoogleFontOption>(fonts);
			OnPropertyChanged(nameof(AvailableGoogleFonts));
			string saved = App.Settings.Prop.CustomFontLocation;
			GoogleFontOption? savedFont = string.IsNullOrWhiteSpace(saved)
				? null
				: AvailableGoogleFonts.FirstOrDefault(font => font.Family.Equals(saved, StringComparison.OrdinalIgnoreCase));
			SelectedGoogleFont = savedFont ?? (HasCustomFont
				? null
				: AvailableGoogleFonts.FirstOrDefault(font => font.Family.Equals("Roboto", StringComparison.OrdinalIgnoreCase)) ?? AvailableGoogleFonts.FirstOrDefault());
			bool starter = AvailableGoogleFonts.Count > 0 && AvailableGoogleFonts.All(font => font.Category == "starter");
			FontManagerStatus = starter ? "Starter fonts ready. Refresh to load the full catalog." : AvailableGoogleFonts.Count.ToString("N0") + " fonts ready. Type a name to search.";
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::LoadGoogleFonts", ex);
			FontManagerStatus = "Could not load fonts. Select refresh to try again.";
		}
		finally
		{
			if (Interlocked.CompareExchange(ref _fontManagerCts, null, cancellation) == cancellation)
				FontManagerBusy = false;
			cancellation.Dispose();
		}
	}

	private void NotifyCustomFontChanged()
	{
		_activePreviewFontPath = string.Empty;
		_activePreviewFontFamily = new System.Windows.Media.FontFamily("Segoe UI");
		OnPropertyChanged("ChooseCustomFontVisibility");
		OnPropertyChanged("DeleteCustomFontVisibility");
		OnPropertyChanged("DeleteCustomFontFontName");
		OnPropertyChanged("DeleteCustomFontFontFamily");
		OnPropertyChanged(nameof(FontPreviewFontFamily));
		OnPropertyChanged(nameof(FontPreviewVisible));
		OnPropertyChanged(nameof(HasCustomFont));
		OnPropertyChanged(nameof(ActiveFontName));
	}

	public async Task LoadModsAsync()
	{
		try
		{
			string? json = await GitHubCache.GetStringAsync("https://api.github.com/repos/KloBraticc/ModsHub-Reworked-/contents", TimeSpan.FromHours(1L));
			if (json == null)
			{
				return;
			}
			List<GitHubContent>? list = JsonSerializer.Deserialize<List<GitHubContent>>(json);
			if (list == null)
			{
				return;
			}
			Task<ModInfo>[] tasks = list
				.Where(item => item.Type == "dir")
				.Select(async item => new ModInfo
				{
					Name = item.Name,
					FolderPath = item.Path,
					ImageUrl = await GetPreviewImageUrl(item.Path, _http).ConfigureAwait(false)
				})
				.ToArray();
			AvailableMods = new ObservableCollection<ModInfo>(await Task.WhenAll(tasks).ConfigureAwait(true));
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::LoadModsAsync", ex);
		}
	}

	private async Task<string> GetPreviewImageUrl(string folder, HttpClient http)
	{
		await PreviewProbeGate.WaitAsync().ConfigureAwait(false);
		try
		{
			string[] array = new string[3] { "png", "jpg", "jpeg" };
			foreach (string text in array)
			{
				string rawUrl = "https://raw.githubusercontent.com/KloBraticc/ModsHub-Reworked-/main/" + folder + "/Preview." + text;
				using var request = new HttpRequestMessage(HttpMethod.Head, rawUrl);
				using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
					return rawUrl;
			}
			return null;
		}
		finally
		{
			PreviewProbeGate.Release();
		}
	}

	public async Task LoadSkyboxPacksFromGithub(bool force = false)
	{
		CancellationTokenSource cancellation = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _skyboxManagerCts, cancellation);
		previous?.Cancel();
		previous?.Dispose();
		SkyboxManagerBusy = true;
		SkyboxManagerStatus = force ? "Refreshing skyboxes..." : "Loading skyboxes...";
		bool online = false;
		List<string> names = [];
		try
		{
			using HttpResponseMessage response = await _http.GetAsync(RepoRoot, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > 2097152))
				throw new InvalidDataException("The skybox catalog size is invalid");
			byte[] data = await Voidstrap.Utility.Http.ReadBytesBoundedAsync(response.Content, 2097152, cancellation.Token).ConfigureAwait(false);
			if (data.Length == 0 || data.Length > 2097152)
				throw new InvalidDataException("The skybox catalog size is invalid");
			JsonElement[] entries = JsonSerializer.Deserialize<JsonElement[]>(data, JsonOptions.Tolerant) ?? [];
			names = entries
				.Where(entry => entry.TryGetProperty("type", out JsonElement type) && type.GetString() == "dir")
				.Select(entry => entry.TryGetProperty("name", out JsonElement name) ? name.GetString() : null)
				.Where(name => IsSafeSkyboxName(name))
				.Select(name => name!)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(name => name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
				.ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
				.ToList();
			online = names.Count > 0;
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
			return;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModsViewModel::LoadSkyboxes", "Skybox catalog unavailable: " + ex.Message);
			try
			{
				if (Directory.Exists(Paths.SkyboxPack))
					names = Directory.EnumerateDirectories(Paths.SkyboxPack).Select(Path.GetFileName).Where(IsSafeSkyboxName).Select(name => name!).ToList();
			}
			catch (Exception localEx)
			{
				App.Logger.WriteLine("ModsViewModel::LoadSkyboxes", "Could not read saved skyboxes: " + localEx.Message);
			}
		}
		finally
		{
			if (ReferenceEquals(_skyboxManagerCts, cancellation))
			{
				if (SkyboxImageConverter.HasCustomPack() && !names.Contains(SkyboxImageConverter.CustomPackName, StringComparer.OrdinalIgnoreCase))
				{
					int customIndex = names.FindIndex(name => name.Equals("Default", StringComparison.OrdinalIgnoreCase));
					names.Insert(customIndex >= 0 ? customIndex + 1 : 0, SkyboxImageConverter.CustomPackName);
				}
				if (!online)
				{
					string saved = App.Settings.Prop.SkyboxName;
					if (IsSafeSkyboxName(saved) && !names.Contains(saved, StringComparer.OrdinalIgnoreCase))
						names.Add(saved);
					if (names.Count == 0)
						names.Add("Default");
				}
				List<string> resolved = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
				RunOnDispatcher(() => ApplySkyboxPacks(resolved, online));
				Interlocked.CompareExchange(ref _skyboxManagerCts, null, cancellation);
			}
			cancellation.Dispose();
		}
	}

	private void ApplySkyboxPacks(List<string> names, bool online)
	{
		AvailableSkyboxPacks.Clear();
		foreach (string name in names)
			AvailableSkyboxPacks.Add(new SkyboxPack { Name = name });
		SelectedSkyboxPack = AvailableSkyboxPacks.FirstOrDefault(pack => pack.Name.Equals(App.Settings.Prop.SkyboxName, StringComparison.OrdinalIgnoreCase)) ?? AvailableSkyboxPacks.FirstOrDefault();
		SkyboxManagerStatus = online ? AvailableSkyboxPacks.Count.ToString("N0") + " skyboxes ready. The selected pack downloads when Roblox starts." : "Saved choices are ready. Refresh to check for every skybox.";
		SkyboxManagerBusy = false;
		OnPropertyChanged(nameof(SkyboxManagerReady));
	}

	private static void RunOnDispatcher(Action action)
	{
		Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
		if (dispatcher is null || dispatcher.CheckAccess() || dispatcher.HasShutdownStarted)
		{
			action();
			return;
		}
		dispatcher.Invoke(action);
	}

	private static bool IsSafeSkyboxName(string? name)
	{
		return !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && name != "." && name != ".." && name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
	}

	private void ChooseSkyboxFace(string? face)
	{
		if (CustomSkyboxBusy || string.IsNullOrWhiteSpace(face))
			return;
		string? source = ShowSkyboxImagePicker("Choose the " + face.ToLowerInvariant() + " skybox face");
		if (source == null)
			return;
		SetSkyboxFace(face, source);
	}

	private void ChooseSingleSkyboxImage()
	{
		if (CustomSkyboxBusy)
			return;
		string? source = ShowSkyboxImagePicker("Choose one image for every skybox face");
		if (source == null)
			return;
		_customSkyboxBack = source;
		_customSkyboxDown = source;
		_customSkyboxFront = source;
		_customSkyboxLeft = source;
		_customSkyboxRight = source;
		_customSkyboxUp = source;
		NotifyCustomSkyboxSelectionChanged();
	}

	private static string? ShowSkyboxImagePicker(string title)
	{
		try
		{
			Microsoft.Win32.OpenFileDialog dialog = new()
			{
				Title = title,
				CheckFileExists = true,
				Multiselect = false,
				Filter = "Image files|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.tif;*.tiff;*.tga;*.pbm;*.pgm;*.ppm;*.qoi;*.ico;*.heic;*.heif;*.avif;*.jfif;*.dds;*.tex|All files|*.*"
			};
			return dialog.ShowDialog() == true ? dialog.FileName : null;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::SkyboxPicker", ex);
			Frontend.ShowMessageBox("The image picker could not be opened.", MessageBoxImage.Hand);
			return null;
		}
	}

	private void SetSkyboxFace(string face, string source)
	{
		switch (face)
		{
			case "Back":
				_customSkyboxBack = source;
				break;
			case "Down":
				_customSkyboxDown = source;
				break;
			case "Front":
				_customSkyboxFront = source;
				break;
			case "Left":
				_customSkyboxLeft = source;
				break;
			case "Right":
				_customSkyboxRight = source;
				break;
			case "Up":
				_customSkyboxUp = source;
				break;
			default:
				return;
		}
		NotifyCustomSkyboxSelectionChanged();
	}

	private IReadOnlyDictionary<string, string> GetCustomSkyboxSources()
	{
		return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["sky512_bk.tex"] = _customSkyboxBack,
			["sky512_dn.tex"] = _customSkyboxDown,
			["sky512_ft.tex"] = _customSkyboxFront,
			["sky512_lf.tex"] = _customSkyboxLeft,
			["sky512_rt.tex"] = _customSkyboxRight,
			["sky512_up.tex"] = _customSkyboxUp
		};
	}

	private async Task ApplyCustomSkyboxAsync()
	{
		if (!CustomSkyboxCanApply)
		{
			Frontend.ShowMessageBox("Choose all six faces or use one image for every side first.", MessageBoxImage.Information);
			return;
		}
		CancellationTokenSource cancellation = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _skyboxImportCts, cancellation);
		previous?.Cancel();
		previous?.Dispose();
		CustomSkyboxBusy = true;
		try
		{
			await SkyboxImageConverter.ImportAsync(GetCustomSkyboxSources(), cancellation.Token);
			if (!ReferenceEquals(_skyboxImportCts, cancellation))
				return;
			SkyboxPack? customPack = AvailableSkyboxPacks.FirstOrDefault(pack => pack.Name.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase));
			if (customPack == null)
			{
				customPack = new SkyboxPack { Name = SkyboxImageConverter.CustomPackName };
				int defaultIndex = AvailableSkyboxPacks.ToList().FindIndex(pack => pack.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
				AvailableSkyboxPacks.Insert(defaultIndex >= 0 ? defaultIndex + 1 : 0, customPack);
			}
			SelectedSkyboxPack = customPack;
			SkyboxEnabled = true;
			App.Settings.SaveDeferred();
			App.Logger.WriteLine("ModsViewModel::ApplyCustomSkybox", "Custom skybox saved: " + SkyboxImageConverter.CustomPackDirectory);
			OnPropertyChanged(nameof(HasCustomSkybox));
			OnPropertyChanged(nameof(SkyboxManagerReady));
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ApplyCustomSkybox", ex);
			Frontend.ShowMessageBox("The custom skybox could not be saved:\n" + ex.Message, MessageBoxImage.Hand);
		}
		finally
		{
			if (ReferenceEquals(_skyboxImportCts, cancellation))
			{
				Interlocked.CompareExchange(ref _skyboxImportCts, null, cancellation);
				CustomSkyboxBusy = false;
			}
			cancellation.Dispose();
		}
	}

	private void RemoveCustomSkybox()
	{
		if (CustomSkyboxBusy || !HasCustomSkybox)
			return;
		if (Frontend.ShowMessageBox("Remove your saved custom skybox?", MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
			return;
		try
		{
			SkyboxImageConverter.Remove();
			SkyboxPack? customPack = AvailableSkyboxPacks.FirstOrDefault(pack => pack.Name.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase));
			if (customPack != null)
				AvailableSkyboxPacks.Remove(customPack);
			if (App.Settings.Prop.SkyboxName.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase))
				SelectedSkyboxPack = AvailableSkyboxPacks.FirstOrDefault(pack => pack.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) ?? AvailableSkyboxPacks.FirstOrDefault();
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(HasCustomSkybox));
			OnPropertyChanged(nameof(SkyboxManagerReady));
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::RemoveCustomSkybox", ex);
			Frontend.ShowMessageBox("The custom skybox could not be removed:\n" + ex.Message, MessageBoxImage.Hand);
		}
	}

	private static string GetSkyboxFaceDisplayName(string path)
	{
		return string.IsNullOrWhiteSpace(path) ? "Choose image" : Path.GetFileName(path);
	}

	private void NotifyCustomSkyboxSelectionChanged()
	{
		OnPropertyChanged(nameof(CustomSkyboxBack));
		OnPropertyChanged(nameof(CustomSkyboxDown));
		OnPropertyChanged(nameof(CustomSkyboxFront));
		OnPropertyChanged(nameof(CustomSkyboxLeft));
		OnPropertyChanged(nameof(CustomSkyboxRight));
		OnPropertyChanged(nameof(CustomSkyboxUp));
		OnPropertyChanged(nameof(CustomSkyboxCanApply));
	}

	private void OpenCompatSettings()
	{
		string executablePath = new RobloxPlayerData().ExecutablePath;
		if (File.Exists(executablePath))
		{
			Windows.Win32.PInvoke.SHObjectProperties(HWND.Null, SHOP_TYPE.SHOP_FILEPATH, executablePath, "Compatibility");
		}
		else
		{
			Frontend.ShowMessageBox(Strings.Common_RobloxNotInstalled, MessageBoxImage.Hand);
		}
	}

	private Visibility GetVisibility(string directory, string[] filenames, bool checkExist)
	{
		bool flag = filenames.Any((string name) => File.Exists(Path.Combine(directory, name)));
		if (!(checkExist ? flag : (!flag)))
		{
			return Visibility.Collapsed;
		}
		return Visibility.Visible;
	}

	private void AddCustomFile(string[] targetFiles, string targetDir, string dialogTitle, string filter, string failureText, Action postAction = null)
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = filter,
			Title = dialogTitle
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		string fileName = openFileDialog.FileName;
		Directory.CreateDirectory(targetDir);
		try
		{
			foreach (string path in targetFiles)
			{
				string destFileName = Path.Combine(targetDir, path);
				Filesystem.CopyWritableFile(fileName, destFileName);
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to add " + failureText + ":\n" + ex.Message, MessageBoxImage.Hand);
			return;
		}
		postAction?.Invoke();
	}

	private void RemoveCustomFile(string[] targetFiles, string targetDir, string notFoundMessage, Action postAction = null)
	{
		bool flag = false;
		foreach (string text in targetFiles)
		{
			string path = Path.Combine(targetDir, text);
			if (File.Exists(path))
			{
				try
				{
					Filesystem.DeleteWritableFile(path);
					flag = true;
				}
				catch (Exception ex)
				{
					Frontend.ShowMessageBox("Failed to remove " + text + ":\n" + ex.Message, MessageBoxImage.Hand);
				}
			}
		}
		if (!flag)
		{
			Frontend.ShowMessageBox(notFoundMessage, MessageBoxImage.Asterisk);
		}
		postAction?.Invoke();
	}

	public void AddCustomCursorMod()
	{
		string[] targetFiles = new string[3] { "ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png" };
		InlineArray5<string> buffer = default(InlineArray5<string>);
		buffer[0] = Paths.Mods;
		buffer[1] = "Content";
		buffer[2] = "textures";
		buffer[3] = "Cursors";
		buffer[4] = "KeyboardMouse";
		AddCustomFile(targetFiles, Path.Combine(buffer), "Select a PNG Cursor Image", "PNG Images (*.png)|*.png", "cursors", delegate
		{
			OnPropertyChanged("ChooseCustomCursorVisibility");
			OnPropertyChanged("DeleteCustomCursorVisibility");
		});
	}

	public void RemoveCustomCursorMod()
	{
		string[] targetFiles = new string[3] { "ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png" };
		InlineArray5<string> buffer = default(InlineArray5<string>);
		buffer[0] = Paths.Mods;
		buffer[1] = "Content";
		buffer[2] = "textures";
		buffer[3] = "Cursors";
		buffer[4] = "KeyboardMouse";
		RemoveCustomFile(targetFiles, Path.Combine(buffer), "No custom cursors found to remove.", delegate
		{
			OnPropertyChanged("ChooseCustomCursorVisibility");
			OnPropertyChanged("DeleteCustomCursorVisibility");
		});
	}

	public void AddCustomShiftlockMod()
	{
		AddCustomFile(new string[1] { "MouseLockedCursor.png" }, Path.Combine(Paths.Mods, "Content", "textures"), "Select a PNG Shiftlock Image", "PNG Images (*.png)|*.png", "Shiftlock", delegate
		{
			OnPropertyChanged("ChooseCustomShiftlockVisibility");
			OnPropertyChanged("DeleteCustomShiftlockVisibility");
		});
	}

	public void RemoveCustomShiftlockMod()
	{
		RemoveCustomFile(new string[1] { "MouseLockedCursor.png" }, Path.Combine(Paths.Mods, "Content", "textures"), "No custom Shiftlock found to remove.", delegate
		{
			OnPropertyChanged("ChooseCustomShiftlockVisibility");
			OnPropertyChanged("DeleteCustomShiftlockVisibility");
		});
	}

	public async Task AddCustomDeathSoundAsync()
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "Audio files|*.ogg;*.oga;*.wav;*.wave;*.mp3;*.mp2;*.mpa;*.flac;*.aac;*.m4a;*.mp4;*.wma;*.aif;*.aiff;*.aifc;*.opus;*.webm;*.3gp;*.3g2;*.ac3;*.amr|All files|*.*",
			Title = "Select a Custom Death Sound"
		};

		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}

		try
		{
			(long generation, CancellationTokenSource cancellation) = BeginDeathSoundConversion();
			Directory.CreateDirectory(Path.GetDirectoryName(Paths.CustomDeathSoundSource)!);
			string convertedSource = Paths.CustomDeathSoundSource + "." + Guid.NewGuid().ToString("N") + ".importing";
			try
			{
				string conversionError = await Task.Run(() => AudioGain.TryApplyGain(openFileDialog.FileName, convertedSource, 1.0, cancellation.Token, out string error) ? string.Empty : error);
				cancellation.Token.ThrowIfCancellationRequested();
				if (generation != Interlocked.Read(ref _deathSoundConversionGeneration))
					return;
				if (!string.IsNullOrEmpty(conversionError))
					throw new InvalidDataException(conversionError);
				Filesystem.AssertReadOnly(Paths.CustomDeathSoundSource);
				File.Move(convertedSource, Paths.CustomDeathSoundSource, overwrite: true);
			}
			finally
			{
				try
				{
					File.Delete(convertedSource);
				}
				catch
				{
				}
				CompleteDeathSoundConversion(generation, cancellation);
			}
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::AddCustomDeathSound", ex);
			Frontend.ShowMessageBox("Failed to add death sound:\n" + ex.Message, MessageBoxImage.Hand);
			return;
		}

		await ApplyDeathSoundVolumeAsync();

		OnPropertyChanged("ChooseCustomDeathSoundVisibility");
		OnPropertyChanged("DeleteCustomDeathSoundVisibility");
		OnPropertyChanged("CustomDeathSoundVolumeVisibility");
	}

	public void AddCustomDeathSound()
	{
		_ = AddCustomDeathSoundAsync();
	}

	public void RemoveCustomDeathSound()
	{
		CancelDeathSoundConversion();
		RemoveCustomFile(new string[1] { "oof.ogg" }, Path.Combine(Paths.Mods, "Content", "sounds"), "No custom death sound found to remove.", delegate
		{
			try
			{
				if (File.Exists(Paths.CustomDeathSoundSource))
				{
					File.Delete(Paths.CustomDeathSoundSource);
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ModsViewModel::RemoveCustomDeathSound", "Could not remove the stored sound: " + ex.Message);
			}

			OnPropertyChanged("ChooseCustomDeathSoundVisibility");
			OnPropertyChanged("DeleteCustomDeathSoundVisibility");
			OnPropertyChanged("CustomDeathSoundVolumeVisibility");
		});
	}

	private void ApplyDeathSoundVolume()
	{
		_ = ApplyDeathSoundVolumeAsync();
	}

	private async Task ApplyDeathSoundVolumeAsync()
	{
		if (!File.Exists(Paths.CustomDeathSoundSource))
		{
			return;
		}

		(long generation, CancellationTokenSource cancellation) = BeginDeathSoundConversion();
		string error;
		try
		{
			double volume = App.Settings.Prop.CustomDeathSoundVolume;
			error = await Task.Run(() => AudioGain.TryApplyGain(Paths.CustomDeathSoundSource, Paths.CustomDeathSound, volume, cancellation.Token, out string conversionError) ? string.Empty : conversionError);
			if (cancellation.IsCancellationRequested || generation != Interlocked.Read(ref _deathSoundConversionGeneration))
				return;
		}
		finally
		{
			CompleteDeathSoundConversion(generation, cancellation);
		}
		if (!string.IsNullOrEmpty(error))
		{
			Frontend.ShowMessageBox("The death sound could not be converted:\n" + error, MessageBoxImage.Warning);
		}
	}

	private (long Generation, CancellationTokenSource Cancellation) BeginDeathSoundConversion()
	{
		long generation = Interlocked.Increment(ref _deathSoundConversionGeneration);
		CancellationTokenSource cancellation = new();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _deathSoundConversionCts, cancellation);
		previous?.Cancel();
		return (generation, cancellation);
	}

	private void CompleteDeathSoundConversion(long generation, CancellationTokenSource cancellation)
	{
		if (generation == Interlocked.Read(ref _deathSoundConversionGeneration))
			Interlocked.CompareExchange(ref _deathSoundConversionCts, null, cancellation);
		cancellation.Dispose();
	}

	private void CancelDeathSoundConversion()
	{
		Interlocked.Increment(ref _deathSoundConversionGeneration);
		CancellationTokenSource? cancellation = Interlocked.Exchange(ref _deathSoundConversionCts, null);
		cancellation?.Cancel();
	}

	public ModsViewModel()
	{
		_file = Path.Combine(_dir, "crosshair.ini");
		Paths.TryEnsureDirectory(_dir);
		try
		{
			LoadIni();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ModsViewModel", "Could not load the crosshair settings: " + ex.Message);
		}
		PickCursorColorCommand = new RelayCommand(delegate
		{
			PickColor(main: true);
		});
		PickOutlineColorCommand = new RelayCommand(delegate
		{
			PickColor(main: false);
		});
		PickHomepageBackgroundColorCommand = new RelayCommand(PickHomepageBackgroundColor);
		PickHomepageBackgroundGradientColorCommand = new RelayCommand(PickHomepageBackgroundGradientColor);
		ChooseHomepageBackgroundMediaCommand = new RelayCommand(ChooseHomepageBackgroundMedia);
		ClearHomepageBackgroundMediaCommand = new RelayCommand(ClearHomepageBackgroundMedia);
		ToggleHomepageBackgroundMediaCommand = new RelayCommand(ToggleHomepageBackgroundMedia);
		GenerateCursorCodeCommand = new RelayCommand(GenerateCode);
		ApplyCursorCodeCommand = new RelayCommand(ApplyCode);
		DownloadCurCommand = new RelayCommand(DownloadCurFile);
		DownloadPngCommand = new RelayCommand(DownloadPngFile);
		AddManagedModCommand = new AsyncRelayCommand(AddManagedModAsync);
		RefreshManagedModsCommand = new AsyncRelayCommand(LoadManagedModsAsync);
		OpenManagedModsRootCommand = new RelayCommand(OpenManagedModsRoot);
		OpenManagedModCommand = new RelayCommand<ManagedModItem>(OpenManagedMod);
		RenameManagedModCommand = new AsyncRelayCommand<ManagedModItem>(RenameManagedModAsync);
		RemoveManagedModCommand = new AsyncRelayCommand<ManagedModItem>(RemoveManagedModAsync);
		ToggleManagedModCommand = new AsyncRelayCommand<ManagedModItem>(ToggleManagedModAsync);
		CopyManagedModIdCommand = new RelayCommand<ManagedModItem>(CopyManagedModId);
		RefreshSkyboxesCommand = new AsyncRelayCommand(() => LoadSkyboxPacksFromGithub(true));
		ChooseSkyboxFaceCommand = new RelayCommand<string>(ChooseSkyboxFace);
		ChooseSingleSkyboxImageCommand = new RelayCommand(ChooseSingleSkyboxImage);
		ApplyCustomSkyboxCommand = new AsyncRelayCommand(ApplyCustomSkyboxAsync);
		RemoveCustomSkyboxCommand = new RelayCommand(RemoveCustomSkybox);
		RefreshGoogleFontsCommand = new AsyncRelayCommand(() => LoadGoogleFontsAsync(true));
		ApplyGoogleFontCommand = new AsyncRelayCommand(ApplyGoogleFontAsync);
		ChooseLocalFontCommand = new AsyncRelayCommand(ChooseLocalFontAsync);
		RemoveCustomFontCommand = new RelayCommand(RemoveCustomFont);
		((DispatcherObject)System.Windows.Application.Current).Dispatcher.BeginInvoke((DispatcherPriority)6, (Delegate)new Action(UpdatePreview));
		LoadCustomCursorSets();
		LoadCursorPathsForSelectedSet();
		NotifyCursorVisibilities();
	}

	private void PickHomepageBackgroundColor()
	{
		System.Windows.Media.Color initial;
		try
		{
			initial = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(HomepageBackgroundOverlayColor);
		}
		catch
		{
			initial = System.Windows.Media.Color.FromRgb(18, 18, 21);
		}
		var dialog = new Voidstrap.UI.Elements.Controls.RinColorPickerDialog(initial);
		if (dialog.ShowDialog() == true)
			HomepageBackgroundOverlayColor = $"#{dialog.SelectedColor.R:X2}{dialog.SelectedColor.G:X2}{dialog.SelectedColor.B:X2}";
	}

	private void PickHomepageBackgroundGradientColor()
	{
		System.Windows.Media.Color initial;
		try
		{
			initial = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(HomepageBackgroundOverlayGradientColor);
		}
		catch
		{
			initial = System.Windows.Media.Color.FromRgb(91, 46, 255);
		}
		var dialog = new Voidstrap.UI.Elements.Controls.RinColorPickerDialog(initial);
		if (dialog.ShowDialog() == true)
			HomepageBackgroundOverlayGradientColor = $"#{dialog.SelectedColor.R:X2}{dialog.SelectedColor.G:X2}{dialog.SelectedColor.B:X2}";
	}

	private static string NormalizeHomepageColor(string? value)
	{
		string text = (value ?? "").Trim();
		if (Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
			return text.ToUpperInvariant();
		return "#121215";
	}

	private void ChooseHomepageBackgroundMedia()
	{
		try
		{
			Microsoft.Win32.OpenFileDialog dialog = new()
			{
				Title = "Choose homepage background",
				Filter = "All supported media|*.png;*.apng;*.jpg;*.jpeg;*.jpe;*.jfif;*.bmp;*.dib;*.gif;*.webp;*.tif;*.tiff;*.ico;*.wdp;*.jxr;*.hdp;*.tga;*.qoi;*.pbm;*.pgm;*.ppm;*.pnm;*.heic;*.heif;*.avif;*.mp4;*.m4v;*.webm;*.avi;*.mov;*.wmv;*.mpeg;*.mpg;*.mkv|All image files|*.*|Videos|*.mp4;*.m4v;*.webm;*.avi;*.mov;*.wmv;*.mpeg;*.mpg;*.mkv|All files|*.*"
			};
			if (dialog.ShowDialog() != true)
				return;
			string path = Path.GetFullPath(dialog.FileName);
			if (!File.Exists(path))
				return;
			App.Settings.Prop.HomepageBackgroundOverlayMediaPath = path;
			App.Settings.Prop.HomepageBackgroundOverlayMode = "Media";
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(SelectedHomepageBackgroundMode));
			OnPropertyChanged(nameof(ShowHomepageSolidColor));
			OnPropertyChanged(nameof(ShowHomepageGradient));
			OnPropertyChanged(nameof(ShowHomepageMedia));
			NotifyHomepageMediaChanged();
			Voidstrap.Integrations.Overlays.OverlayHub.Restart();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ChooseHomepageBackgroundMedia", ex);
			Frontend.ShowMessageBox("The background media could not be opened.", MessageBoxImage.Hand);
		}
	}

	private void ClearHomepageBackgroundMedia()
	{
		if (string.IsNullOrWhiteSpace(App.Settings.Prop.HomepageBackgroundOverlayMediaPath))
			return;
		App.Settings.Prop.HomepageBackgroundOverlayMediaPath = "";
		App.Settings.SaveDeferred();
		NotifyHomepageMediaChanged();
		Voidstrap.Integrations.Overlays.OverlayHub.Restart();
	}

	private void ToggleHomepageBackgroundMedia()
	{
		if (HasHomepageBackgroundMedia)
			ClearHomepageBackgroundMedia();
		else
			ChooseHomepageBackgroundMedia();
	}

	private void NotifyHomepageMediaChanged()
	{
		_homepageResolvedPath = null;
		ReleaseHomepageMediaPreview();
		OnPropertyChanged(nameof(HomepageBackgroundOverlayMediaName));
		OnPropertyChanged(nameof(HomepageBackgroundOverlayMediaPath));
		OnPropertyChanged(nameof(HasHomepageBackgroundMedia));
		OnPropertyChanged(nameof(HomepageBackgroundMediaButtonText));
		NotifyHomepagePreviewChanged();
	}

	public async Task InitializeAsync()
	{
		await Task.WhenAll(LoadModsAsync(), LoadManagedModsAsync(), LoadSkyboxPacksFromGithub(), LoadGoogleFontsAsync());
	}

	public void CancelTransientOperations()
	{
		CancellationTokenSource? fonts = Interlocked.Exchange(ref _fontManagerCts, null);
		fonts?.Cancel();
		fonts?.Dispose();
		CancellationTokenSource? fontPreview = Interlocked.Exchange(ref _fontPreviewCts, null);
		fontPreview?.Cancel();
		fontPreview?.Dispose();
		CancellationTokenSource? skyboxes = Interlocked.Exchange(ref _skyboxManagerCts, null);
		skyboxes?.Cancel();
		skyboxes?.Dispose();
		CancellationTokenSource? skyboxImport = Interlocked.Exchange(ref _skyboxImportCts, null);
		skyboxImport?.Cancel();
		skyboxImport?.Dispose();
		CancelDeathSoundConversion();
		FontManagerBusy = false;
		SkyboxManagerBusy = false;
		CustomSkyboxBusy = false;
	}

	private async Task LoadManagedModsAsync()
	{
		await _managedModsLoadGate.WaitAsync();
		ManagedModsBusy = true;
		try
		{
			ManagedModItem[] items = await Task.Run(() =>
			{
				IReadOnlyList<ManagedModRecord> records = ManagedModStore.Load();
				ManagedModScanResult scan = ManagedModStore.ScanEnabledFiles();
				Dictionary<string, int> pathCounts = new(StringComparer.OrdinalIgnoreCase);
				Dictionary<string, HashSet<string>> pathsByMod = new(StringComparer.OrdinalIgnoreCase);
				try
				{
					foreach (string file in Directory.EnumerateFiles(Paths.Mods, "*", SearchOption.AllDirectories))
					{
						string relative = Path.GetRelativePath(Paths.Mods, file);
						if (!relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !string.Equals(relative, "README.txt", StringComparison.OrdinalIgnoreCase))
							pathCounts[relative] = pathCounts.GetValueOrDefault(relative) + 1;
					}
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("ModsViewModel::LoadManagedMods", "Could not compare the standard mod folder: " + ex.Message);
				}
				foreach (ManagedModFile file in scan.Files)
				{
					pathCounts[file.Relative] = pathCounts.GetValueOrDefault(file.Relative) + 1;
					if (!pathsByMod.TryGetValue(file.Mod.Id, out HashSet<string>? paths))
					{
						paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
						pathsByMod[file.Mod.Id] = paths;
					}
					paths.Add(file.Relative);
				}
				return records.Select(record =>
				{
					string scanError = scan.Failures.GetValueOrDefault(record.Id) ?? string.Empty;
					ManagedModStatistics statistics;
					try
					{
						statistics = ManagedModStore.GetStatistics(record.Id);
					}
					catch (Exception ex)
					{
						App.Logger.WriteLine("ModsViewModel::LoadManagedMods", "Could not inspect " + record.Name + ": " + ex.Message);
						scanError = ex.Message;
						statistics = new ManagedModStatistics(0, 0);
					}
					int conflicts = pathsByMod.TryGetValue(record.Id, out HashSet<string>? paths) ? paths.Count(path => pathCounts.GetValueOrDefault(path) > 1) : 0;
					return new ManagedModItem(record.Id, record.Name, record.Enabled, record.CreatedUtc, statistics.FileCount, statistics.TotalBytes, conflicts, scanError);
				}).ToArray();
			});
			_allManagedMods.Clear();
			_allManagedMods.AddRange(items);
			ApplyManagedModFilter();
			int enabled = items.Count(item => item.Enabled);
			int files = items.Sum(item => item.FileCount);
			ManagedModsSummary = items.Length == 0 ? "No managed mods" : $"{items.Length} mods, {enabled} enabled, {files} files";
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::LoadManagedMods", ex);
			Frontend.ShowMessageBox("The managed mod library could not be loaded:\n" + ex.Message, MessageBoxImage.Warning);
		}
		finally
		{
			ManagedModsBusy = false;
			_managedModsLoadGate.Release();
		}
	}

	private void ApplyManagedModFilter()
	{
		string query = ManagedModSearchText.Trim();
		IEnumerable<ManagedModItem> filtered = string.IsNullOrEmpty(query)
			? _allManagedMods
			: _allManagedMods.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
		ManagedMods.Clear();
		foreach (ManagedModItem item in filtered)
			ManagedMods.Add(item);
		bool searchHasNoResults = ManagedMods.Count == 0 && _allManagedMods.Count > 0 && !string.IsNullOrEmpty(query);
		ManagedModsEmptyTitle = searchHasNoResults ? "No managed mods match your search" : "Your managed mod library is empty";
		ManagedModsEmptyDescription = searchHasNoResults ? "Try a different name or identifier." : "Add a mod to create its indexed folder, then place its files inside.";
	}

	private async Task AddManagedModAsync()
	{
		string? name = AskForManagedModName("Add Mod", "New Mod");
		if (name is null)
			return;
		ManagedModRecord? record = null;
		await _managedModsMutationGate.WaitAsync();
		try
		{
			record = await Task.Run(() => ManagedModStore.Create(name));
			await LoadManagedModsAsync();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The mod could not be added:\n" + ex.Message, MessageBoxImage.Warning);
		}
		finally
		{
			_managedModsMutationGate.Release();
		}
		if (record is not null)
			OpenManagedFolder(ManagedModStore.GetFolder(record.Id));
	}

	private async Task RenameManagedModAsync(ManagedModItem? item)
	{
		if (item is null)
			return;
		string? name = AskForManagedModName("Rename Mod", item.Name);
		if (name is null)
			return;
		await _managedModsMutationGate.WaitAsync();
		try
		{
			await Task.Run(() => ManagedModStore.Rename(item.Id, name));
			await LoadManagedModsAsync();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The mod could not be renamed:\n" + ex.Message, MessageBoxImage.Warning);
		}
		finally
		{
			_managedModsMutationGate.Release();
		}
	}

	private async Task RemoveManagedModAsync(ManagedModItem? item)
	{
		if (item is null || Frontend.ShowMessageBox("Remove " + item.Name + " and all files in its managed folder?", MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
			return;
		await _managedModsMutationGate.WaitAsync();
		try
		{
			await Task.Run(() => ManagedModStore.Delete(item.Id));
			await LoadManagedModsAsync();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The mod could not be removed:\n" + ex.Message, MessageBoxImage.Warning);
		}
		finally
		{
			_managedModsMutationGate.Release();
		}
	}

	public async Task ReorderManagedModAsync(ManagedModItem source, ManagedModItem target, bool insertAfter)
	{
		if (source.Id == target.Id)
			return;
		await _managedModsMutationGate.WaitAsync();
		try
		{
			await Task.Run(() => ManagedModStore.MoveRelative(source.Id, target.Id, insertAfter));
			await LoadManagedModsAsync();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The mod order could not be changed:\n" + ex.Message, MessageBoxImage.Warning);
		}
		finally
		{
			_managedModsMutationGate.Release();
		}
	}

	private async Task ToggleManagedModAsync(ManagedModItem? item)
	{
		if (item is null)
			return;
		await _managedModsMutationGate.WaitAsync();
		try
		{
			await Task.Run(() => ManagedModStore.SetEnabled(item.Id, !item.Enabled));
			await LoadManagedModsAsync();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The mod state could not be saved:\n" + ex.Message, MessageBoxImage.Warning);
		}
		finally
		{
			_managedModsMutationGate.Release();
		}
	}

	private static string? AskForManagedModName(string title, string initial)
	{
		Voidstrap.UI.Elements.Dialogs.TextInputDialog dialog = new(title, initial);
		dialog.Owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
		dialog.ShowDialog();
		return dialog.Confirmed ? dialog.Value : null;
	}

	private static void OpenManagedModsRoot()
	{
		try
		{
			Directory.CreateDirectory(Paths.ManagedModPackages);
			OpenManagedFolder(Paths.ManagedModPackages);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The managed mod library could not be opened:\n" + ex.Message, MessageBoxImage.Warning);
		}
	}

	private static void OpenManagedMod(ManagedModItem? item)
	{
		if (item is null)
			return;
		try
		{
			string folder = ManagedModStore.GetFolder(item.Id);
			Directory.CreateDirectory(folder);
			OpenManagedFolder(folder);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The mod folder could not be opened:\n" + ex.Message, MessageBoxImage.Warning);
		}
	}

	private static void OpenManagedFolder(string folder)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			Frontend.ShowMessageBox(Strings.Common_NotAvailableOnPlatform, MessageBoxImage.Information);
			return;
		}
		try
		{
			System.Diagnostics.Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The folder could not be opened:\n" + ex.Message, MessageBoxImage.Warning);
		}
	}

	private static void CopyManagedModId(ManagedModItem? item)
	{
		if (item is null)
			return;
		try
		{
			System.Windows.Clipboard.SetText(item.Id);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The mod identifier could not be copied:\n" + ex.Message, MessageBoxImage.Warning);
		}
	}

	private void PickColor(bool main)
	{
		var dlg = new Voidstrap.UI.Elements.Controls.RinColorPickerDialog();
		if (dlg.ShowDialog() == true)
		{
			string text = $"#{dlg.SelectedColor.R:X2}{dlg.SelectedColor.G:X2}{dlg.SelectedColor.B:X2}";
			if (main)
			{
				CursorColorHex = text;
			}
			else
			{
				CursorOutlineColorHex = text;
			}
		}
	}

	private ImageSource LoadImageFromUrl(string url)
	{
		try
		{
			return Voidstrap.Utility.AppImage.LoadSync(url);
		}
		catch
		{
			return null;
		}
	}

	private void DownloadCurFile()
	{
		if (!Voidstrap.Utility.SafeImaging.SupportsVisualCapture)
		{
			Frontend.ShowMessageBox(Strings.Common_NotAvailableOnPlatform, MessageBoxImage.Information);
			return;
		}
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01da: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0201: Unknown result type (might be due to invalid IL or missing references)
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_021e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0231: Unknown result type (might be due to invalid IL or missing references)
		//IL_023b: Unknown result type (might be due to invalid IL or missing references)
		//IL_024e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_026b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0275: Unknown result type (might be due to invalid IL or missing references)
		//IL_0288: Unknown result type (might be due to invalid IL or missing references)
		//IL_0292: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02df: Unknown result type (might be due to invalid IL or missing references)
		//IL_0304: Unknown result type (might be due to invalid IL or missing references)
		//IL_0319: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			double num = 32.0;
			DrawingVisual drawingVisual = new DrawingVisual();
			using (DrawingContext drawingContext = drawingVisual.RenderOpen())
			{
				drawingContext.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0.0, 0.0, 64.0, 64.0));
				if (SelectedShape == CrosshairShape.Image && !string.IsNullOrWhiteSpace(ImageUrl))
				{
					if (LoadImageFromUrl(ImageUrl) is BitmapSource imageSource)
					{
						drawingContext.DrawImage(imageSource, new Rect(0.0, 0.0, 64.0, 64.0));
					}
				}
				else
				{
					System.Windows.Media.Color color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CursorColorHex);
					System.Windows.Media.Color color2 = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CursorOutlineColorHex);
					SolidColorBrush solidColorBrush = new SolidColorBrush(color)
					{
						Opacity = CursorOpacity
					};
					SolidColorBrush solidColorBrush2 = new SolidColorBrush(color2)
					{
						Opacity = CursorOpacity
					};
					((Freezable)solidColorBrush).Freeze();
					((Freezable)solidColorBrush2).Freeze();
					double num2 = 1.0;
					double num3 = (double)CursorSize * num2;
					double num4 = (double)Gap * num2;
					double num5 = Math.Max(1.0, (double)CrosshairThickness * num2);
					System.Windows.Media.Pen pen = new System.Windows.Media.Pen(solidColorBrush, num5)
					{
						StartLineCap = PenLineCap.Round,
						EndLineCap = PenLineCap.Round
					};
					System.Windows.Media.Pen pen2 = new System.Windows.Media.Pen(solidColorBrush2, num5 + 2.0)
					{
						StartLineCap = PenLineCap.Round,
						EndLineCap = PenLineCap.Round
					};
					((Freezable)pen).Freeze();
					((Freezable)pen2).Freeze();
					switch (SelectedShape)
					{
					case CrosshairShape.Cross:
						drawingContext.DrawLine(pen2, new Point(num - num3, num), new Point(num - num4, num));
						drawingContext.DrawLine(pen2, new Point(num + num4, num), new Point(num + num3, num));
						drawingContext.DrawLine(pen2, new Point(num, num - num3), new Point(num, num - num4));
						drawingContext.DrawLine(pen2, new Point(num, num + num4), new Point(num, num + num3));
						drawingContext.DrawLine(pen, new Point(num - num3, num), new Point(num - num4, num));
						drawingContext.DrawLine(pen, new Point(num + num4, num), new Point(num + num3, num));
						drawingContext.DrawLine(pen, new Point(num, num - num3), new Point(num, num - num4));
						drawingContext.DrawLine(pen, new Point(num, num + num4), new Point(num, num + num3));
						break;
					case CrosshairShape.Dot:
					{
						double num7 = num3 / 3.0;
						drawingContext.DrawEllipse(solidColorBrush2, null, new Point(num, num), num7 + 2.0, num7 + 2.0);
						drawingContext.DrawEllipse(solidColorBrush, null, new Point(num, num), num7, num7);
						break;
					}
					case CrosshairShape.Circle:
					{
						double num6 = num3 / 2.0;
						drawingContext.DrawEllipse(null, pen2, new Point(num, num), num6, num6);
						drawingContext.DrawEllipse(null, pen, new Point(num, num), num6 - 2.0, num6 - 2.0);
						break;
					}
					}
				}
			}
			RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(64, 64, 96.0, 96.0, PixelFormats.Pbgra32);
			renderTargetBitmap.Render(drawingVisual);
			int num8 = 256;
			byte[] array = new byte[num8 * 64];
			renderTargetBitmap.CopyPixels(array, num8, 0);
			byte[] array2 = new byte[array.Length];
			for (int i = 0; i < 64; i++)
			{
				Array.Copy(array, i * num8, array2, (64 - i - 1) * num8, num8);
			}
			Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
			{
				Filter = "Cursor File (*.cur)|*.cur",
				FileName = "crosshair.cur"
			};
			if (saveFileDialog.ShowDialog() != true)
			{
				return;
			}
			using FileStream output = new FileStream(saveFileDialog.FileName, FileMode.Create);
			using BinaryWriter binaryWriter = new BinaryWriter(output);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)2);
			binaryWriter.Write((ushort)1);
			binaryWriter.Write((byte)64);
			binaryWriter.Write((byte)64);
			binaryWriter.Write((byte)0);
			binaryWriter.Write((byte)0);
			binaryWriter.Write((ushort)32);
			binaryWriter.Write((ushort)32);
			int value = 40 + array2.Length + 512;
			binaryWriter.Write((uint)value);
			binaryWriter.Write(22u);
			binaryWriter.Write(40);
			binaryWriter.Write(64);
			binaryWriter.Write(128);
			binaryWriter.Write((ushort)1);
			binaryWriter.Write((ushort)32);
			binaryWriter.Write(0);
			binaryWriter.Write(array2.Length);
			binaryWriter.Write(0);
			binaryWriter.Write(0);
			binaryWriter.Write(0);
			binaryWriter.Write(0);
			binaryWriter.Write(array2);
			int num9 = 512;
			binaryWriter.Write(new byte[num9]);
			binaryWriter.Flush();
			Frontend.ShowMessageBox("Crosshair CUR Saved");
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to generate cursor:\n" + ex.Message);
		}
	}

	private void DownloadPngFile()
	{
		if (!Voidstrap.Utility.SafeImaging.SupportsVisualCapture)
		{
			Frontend.ShowMessageBox(Strings.Common_NotAvailableOnPlatform, MessageBoxImage.Information);
			return;
		}
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01da: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0201: Unknown result type (might be due to invalid IL or missing references)
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_021e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0231: Unknown result type (might be due to invalid IL or missing references)
		//IL_023b: Unknown result type (might be due to invalid IL or missing references)
		//IL_024e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_026b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0275: Unknown result type (might be due to invalid IL or missing references)
		//IL_0288: Unknown result type (might be due to invalid IL or missing references)
		//IL_0292: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02df: Unknown result type (might be due to invalid IL or missing references)
		//IL_0304: Unknown result type (might be due to invalid IL or missing references)
		//IL_0319: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			double num = 64.0;
			DrawingVisual drawingVisual = new DrawingVisual();
			using (DrawingContext drawingContext = drawingVisual.RenderOpen())
			{
				drawingContext.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0.0, 0.0, 128.0, 128.0));
				if (SelectedShape == CrosshairShape.Image && !string.IsNullOrWhiteSpace(ImageUrl))
				{
					if (LoadImageFromUrl(ImageUrl) is BitmapSource imageSource)
					{
						drawingContext.DrawImage(imageSource, new Rect(0.0, 0.0, 128.0, 128.0));
					}
				}
				else
				{
					System.Windows.Media.Color color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CursorColorHex);
					System.Windows.Media.Color color2 = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CursorOutlineColorHex);
					SolidColorBrush solidColorBrush = new SolidColorBrush(color)
					{
						Opacity = CursorOpacity
					};
					SolidColorBrush solidColorBrush2 = new SolidColorBrush(color2)
					{
						Opacity = CursorOpacity
					};
					((Freezable)solidColorBrush).Freeze();
					((Freezable)solidColorBrush2).Freeze();
					double num2 = 1.0;
					double num3 = (double)CursorSize * num2;
					double num4 = (double)Gap * num2;
					double num5 = Math.Max(1.0, (double)CrosshairThickness * num2);
					System.Windows.Media.Pen pen = new System.Windows.Media.Pen(solidColorBrush, num5)
					{
						StartLineCap = PenLineCap.Round,
						EndLineCap = PenLineCap.Round
					};
					System.Windows.Media.Pen pen2 = new System.Windows.Media.Pen(solidColorBrush2, num5 + 2.0)
					{
						StartLineCap = PenLineCap.Round,
						EndLineCap = PenLineCap.Round
					};
					((Freezable)pen).Freeze();
					((Freezable)pen2).Freeze();
					switch (SelectedShape)
					{
					case CrosshairShape.Cross:
						drawingContext.DrawLine(pen2, new Point(num - num3, num), new Point(num - num4, num));
						drawingContext.DrawLine(pen2, new Point(num + num4, num), new Point(num + num3, num));
						drawingContext.DrawLine(pen2, new Point(num, num - num3), new Point(num, num - num4));
						drawingContext.DrawLine(pen2, new Point(num, num + num4), new Point(num, num + num3));
						drawingContext.DrawLine(pen, new Point(num - num3, num), new Point(num - num4, num));
						drawingContext.DrawLine(pen, new Point(num + num4, num), new Point(num + num3, num));
						drawingContext.DrawLine(pen, new Point(num, num - num3), new Point(num, num - num4));
						drawingContext.DrawLine(pen, new Point(num, num + num4), new Point(num, num + num3));
						break;
					case CrosshairShape.Dot:
					{
						double num7 = num3 / 3.0;
						drawingContext.DrawEllipse(solidColorBrush2, null, new Point(num, num), num7 + 2.0, num7 + 2.0);
						drawingContext.DrawEllipse(solidColorBrush, null, new Point(num, num), num7, num7);
						break;
					}
					case CrosshairShape.Circle:
					{
						double num6 = num3 / 2.0;
						drawingContext.DrawEllipse(null, pen2, new Point(num, num), num6, num6);
						drawingContext.DrawEllipse(null, pen, new Point(num, num), num6 - 2.0, num6 - 2.0);
						break;
					}
					}
				}
			}
			RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(128, 128, 96.0, 96.0, PixelFormats.Pbgra32);
			renderTargetBitmap.Render(drawingVisual);
			Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
			{
				Filter = "PNG Image (*.png)|*.png",
				FileName = "crosshair.png"
			};
			if (saveFileDialog.ShowDialog() != true)
			{
				return;
			}
			PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
			pngBitmapEncoder.Frames.Add(BitmapFrame.Create(renderTargetBitmap));
			using FileStream stream = new FileStream(saveFileDialog.FileName, FileMode.Create);
			pngBitmapEncoder.Save(stream);
			Frontend.ShowMessageBox("Crosshair PNG Saved");
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to save PNG:\n" + ex.Message);
		}
	}

	public void GenerateCode()
	{
		if (SelectedShape == CrosshairShape.Image && !string.IsNullOrWhiteSpace(ImageUrl))
		{
			CursorCode = $"VXH:IMAGE|{ImageUrl}|{CursorSize}|{CursorOpacity}";
			return;
		}
		CursorCode = $"VXH:{SelectedShape}|{CursorColorHex}|{CursorOutlineColorHex}|{CursorSize}|{CrosshairThickness}|{Gap}|{CursorOpacity}";
	}

	private void ApplyCode()
	{
		if (string.IsNullOrWhiteSpace(CursorCode) || !CursorCode.StartsWith("VXH:"))
		{
			return;
		}
		string[] array = CursorCode.Substring(4).Split('|');
		try
		{
			if (array[0] == "IMAGE")
			{
				SelectedShape = CrosshairShape.Image;
				ImageUrl = ((array.Length > 1) ? array[1] : "");
				CursorSize = ((array.Length > 2 && int.TryParse(array[2], out var result)) ? result : 20);
				CursorOpacity = ((array.Length > 3 && double.TryParse(array[3], out var result2)) ? result2 : 1.0);
				return;
			}
			if (!Enum.TryParse<CrosshairShape>(array[0], ignoreCase: true, out var result3))
			{
				result3 = CrosshairShape.Cross;
			}
			SelectedShape = result3;
			CursorColorHex = ((array.Length > 1) ? array[1] : "#00FF00");
			CursorOutlineColorHex = ((array.Length > 2) ? array[2] : "#000000");
			CursorSize = ((array.Length > 3 && int.TryParse(array[3], out var result4)) ? result4 : 20);
			CrosshairThickness = ((array.Length > 4 && int.TryParse(array[4], out var result5)) ? result5 : 2);
			Gap = ((array.Length > 5 && int.TryParse(array[5], out var result6)) ? result6 : 4);
			CursorOpacity = ((array.Length > 6 && double.TryParse(array[6], out var result7)) ? result7 : 1.0);
		}
		catch
		{
		}
	}

	private void UpdatePreview()
	{
		if (System.Windows.Application.Current == null)
		{
			return;
		}
		((DispatcherObject)System.Windows.Application.Current).Dispatcher.Invoke((Action)delegate
		{
			//IL_0076: Unknown result type (might be due to invalid IL or missing references)
			//IL_0196: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b3: Unknown result type (might be due to invalid IL or missing references)
			//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
			//IL_01d0: Unknown result type (might be due to invalid IL or missing references)
			//IL_01da: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ed: Unknown result type (might be due to invalid IL or missing references)
			//IL_01f7: Unknown result type (might be due to invalid IL or missing references)
			//IL_020a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0214: Unknown result type (might be due to invalid IL or missing references)
			//IL_0227: Unknown result type (might be due to invalid IL or missing references)
			//IL_0231: Unknown result type (might be due to invalid IL or missing references)
			//IL_0244: Unknown result type (might be due to invalid IL or missing references)
			//IL_024e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0261: Unknown result type (might be due to invalid IL or missing references)
			//IL_026b: Unknown result type (might be due to invalid IL or missing references)
			//IL_028f: Unknown result type (might be due to invalid IL or missing references)
			//IL_02b8: Unknown result type (might be due to invalid IL or missing references)
			//IL_02dd: Unknown result type (might be due to invalid IL or missing references)
			//IL_02f2: Unknown result type (might be due to invalid IL or missing references)
			try
			{
				double num = 64.0;
				if (SelectedShape == CrosshairShape.Image && !string.IsNullOrWhiteSpace(ImageUrl))
				{
					ImageSource imageSource = LoadImageFromUrl(ImageUrl);
					if (imageSource != null)
					{
						CursorPreview = imageSource;
						return;
					}
				}
				DrawingVisual drawingVisual = new DrawingVisual();
				using (DrawingContext drawingContext = drawingVisual.RenderOpen())
				{
					drawingContext.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0.0, 0.0, 128.0, 128.0));
					System.Windows.Media.Color color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CursorColorHex);
					System.Windows.Media.Color color2 = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CursorOutlineColorHex);
					SolidColorBrush solidColorBrush = new SolidColorBrush(color)
					{
						Opacity = CursorOpacity
					};
					((Freezable)solidColorBrush).Freeze();
					SolidColorBrush solidColorBrush2 = new SolidColorBrush(color2)
					{
						Opacity = CursorOpacity
					};
					((Freezable)solidColorBrush2).Freeze();
					double num2 = 0.75;
					double num3 = (double)CursorSize * num2;
					double num4 = (double)Gap * num2;
					double num5 = Math.Max(1.0, (double)CrosshairThickness * num2);
					System.Windows.Media.Pen pen = new System.Windows.Media.Pen(solidColorBrush, num5)
					{
						StartLineCap = PenLineCap.Round,
						EndLineCap = PenLineCap.Round,
						LineJoin = PenLineJoin.Round
					};
					((Freezable)pen).Freeze();
					System.Windows.Media.Pen pen2 = new System.Windows.Media.Pen(solidColorBrush2, num5 + 2.0)
					{
						StartLineCap = PenLineCap.Round,
						EndLineCap = PenLineCap.Round,
						LineJoin = PenLineJoin.Round
					};
					((Freezable)pen2).Freeze();
					switch (SelectedShape)
					{
					case CrosshairShape.Cross:
						drawingContext.DrawLine(pen2, new Point(num - num3, num), new Point(num - num4, num));
						drawingContext.DrawLine(pen2, new Point(num + num4, num), new Point(num + num3, num));
						drawingContext.DrawLine(pen, new Point(num - num3, num), new Point(num - num4, num));
						drawingContext.DrawLine(pen, new Point(num + num4, num), new Point(num + num3, num));
						drawingContext.DrawLine(pen2, new Point(num, num - num3), new Point(num, num - num4));
						drawingContext.DrawLine(pen2, new Point(num, num + num4), new Point(num, num + num3));
						drawingContext.DrawLine(pen, new Point(num, num - num3), new Point(num, num - num4));
						drawingContext.DrawLine(pen, new Point(num, num + num4), new Point(num, num + num3));
						break;
					case CrosshairShape.Dot:
					{
						double num7 = num3 / 3.0;
						drawingContext.DrawEllipse(solidColorBrush2, null, new Point(num, num), num7 + 2.0, num7 + 2.0);
						drawingContext.DrawEllipse(solidColorBrush, null, new Point(num, num), num7, num7);
						break;
					}
					case CrosshairShape.Circle:
					{
						double num6 = num3 / 2.0;
						drawingContext.DrawEllipse(null, pen2, new Point(num, num), num6, num6);
						drawingContext.DrawEllipse(null, pen, new Point(num, num), num6 - 2.0, num6 - 2.0);
						break;
					}
					}
				}
				RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(128, 128, 96.0, 96.0, PixelFormats.Pbgra32);
				renderTargetBitmap.Render(drawingVisual);
				((Freezable)renderTargetBitmap).Freeze();
				CursorPreview = renderTargetBitmap;
			}
			catch
			{
				CursorPreview = null;
			}
		});
	}

	private void MirrorCrosshairToSettings()
	{
		try
		{
			var prop = App.Settings.Prop;
			prop.CrosshairShapeIndex = (int)SelectedShape;
			prop.CrosshairColorHex = CursorColorHex ?? "";
			prop.CrosshairOutlineColorHex = CursorOutlineColorHex ?? "";
			prop.CrosshairSize = CursorSize;
			prop.CrosshairLineThickness = CrosshairThickness;
			prop.CrosshairGap = Gap;
			prop.CrosshairOpacity = CursorOpacity;
			App.Settings.SaveDeferred();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::MirrorCrosshairToSettings", ex);
		}
	}

	private void SaveIni()
	{
		MirrorCrosshairToSettings();
		IniFile.Write(_file, new Dictionary<string, string>
		{
			["Shape"] = SelectedShape.ToString(),
			["Color"] = CursorColorHex,
			["Outline"] = CursorOutlineColorHex,
			["Size"] = CursorSize.ToString(),
			["Thickness"] = CrosshairThickness.ToString(),
			["Gap"] = Gap.ToString(),
			["Opacity"] = CursorOpacity.ToString(),
			["ImageUrl"] = ImageUrl ?? ""
		});
	}

	private void LoadIni()
	{
		if (File.Exists(_file))
		{
			Dictionary<string, string> dictionary = IniFile.Read(_file);
			if (!Enum.TryParse<CrosshairShape>(dictionary.GetValueOrDefault("Shape", "Cross"), ignoreCase: true, out var result))
			{
				result = CrosshairShape.Cross;
			}
			SelectedShape = result;
			CursorColorHex = dictionary.GetValueOrDefault("Color", "#00FF00");
			CursorOutlineColorHex = dictionary.GetValueOrDefault("Outline", "#000000");
			if (!int.TryParse(dictionary.GetValueOrDefault("Size", "20"), out var result2))
			{
				result2 = 20;
			}
			CursorSize = result2;
			if (!int.TryParse(dictionary.GetValueOrDefault("Thickness", "2"), out var result3))
			{
				result3 = 2;
			}
			CrosshairThickness = result3;
			if (!int.TryParse(dictionary.GetValueOrDefault("Gap", "4"), out var result4))
			{
				result4 = 4;
			}
			Gap = result4;
			if (!double.TryParse(dictionary.GetValueOrDefault("Opacity", "1.0"), out var result5))
			{
				result5 = 1.0;
			}
			CursorOpacity = result5;
			ImageUrl = dictionary.GetValueOrDefault("ImageUrl", "");
		}
	}

	private void LoadCustomCursorSets()
	{
		CustomCursorSets.Clear();
		if (!Directory.Exists(Paths.CustomCursors))
		{
			Directory.CreateDirectory(Paths.CustomCursors);
		}
		string[] directories = Directory.GetDirectories(Paths.CustomCursors);
		foreach (string text in directories)
		{
			string fileName = Path.GetFileName(text);
			CustomCursorSets.Add(new CustomCursorSet
			{
				Name = fileName,
				FolderPath = text
			});
		}
		if (CustomCursorSets.Any())
		{
			SelectedCustomCursorSetIndex = 0;
		}
		OnPropertyChanged("IsCustomCursorSetSelected");
	}

	private void AddCustomCursorSet()
	{
		string customCursors = Paths.CustomCursors;
		int num = 1;
		string text;
		do
		{
			string path = $"Custom Cursor Set {num}";
			text = Path.Combine(customCursors, path);
			num++;
		}
		while (Directory.Exists(text));
		try
		{
			Directory.CreateDirectory(text);
			CustomCursorSet item = new CustomCursorSet
			{
				Name = Path.GetFileName(text),
				FolderPath = text
			};
			CustomCursorSets.Add(item);
			SelectedCustomCursorSetIndex = CustomCursorSets.Count - 1;
			OnPropertyChanged("IsCustomCursorSetSelected");
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::AddCustomCursorSet", ex);
			Frontend.ShowMessageBox("Failed to create cursor set:\n" + ex.Message, MessageBoxImage.Hand);
		}
	}

	private void DeleteCustomCursorSet()
	{
		if (SelectedCustomCursorSet == null)
		{
			return;
		}
		try
		{
			if (Directory.Exists(SelectedCustomCursorSet.FolderPath))
			{
				Filesystem.AssertReadOnlyDirectory(SelectedCustomCursorSet.FolderPath);
				Directory.Delete(SelectedCustomCursorSet.FolderPath, recursive: true);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::DeleteCustomCursorSet", ex);
			Frontend.ShowMessageBox("Failed to delete cursor set:\n" + ex.Message, MessageBoxImage.Hand);
			return;
		}
		CustomCursorSets.Remove(SelectedCustomCursorSet);
		if (CustomCursorSets.Any())
		{
			SelectedCustomCursorSetIndex = CustomCursorSets.Count - 1;
			OnPropertyChanged("SelectedCustomCursorSet");
		}
		OnPropertyChanged("IsCustomCursorSetSelected");
	}

	private void RenameCustomCursorSetStructure(string oldName, string newName)
	{
		string sourceDirName = Path.Combine(Paths.CustomCursors, oldName);
		string text = Path.Combine(Paths.CustomCursors, newName);
		if (Directory.Exists(text))
		{
			throw new IOException("A folder with the new name already exists.");
		}
		Directory.Move(sourceDirName, text);
	}

	private void RenameCustomCursorSet()
	{
		if (SelectedCustomCursorSet == null || SelectedCustomCursorSet.Name == SelectedCustomCursorSetName)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(SelectedCustomCursorSetName))
		{
			Frontend.ShowMessageBox("Name cannot be empty.", MessageBoxImage.Hand);
			return;
		}
		PathValidator.ValidationResult validationResult = PathValidator.IsFileNameValid(SelectedCustomCursorSetName);
		if (validationResult != PathValidator.ValidationResult.Ok)
		{
			object message = validationResult switch
			{
				PathValidator.ValidationResult.IllegalCharacter => "Name contains illegal characters.", 
				PathValidator.ValidationResult.ReservedFileName => "Name is reserved.", 
				_ => "Unknown validation error.", 
			};
			App.Logger.WriteLine("ModsViewModel::RenameCustomCursorSet", $"Validation result: {validationResult}");
			Frontend.ShowMessageBox((string)message, MessageBoxImage.Hand);
			return;
		}
		try
		{
			RenameCustomCursorSetStructure(SelectedCustomCursorSet.Name, SelectedCustomCursorSetName);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::RenameCustomCursorSet", ex);
			Frontend.ShowMessageBox("Failed to rename:\n" + ex.Message, MessageBoxImage.Hand);
			return;
		}
		int num = CustomCursorSets.IndexOf(SelectedCustomCursorSet);
		CustomCursorSets[num] = new CustomCursorSet
		{
			Name = SelectedCustomCursorSetName,
			FolderPath = Path.Combine(Paths.CustomCursors, SelectedCustomCursorSetName)
		};
		SelectedCustomCursorSetIndex = num;
		OnPropertyChanged("SelectedCustomCursorSetIndex");
	}

	private void ApplyCursorSet()
	{
		if (SelectedCustomCursorSet == null)
		{
			Frontend.ShowMessageBox("Please select a cursor set first.", MessageBoxImage.Exclamation);
			return;
		}
		string folderPath = SelectedCustomCursorSet.FolderPath;
		string text = Path.Combine(Paths.Mods, "content", "textures");
		string text2 = Path.Combine(text, "Cursors", "KeyboardMouse");
		try
		{
			if (!Directory.Exists(folderPath))
			{
				Frontend.ShowMessageBox("Selected cursor set folder does not exist.", MessageBoxImage.Hand);
				return;
			}
			Directory.CreateDirectory(text);
			Directory.CreateDirectory(text2);
			string[] array = new string[4]
			{
				Path.Combine(text, "MouseLockedCursor.png"),
				Path.Combine(text2, "ArrowCursor.png"),
				Path.Combine(text2, "ArrowFarCursor.png"),
				Path.Combine(text2, "IBeamCursor.png")
			};
			HashSet<string> appliedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string text3 in Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories))
			{
				string relativePath = Path.GetRelativePath(folderPath, text3);
				string text4 = Path.Combine(text, relativePath);
				Filesystem.CopyWritableFile(text3, text4);
				appliedFiles.Add(Path.GetFullPath(text4));
			}
			foreach (string path in array)
			{
				if (!appliedFiles.Contains(Path.GetFullPath(path)))
				{
					Filesystem.DeleteWritableFile(path);
				}
			}
			Frontend.ShowMessageBox("Cursor set '" + SelectedCustomCursorSet.Name + "' applied successfully!", MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ApplyCursorSet", ex);
			Frontend.ShowMessageBox("Failed to apply cursor set:\n" + ex.Message, MessageBoxImage.Hand);
		}
		LoadCursorPathsForSelectedSet();
		OnPropertyChanged("ChooseCustomShiftlockVisibility");
		OnPropertyChanged("DeleteCustomShiftlockVisibility");
		OnPropertyChanged("ChooseCustomCursorVisibility");
		OnPropertyChanged("DeleteCustomCursorVisibility");
	}

	private void GetCurrentCursorSet()
	{
		if (SelectedCustomCursorSet == null)
		{
			Frontend.ShowMessageBox("Please select a cursor set first.", MessageBoxImage.Exclamation);
			return;
		}
		string text = Path.Combine(Paths.Mods, "content", "textures", "MouseLockedCursor.png");
		InlineArray5<string> buffer = default(InlineArray5<string>);
		buffer[0] = Paths.Mods;
		buffer[1] = "content";
		buffer[2] = "textures";
		buffer[3] = "Cursors";
		buffer[4] = "KeyboardMouse";
		string text2 = Path.Combine(buffer);
		string folderPath = SelectedCustomCursorSet.FolderPath;
		string text3 = Path.Combine(folderPath, "MouseLockedCursor.png");
		string text4 = Path.Combine(folderPath, "Cursors", "KeyboardMouse");
		try
		{
			Directory.CreateDirectory(folderPath);
			Directory.CreateDirectory(text4);
			string[] array = new string[4]
			{
				text3,
				Path.Combine(text4, "ArrowCursor.png"),
				Path.Combine(text4, "ArrowFarCursor.png"),
				Path.Combine(text4, "IBeamCursor.png")
			};
			foreach (string path in array)
			{
				Filesystem.DeleteWritableFile(path);
			}
			if (File.Exists(text))
			{
				Filesystem.CopyWritableFile(text, text3);
			}
			if (Directory.Exists(text2))
			{
				array = new string[3] { "ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png" };
				foreach (string path2 in array)
				{
					string text5 = Path.Combine(text2, path2);
					string destFileName = Path.Combine(text4, path2);
					if (File.Exists(text5))
					{
						Filesystem.CopyWritableFile(text5, destFileName);
					}
				}
			}
			Frontend.ShowMessageBox("Current cursor set copied into selected folder.", MessageBoxImage.Asterisk);
			NotifyCursorVisibilities();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::GetCurrentCursorSet", ex);
			Frontend.ShowMessageBox("Failed to get current cursor set:\n" + ex.Message, MessageBoxImage.Hand);
		}
		LoadCursorPathsForSelectedSet();
		NotifyCursorVisibilities();
	}

	private void ExportCursorSet()
	{
		if (SelectedCustomCursorSet == null)
		{
			return;
		}
		Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
		{
			FileName = SelectedCustomCursorSet.Name + ".zip",
			Filter = Strings.FileTypes_ZipArchive + "|*.zip"
		};
		if (saveFileDialog.ShowDialog() != true)
		{
			return;
		}
		string folderPath = SelectedCustomCursorSet.FolderPath;
		try
		{
			using MemoryStream memoryStream = new MemoryStream();
			using ZipOutputStream zipOutputStream = new ZipOutputStream(memoryStream);
			foreach (string item in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories))
			{
				ZipEntry entry = new ZipEntry(item.Substring(folderPath.Length + 1).Replace('\\', '/'))
				{
					DateTime = DateTime.Now,
					Size = new FileInfo(item).Length
				};
				zipOutputStream.PutNextEntry(entry);
				using FileStream fileStream = File.OpenRead(item);
				fileStream.CopyTo(zipOutputStream);
				zipOutputStream.CloseEntry();
			}
			zipOutputStream.Finish();
			memoryStream.Position = 0L;
			using FileStream destination = new(saveFileDialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
			memoryStream.CopyTo(destination);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ExportCursorSet", ex);
			Frontend.ShowMessageBox("Failed to export cursor set:\n" + ex.Message, MessageBoxImage.Hand);
			return;
		}
		Process.Start("explorer.exe", "/select,\"" + saveFileDialog.FileName + "\"");
	}

	private void ImportCursorSet()
	{
		if (SelectedCustomCursorSet == null)
		{
			Frontend.ShowMessageBox("Please select a cursor set first.", MessageBoxImage.Exclamation);
			return;
		}
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Import Cursor Set",
			Filter = Strings.FileTypes_ZipArchive + "|*.zip",
			Multiselect = false
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		string text = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
		try
		{
			Directory.CreateDirectory(text);
			ExtractZipToDirectory(openFileDialog.FileName, text);
			string text2 = Path.Combine(SelectedCustomCursorSet.FolderPath, "MouseLockedCursor.png");
			string text3 = Path.Combine(SelectedCustomCursorSet.FolderPath, "Cursors", "KeyboardMouse");
			string text4 = Directory.GetFiles(text, "MouseLockedCursor.png", SearchOption.AllDirectories).FirstOrDefault();
			if (text4 != null)
			{
				Filesystem.CopyWritableFile(text4, text2);
			}
			else
			{
				Filesystem.DeleteWritableFile(text2);
			}
			Directory.CreateDirectory(text3);
			string[] array = new string[3] { "ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png" };
			foreach (string text5 in array)
			{
				string text6 = Directory.GetFiles(text, text5, SearchOption.AllDirectories).FirstOrDefault();
				string destFileName = Path.Combine(text3, text5);
				if (text6 != null)
				{
					Filesystem.CopyWritableFile(text6, destFileName);
				}
				else
				{
					Filesystem.DeleteWritableFile(destFileName);
				}
			}
			Frontend.ShowMessageBox("Cursor set imported successfully.", MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ImportCursorSet", ex);
			Frontend.ShowMessageBox("Failed to import cursor set:\n" + ex.Message, MessageBoxImage.Hand);
		}
		finally
		{
			try
			{
				if (Directory.Exists(text))
				{
					Filesystem.AssertReadOnlyDirectory(text);
					Directory.Delete(text, recursive: true);
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("ModsViewModel::ImportCursorSetCleanup", ex);
			}
		}
		LoadCursorPathsForSelectedSet();
	}

	private void ExtractZipToDirectory(string zipFilePath, string extractPath)
	{
		SafeZipExtractor.ExtractToDirectory(zipFilePath, extractPath, true, 256L * 1024 * 1024, 4096);
	}

	private string? GetCursorTargetPath(string fileName)
	{
		if (SelectedCustomCursorSet == null)
		{
			return null;
		}
		string obj = ((fileName == "MouseLockedCursor.png") ? SelectedCustomCursorSet.FolderPath : Path.Combine(SelectedCustomCursorSet.FolderPath, "Cursors", "KeyboardMouse"));
		Directory.CreateDirectory(obj);
		return Path.Combine(obj, fileName);
	}

	private void DeleteCursorImage(string fileName)
	{
		string cursorTargetPath = GetCursorTargetPath(fileName);
		if (cursorTargetPath != null && File.Exists(cursorTargetPath))
		{
			try
			{
				Filesystem.DeleteWritableFile(cursorTargetPath);
				UpdateCursorPathProperty(fileName, "");
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("ModsViewModel::Delete" + fileName, ex);
				Frontend.ShowMessageBox("Failed to delete " + fileName + ":\n" + ex.Message, MessageBoxImage.Hand);
			}
			LoadCursorPathsForSelectedSet();
			NotifyCursorVisibilities();
			OnPropertyChanged("ChooseCustomShiftlockVisibility");
			OnPropertyChanged("DeleteCustomShiftlockVisibility");
			OnPropertyChanged("ChooseCustomCursorVisibility");
			OnPropertyChanged("DeleteCustomCursorVisibility");
		}
	}

	private void AddShiftlockCursor()
	{
		AddCursorImage("MouseLockedCursor.png", "Select Shiftlock PNG");
		OnPropertyChanged("ChooseCustomShiftlockVisibility");
		OnPropertyChanged("DeleteCustomShiftlockVisibility");
		OnPropertyChanged("ChooseCustomCursorVisibility");
		OnPropertyChanged("DeleteCustomCursorVisibility");
	}

	private void AddCursorImage(string fileName, string dialogTitle)
	{
		if (SelectedCustomCursorSet == null)
		{
			Frontend.ShowMessageBox("Please select a cursor set first.", MessageBoxImage.Exclamation);
			return;
		}
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = dialogTitle,
			Filter = "PNG files (*.png)|*.png",
			Multiselect = false
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		string cursorTargetPath = GetCursorTargetPath(fileName);
		if (cursorTargetPath == null)
		{
			return;
		}
		try
		{
			Filesystem.CopyWritableFile(openFileDialog.FileName, cursorTargetPath);
			UpdateCursorPathAndPreview(fileName, openFileDialog.FileName);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::Add" + fileName, ex);
			Frontend.ShowMessageBox("Failed to add " + fileName + ":\n" + ex.Message, MessageBoxImage.Hand);
		}
		LoadCursorPathsForSelectedSet();
		NotifyCursorVisibilities();
		OnPropertyChanged("ChooseCustomShiftlockVisibility");
		OnPropertyChanged("DeleteCustomShiftlockVisibility");
		OnPropertyChanged("ChooseCustomCursorVisibility");
		OnPropertyChanged("DeleteCustomCursorVisibility");
	}

	private void UpdateCursorPathProperty(string fileName, string path)
	{
		switch (fileName)
		{
		case "MouseLockedCursor.png":
			ShiftlockCursorSelectedPath = path;
			break;
		case "ArrowCursor.png":
			ArrowCursorSelectedPath = path;
			break;
		case "ArrowFarCursor.png":
			ArrowFarCursorSelectedPath = path;
			break;
		case "IBeamCursor.png":
			IBeamCursorSelectedPath = path;
			break;
		}
	}

	private void UpdateCursorPathAndPreview(string fileName, string fullPath)
	{
		if (!File.Exists(fullPath))
		{
			fullPath = "";
		}
		ImageSource imageSource = LoadImageSafely(fullPath);
		switch (fileName)
		{
		case "MouseLockedCursor.png":
			ShiftlockCursorSelectedPath = fullPath;
			ShiftlockCursorPreview = imageSource;
			App.Settings.Prop.ShiftlockCursorSelectedPath = fullPath;
			break;
		case "ArrowCursor.png":
			ArrowCursorSelectedPath = fullPath;
			ArrowCursorPreview = imageSource;
			App.Settings.Prop.ArrowCursorSelectedPath = fullPath;
			break;
		case "ArrowFarCursor.png":
			ArrowFarCursorSelectedPath = fullPath;
			ArrowFarCursorPreview = imageSource;
			App.Settings.Prop.ArrowFarCursorSelectedPath = fullPath;
			break;
		case "IBeamCursor.png":
			IBeamCursorSelectedPath = fullPath;
			IBeamCursorPreview = imageSource;
			App.Settings.Prop.IBeamCursorSelectedPath = fullPath;
			break;
		}
		App.Settings.SaveDeferred();
	}

	private void LoadCursorPathsForSelectedSet()
	{
		if (SelectedCustomCursorSet == null)
		{
			UpdateCursorPathAndPreview("MouseLockedCursor.png", "");
			UpdateCursorPathAndPreview("ArrowCursor.png", "");
			UpdateCursorPathAndPreview("ArrowFarCursor.png", "");
			UpdateCursorPathAndPreview("IBeamCursor.png", "");
		}
		else
		{
			string folderPath = SelectedCustomCursorSet.FolderPath;
			string path = Path.Combine(folderPath, "Cursors", "KeyboardMouse");
			UpdateCursorPathAndPreview("MouseLockedCursor.png", Path.Combine(folderPath, "MouseLockedCursor.png"));
			UpdateCursorPathAndPreview("ArrowCursor.png", Path.Combine(path, "ArrowCursor.png"));
			UpdateCursorPathAndPreview("ArrowFarCursor.png", Path.Combine(path, "ArrowFarCursor.png"));
			UpdateCursorPathAndPreview("IBeamCursor.png", Path.Combine(path, "IBeamCursor.png"));
		}
	}

	private static BitmapSource LoadImageSafely(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}
		try
		{
			return Voidstrap.Utility.SafeImaging.FromFile(path);
		}
		catch
		{
			return null;
		}
	}

	private Visibility GetCursorAddVisibility(string fileName)
	{
		string cursorTargetPath = GetCursorTargetPath(fileName);
		if (cursorTargetPath == null || !File.Exists(cursorTargetPath))
		{
			return Visibility.Visible;
		}
		return Visibility.Collapsed;
	}

	private Visibility GetCursorDeleteVisibility(string fileName)
	{
		string cursorTargetPath = GetCursorTargetPath(fileName);
		if (cursorTargetPath == null || !File.Exists(cursorTargetPath))
		{
			return Visibility.Collapsed;
		}
		return Visibility.Visible;
	}

	private void NotifyCursorVisibilities()
	{
		OnPropertyChanged("AddShiftlockCursorVisibility");
		OnPropertyChanged("DeleteShiftlockCursorVisibility");
		OnPropertyChanged("AddArrowCursorVisibility");
		OnPropertyChanged("DeleteArrowCursorVisibility");
		OnPropertyChanged("AddArrowFarCursorVisibility");
		OnPropertyChanged("DeleteArrowFarCursorVisibility");
		OnPropertyChanged("AddIBeamCursorVisibility");
		OnPropertyChanged("DeleteIBeamCursorVisibility");
	}

	public string ExplorerStatusMessage
	{
		get
		{
			return _explorerStatusMessage;
		}
		private set
		{
			if (_explorerStatusMessage == value)
				return;
			_explorerStatusMessage = value;
			OnPropertyChanged("ExplorerStatusMessage");
			OnPropertyChanged("ExplorerHasStatus");
		}
	}

	public bool ExplorerHasStatus => !string.IsNullOrEmpty(_explorerStatusMessage);

	private string ResolveRobloxPlayerDir(bool forceRefresh = false)
	{
		if (!forceRefresh && _robloxPlayerDirCache != null && Directory.Exists(_robloxPlayerDirCache))
			return _robloxPlayerDirCache;
		_robloxPlayerDirCache = GetRobloxPlayerDir();
		return _robloxPlayerDirCache;
	}

	private string GetRobloxPlayerDir()
	{
		RobloxPlayerData playerData = new RobloxPlayerData();
		string versionsRoot = Path.GetFullPath(playerData.VersionsRoot);
		if (Voidstrap.AppData.CommonAppData.IsVersionGuidValid(playerData.State.VersionGuid))
		{
			string text = playerData.Directory;
			if (File.Exists(Path.Combine(text, "RobloxPlayerBeta.exe")))
			{
				return text;
			}
		}
		if (Directory.Exists(versionsRoot))
		{
			try
			{
				string[] directories = Directory.GetDirectories(versionsRoot);
				foreach (string text2 in directories)
				{
					if (Voidstrap.AppData.CommonAppData.IsVersionGuidValid(Path.GetFileName(text2)) && File.Exists(Path.Combine(text2, "RobloxPlayerBeta.exe")))
					{
						return text2;
					}
				}
			}
			catch
			{
			}
		}
		return versionsRoot;
	}

	public void ShowFileDetails()
	{
		if (SelectedModFile != null)
		{
			Frontend.ShowMessageBox($"Name: {SelectedModFile.Name}\nType: {SelectedModFile.Type}\nSize: {(SelectedModFile.IsFolder ? "N/A" : SelectedModFile.SizeText)}\nModified: {SelectedModFile.ModifiedTime}\nStatus: {SelectedModFile.Status}\nFull Path: {SelectedModFile.FullPath}", MessageBoxImage.Asterisk);
		}
	}

	public void ReplaceFile()
	{
		if (SelectedModFile == null || SelectedModFile.IsFolder)
		{
			return;
		}
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Select replacement for " + SelectedModFile.Name,
			Filter = "Compatible files|*.png;*.jpg;*.jpeg;*.bmp;*.ogg;*.mp3;*.wav;*.mesh;*.rbxm;*.rbxmx;*.json|All files|*.*"
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		string text = Path.Combine(Paths.Mods, SelectedModFile.RelativePath);
		string directoryName = Path.GetDirectoryName(text);
		try
		{
			if (directoryName != null)
			{
				Directory.CreateDirectory(directoryName);
			}
			string text2 = Path.GetExtension(SelectedModFile.FullPath).ToLower();
			string text3 = Path.GetExtension(openFileDialog.FileName).ToLower();
			if (SelectedModFile.IsImage && text2 != text3)
			{
				using Image image = Image.FromFile(openFileDialog.FileName);
				ImageFormat imageFormat;
				switch (text2)
				{
				case ".png":
					imageFormat = ImageFormat.Png;
					break;
				case ".jpg":
				case ".jpeg":
					imageFormat = ImageFormat.Jpeg;
					break;
				case ".bmp":
					imageFormat = ImageFormat.Bmp;
					break;
				default:
					imageFormat = ImageFormat.Png;
					break;
				}
				ImageFormat format = imageFormat;
				image.Save(text, format);
			}
			else
			{
				File.Copy(openFileDialog.FileName, text, overwrite: true);
			}
			Frontend.ShowMessageBox("Successfully replaced " + SelectedModFile.Name + " in your Mods folder!", MessageBoxImage.Asterisk);
			RefreshModFiles();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to replace file: " + ex.Message, MessageBoxImage.Hand);
		}
	}

	public void ExportFile()
	{
		if (SelectedModFile == null || SelectedModFile.IsFolder)
		{
			return;
		}
		Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
		{
			Title = "Export " + SelectedModFile.Name,
			FileName = SelectedModFile.Name,
			Filter = "Original Type|*." + SelectedModFile.Type.ToLower() + "|All files|*.*"
		};
		if (saveFileDialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			File.Copy(SelectedModFile.FullPath, saveFileDialog.FileName, overwrite: true);
			Frontend.ShowMessageBox("Exported to " + Path.GetFileName(saveFileDialog.FileName), MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Export failed: " + ex.Message, MessageBoxImage.Hand);
		}
	}

	public void RecolorImage()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			Frontend.ShowMessageBox(Strings.Common_NotAvailableOnPlatform, MessageBoxImage.Information);
			return;
		}
		if (SelectedModFile != null && SelectedModFile.IsImage)
		{
			ImageRecolorWindow imageRecolorWindow = new ImageRecolorWindow(SelectedModFile.FullPath, SelectedModFile.RelativePath);
			imageRecolorWindow.Owner = System.Windows.Application.Current.MainWindow;
			imageRecolorWindow.ShowDialog();
			RefreshModFiles();
		}
	}

	public void AdjustImage()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			Frontend.ShowMessageBox(Strings.Common_NotAvailableOnPlatform, MessageBoxImage.Information);
			return;
		}
		if (SelectedModFile != null && SelectedModFile.IsImage)
		{
			ImageAdjustWindow imageAdjustWindow = new ImageAdjustWindow(SelectedModFile.FullPath, SelectedModFile.RelativePath);
			imageAdjustWindow.Owner = System.Windows.Application.Current.MainWindow;
			imageAdjustWindow.ShowDialog();
			RefreshModFiles();
		}
	}

	public void RefreshModFiles()
	{
		try
		{
			_explorerCts?.Cancel();
			_explorerCts?.Dispose();
		}
		catch
		{
		}
		_explorerCts = new CancellationTokenSource();
		_ = RefreshModFilesAsync(_explorerCts.Token);
	}

	private async Task RefreshModFilesAsync(CancellationToken token)
	{
		string root = ResolveRobloxPlayerDir();
		string path = CurrentExplorerPath;
		if (!IsSafeExplorerPath(path))
		{
			path = root;
			_currentExplorerPath = root;
			OnPropertyChanged("CurrentExplorerPath");
			OnPropertyChanged("ExplorerPathDisplay");
		}

		if (!Directory.Exists(path))
		{
			root = ResolveRobloxPlayerDir(forceRefresh: true);
			if (Directory.Exists(root))
			{
				_currentExplorerPath = root;
				OnPropertyChanged("CurrentExplorerPath");
				OnPropertyChanged("ExplorerPathDisplay");
				path = root;
			}
		}

		string filter = _explorerSearchText ?? "";
		List<ModFile> built;
		string status;

		try
		{
			built = await Task.Run(() => BuildModFileList(path, root, filter, token), token).ConfigureAwait(true);
			status = built.Count != 0
				? ""
				: (filter.Length != 0
					? "Nothing here matches your search."
					: "This folder is empty.");
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModsViewModel::RefreshModFiles", "Explorer scan failed: " + ex.Message);
			built = new List<ModFile>();
			status = Directory.Exists(path)
				? "This folder could not be read."
				: "The Roblox install folder was not found. Launch Roblox once through Voidstrap, then refresh.";
		}

		if (token.IsCancellationRequested)
			return;

		ModFiles.Clear();
		foreach (ModFile item in built)
			ModFiles.Add(item);
		ExplorerStatusMessage = status;
	}

	private List<ModFile> BuildModFileList(string path, string robloxRoot, string filter, CancellationToken token)
	{
		List<ModFile> built = new List<ModFile>();
		if (!Directory.Exists(path) || !IsSafeExplorerPath(path))
			return built;

		foreach (string directory in Directory.EnumerateDirectories(path))
		{
			token.ThrowIfCancellationRequested();
			if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
				continue;
			string name = Path.GetFileName(directory);
			if (HiddenExplorerItems.Contains(name, StringComparer.OrdinalIgnoreCase))
				continue;
			if (filter.Length != 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
				continue;
			ModFile entry = CreateModFile(directory, isFolder: true);
			UpdateModStatus(entry, robloxRoot);
			built.Add(entry);
		}

		foreach (string file in Directory.EnumerateFiles(path))
		{
			token.ThrowIfCancellationRequested();
			if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
				continue;
			string name = Path.GetFileName(file);
			if (HiddenExplorerItems.Contains(name, StringComparer.OrdinalIgnoreCase))
				continue;
			if (filter.Length != 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
				continue;
			ModFile entry = CreateModFile(file, isFolder: false);
			UpdateModStatus(entry, robloxRoot);
			built.Add(entry);
		}

		return built;
	}

	private void UpdateModStatus(ModFile file, string robloxPlayerDir)
	{
		if (file.IsFolder)
		{
			file.Status = "";
			return;
		}
		try
		{
			string relativePath = Path.GetRelativePath(robloxPlayerDir, file.FullPath);
			string path = Path.Combine(Paths.Mods, relativePath);
			file.Status = (File.Exists(path) ? "Modded" : "Original");
		}
		catch
		{
			file.Status = "Unknown";
		}
	}

	private ModFile CreateModFile(string path, bool isFolder)
	{
		FileSystemInfo fileSystemInfo = (isFolder ? ((FileSystemInfo)new DirectoryInfo(path)) : ((FileSystemInfo)new FileInfo(path)));
		string robloxPlayerDir = ResolveRobloxPlayerDir();
		string relativePath = "";
		try
		{
			relativePath = Path.GetRelativePath(robloxPlayerDir, path);
		}
		catch
		{
		}
		return new ModFile
		{
			Name = (isFolder ? fileSystemInfo.Name : Path.GetFileName(path)),
			FullPath = path,
			RelativePath = relativePath,
			IsFolder = isFolder,
			Type = (isFolder ? "Folder" : Path.GetExtension(path).ToUpper().TrimStart('.')),
			SizeText = (isFolder ? "" : Utilities.FormatBytes(((FileInfo)fileSystemInfo).Length)),
			ModifiedTime = fileSystemInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
		};
	}

	private void OpenModFileFolder()
	{
		string text = SelectedModFile?.FullPath ?? CurrentExplorerPath;
		if (SelectedModFile != null && !SelectedModFile.IsFolder)
		{
			text = Path.GetDirectoryName(text) ?? CurrentExplorerPath;
		}
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = text,
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::OpenModFileFolder", ex);
		}
	}

	private void DeleteModFile()
	{
		if (SelectedModFile == null || !IsSafeExplorerPath(SelectedModFile.FullPath) || Frontend.ShowMessageBox("Are you sure you want to delete " + SelectedModFile.Name + "?", MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
		{
			return;
		}
		try
		{
			if (SelectedModFile.IsFolder)
			{
				Directory.Delete(SelectedModFile.FullPath, recursive: true);
			}
			else
			{
				File.Delete(SelectedModFile.FullPath);
			}
			RefreshModFiles();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Error deleting file: " + ex.Message, MessageBoxImage.Hand);
		}
	}

	private bool IsSafeExplorerPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			string root = Path.GetFullPath(ResolveRobloxPlayerDir()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string relative = Path.GetRelativePath(root, candidate);
			string current = root;
			foreach (string part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
			{
				current = Path.Combine(current, part);
				if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
				{
					return false;
				}
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void OpenFolder(string dir)
	{
		try
		{
			Directory.CreateDirectory(dir);
			Process.Start(new ProcessStartInfo
			{
				FileName = dir,
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}

	public void StartCaptureBrowser()
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Expected O, but got Unknown
		if (!_captureBrowserActive)
		{
			_captureBrowserActive = true;
			CapturedAsset.ShowNames = _showAssetNames;
			RunScanAndRebuild(initial: true);
			_captureTimer = new DispatcherTimer((DispatcherPriority)4)
			{
				Interval = TimeSpan.FromSeconds(4L)
			};
			_captureTimer.Tick += CaptureTimer_Tick;
			_captureTimer.Start();
		}
	}

	private void CaptureTimer_Tick(object? sender, EventArgs e)
	{
		RunScanAndRebuild(initial: false);
	}

	private static int _scanBusy;

	private string _lastCaptureSig = "";

	private void RunScanAndRebuild(bool initial)
	{
		if (System.Threading.Interlocked.CompareExchange(ref _scanBusy, 1, 0) != 0)
		{
			return;
		}
		Task.Run(delegate
		{
			try
			{
				if (initial)
				{
					try
					{
						AssetCaptureStore.EnsureLoadedFromDisk();
					}
					catch
					{
					}
				}
				ScanCache();
			}
			catch
			{
			}
			finally
			{
				System.Threading.Interlocked.Exchange(ref _scanBusy, 0);
			}
			try
			{
				((DispatcherObject)System.Windows.Application.Current).Dispatcher.BeginInvoke((DispatcherPriority)4, (Delegate)new Action(delegate
				{
					if (_captureBrowserActive)
					{
						RebuildCaptures();
					}
				}));
			}
			catch
			{
			}
		});
	}

	public void StopCaptureBrowser()
	{
		_captureBrowserActive = false;
		DispatcherTimer? captureTimer = _captureTimer;
		if (captureTimer != null)
		{
			captureTimer.Stop();
			captureTimer.Tick -= CaptureTimer_Tick;
		}
		_captureTimer = null;
	}

	private static void ScanCache()
	{
		try
		{
			AssetCaptureStore.ScanRobloxCache(20000);
		}
		catch
		{
		}
		try
		{
			AssetCaptureStore.ScanRobloxLog(20000);
		}
		catch
		{
		}
		try
		{
			AssetCaptureStore.ScanAvatarAssetsAsync();
		}
		catch
		{
		}
		try
		{
			AssetCaptureStore.ResolveHashesAsync(150);
		}
		catch
		{
		}
		try
		{
			AssetCaptureStore.PrefetchUnsizedAsync(6);
		}
		catch
		{
		}
		try
		{
			Task.Run(delegate
			{
				AssetCaptureStore.ScanModelReferences(4);
			});
		}
		catch
		{
		}
		try
		{
			Task.Run(delegate
			{
				AssetCaptureStore.ScanRecentLogs(2);
			});
		}
		catch
		{
		}
	}

	private bool PassesFilter(CapturedAsset a)
	{
		if (CacheFilter != "All" && !string.Equals(a.Category, CacheFilter, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(_captureSearch))
		{
			string value = _captureSearch.Trim();
			string assetId = a.AssetId;
			if (assetId == null || !assetId.Contains(value, StringComparison.OrdinalIgnoreCase))
			{
				string hash = a.Hash;
				if (hash == null || !hash.Contains(value, StringComparison.OrdinalIgnoreCase))
				{
					string resolvedName = a.ResolvedName;
					if (resolvedName == null || !resolvedName.Contains(value, StringComparison.OrdinalIgnoreCase))
					{
						string creator = a.Creator;
						if (creator == null || !creator.Contains(value, StringComparison.OrdinalIgnoreCase))
						{
							string type = a.Type;
							if (type == null || !type.Contains(value, StringComparison.OrdinalIgnoreCase))
							{
								return false;
							}
						}
					}
				}
			}
		}
		return true;
	}

	private void RebuildCaptures()
	{
		try
		{
			AssetCaptureStore.ApplyLinks();
		}
		catch
		{
		}
		List<CapturedAsset> list = AssetCaptureStore.Snapshot().Where(PassesFilter).Take(5000)
			.ToList();
		string sig = list.Count + "|" + ((list.Count > 0) ? ((list[0].Hash ?? "") + (list[list.Count - 1].Hash ?? "")) : "") + "|" + CacheFilter + "|" + _captureSearch;
		if (sig == _lastCaptureSig)
		{
			return;
		}
		_lastCaptureSig = sig;
		CapturedAssets.Clear();
		foreach (CapturedAsset item in list)
		{
			CapturedAssets.Add(item);
		}
		UpdateCaptureStats();
		if (_showAssetNames)
		{
			ResolveNamesAsync();
		}
	}

	private void UpdateCaptureStats()
	{
		long num = CapturedAssets.Sum((CapturedAsset a) => a.Size);
		string value = ((num >= 1048576) ? $"{(double)num / 1048576.0:0.0} MB" : ((num >= 1024) ? $"{(double)num / 1024.0:0} KB" : $"{num} B"));
		CaptureStatsText = $"Total: {CapturedAssets.Count} assets      Size: {value}";
	}

	private void CopyCapturedId(CapturedAsset? asset)
	{
		if (asset == null)
		{
			return;
		}
		string text = ((!string.IsNullOrEmpty(asset.AssetId)) ? asset.AssetId : asset.Hash);
		try
		{
			System.Windows.Clipboard.SetText(text);
		}
		catch
		{
		}
	}

	private void ExportScraped(CapturedAsset? asset)
	{
		if (asset == null)
		{
			return;
		}
		byte[] array = AssetCaptureStore.ReadContent(asset);
		if (array == null || array.Length == 0)
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(ExportDir);
			string text = ((!string.IsNullOrEmpty(asset.AssetId)) ? asset.AssetId : asset.Hash);
			if (KtxDecoder.IsKtx(array))
			{
				BitmapSource bitmapSource = KtxDecoder.DecodeToBitmap(array);
				if (bitmapSource != null)
				{
					string path = Path.Combine(ExportDir, text + ".png");
					PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder
					{
						Frames = { BitmapFrame.Create(bitmapSource) }
					};
					using (FileStream stream = File.Create(path))
					{
						pngBitmapEncoder.Save(stream);
					}
					AssetWarpStatus = "Exported " + text + ".png";
					return;
				}
			}
			if (asset.IsMesh || string.Equals(asset.Category, "Mesh", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					MeshModel meshModel = MeshParser.Parse(array);
					if (meshModel.Positions.Count > 0 && meshModel.Indices.Count >= 3)
					{
						File.WriteAllText(Path.Combine(ExportDir, text + ".obj"), MeshParser.ToObj(meshModel));
						return;
					}
				}
				catch
				{
				}
			}
			string text2 = text + asset.Extension;
			File.WriteAllBytes(Path.Combine(ExportDir, text2), array);
		}
		catch
		{
		}
	}

	public async Task<byte[]?> GetCaptureContentAsync(CapturedAsset asset)
	{
		return await AssetCaptureStore.GetContentAsync(asset);
	}

	private static string MapAssetCategory(string? devType)
	{
		switch (devType?.ToLowerInvariant())
		{
		case "pants":
		case "shirt":
		case "decal":
		case "image":
		case "face":
		case "tshirt":
			return "Image";
		case "audio":
			return "Audio";
		case "mesh":
		case "meshpart":
		case "solidmodel":
			return "Mesh";
		case "animation":
			return "Animation";
		case "model":
		case "lua":
		case "package":
			return "Model";
		default:
			return "Other";
		}
	}

	private async Task ResolveNamesAsync()
	{
		if (_resolving || DateTime.UtcNow < _nameCooldownUntil)
		{
			return;
		}
		_resolving = true;
		try
		{
			List<string> list = (from a in CapturedAssets
				where long.TryParse(a.AssetId, out var result) && result > 0 && string.IsNullOrEmpty(a.ResolvedName) && !_attemptedNames.Contains(a.AssetId)
				select a.AssetId).Distinct<string>(StringComparer.Ordinal).Take(50).ToList();
			if (list.Count == 0)
			{
				return;
			}
			foreach (string item in list)
			{
				_attemptedNames.Add(item);
			}
			Dictionary<string, RobloxCookie.AssetMeta> dictionary = await RobloxCookie.ResolveAssetNamesAsync(list).ConfigureAwait(continueOnCapturedContext: true);
			if (dictionary.Count == 0)
			{
				_nameCooldownUntil = DateTime.UtcNow.AddSeconds(20.0);
				return;
			}
			foreach (CapturedAsset capturedAsset in CapturedAssets)
			{
				if (!string.IsNullOrEmpty(capturedAsset.AssetId) && dictionary.TryGetValue(capturedAsset.AssetId, out var value))
				{
					if (string.IsNullOrEmpty(capturedAsset.ResolvedName) && !string.IsNullOrEmpty(value.Name))
					{
						capturedAsset.ResolvedName = value.Name;
					}
					if (string.IsNullOrEmpty(capturedAsset.Creator) && !string.IsNullOrEmpty(value.CreatorName))
					{
						capturedAsset.Creator = value.CreatorName;
					}
					if (!string.IsNullOrEmpty(value.Type) && (capturedAsset.Type == "Asset" || capturedAsset.Type == "Other" || capturedAsset.Category == "Pending"))
					{
						capturedAsset.Type = value.Type;
						capturedAsset.Category = MapAssetCategory(value.Type);
					}
				}
			}
		}
		finally
		{
			_resolving = false;
		}
	}
}
