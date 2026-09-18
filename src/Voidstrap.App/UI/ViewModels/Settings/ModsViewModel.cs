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
using SixLabors.ImageSharp.Processing;
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

public partial class ModsViewModel : NotifyPropertyChangedViewModel
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

	private CrosshairShape _selectedShape;

	private string _cursorColorHex = "#00FF00";

	private string _cursorOutlineColorHex = "#000000";

	private int _cursorSize = 20;

	private int _crosshairThickness = 2;

	private int _gap = 4;

	private double _cursorOpacity = 1.0;

	private string _cursorCode = null!;

	private ImageSource? _cursorPreview;

	private bool _useImageCrosshair;

	private string _imageUrl = null!;

	private readonly string _dir = Paths.UserData;

	private readonly string _file;

	private CancellationTokenSource? _deathSoundConversionCts;

	private long _deathSoundConversionGeneration;

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

	public CommunityModsViewModel CommunityMods { get; } = new CommunityModsViewModel();

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
				Voidstrap.Utility.LinuxEffectMapper.RefreshConfiguration();
				OnPropertyChanged(nameof(Saturation));
				OnPropertyChanged(nameof(SaturationDisplay));
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
				Voidstrap.Utility.LinuxEffectMapper.RefreshConfiguration();
				OnPropertyChanged(nameof(Contrast));
				OnPropertyChanged(nameof(ContrastDisplay));
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
				OnPropertyChanged(nameof(ColorTemperature));
				OnPropertyChanged(nameof(ColorTemperatureDisplay));
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

	private bool _classicTopBarBusy;

	private static int _classicTopBarCard;

	private static volatile bool _classicTopBarFinished;

	private static CancellationTokenSource? _classicTopBarCancellation;

	public bool ClassicTopBarEnabled
	{
		get
		{
			return App.Settings.Prop.ClassicTopBarEnabled;
		}
		set
		{
			if (App.Settings.Prop.ClassicTopBarEnabled == value || _classicTopBarBusy)
			{
				return;
			}
			App.Settings.Prop.ClassicTopBarEnabled = value;
			App.Settings.Prop.ClassicTopBarHideAllCoreGui = value;
			OnPropertyChanged(nameof(ClassicTopBarEnabled));
			OnPropertyChanged(nameof(ClassicTopBarHideAllCoreGui));
			OnPropertyChanged(nameof(ClassicTopBarCoreGuiEditable));
			_ = ApplyClassicTopBarAsync(value);
		}
	}

	public bool ClassicTopBarHideAllCoreGui
	{
		get
		{
			return App.Settings.Prop.ClassicTopBarHideAllCoreGui;
		}
		set
		{
			if (App.Settings.Prop.ClassicTopBarHideAllCoreGui == value || !ClassicTopBarCoreGuiEditable)
			{
				return;
			}
			App.Settings.Prop.ClassicTopBarHideAllCoreGui = value;
			OnPropertyChanged(nameof(ClassicTopBarHideAllCoreGui));
			App.Settings.Save();
			_ = ApplyHideCoreGuiAsync(value);
		}
	}

	public bool Ps4ButtonsEnabled
	{
		get
		{
			return App.Settings.Prop.Ps4ButtonsEnabled;
		}
		set
		{
			if (App.Settings.Prop.Ps4ButtonsEnabled == value || _classicTopBarBusy)
			{
				return;
			}
			App.Settings.Prop.Ps4ButtonsEnabled = value;
			OnPropertyChanged(nameof(Ps4ButtonsEnabled));
			App.Settings.Save();
			_ = ApplyPs4ButtonsAsync(value);
		}
	}

	private Task ApplyPs4ButtonsAsync(bool enabled)
	{
		return RunClassicTopBarAsync(
			(progress, token) => Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.SetPs4ButtonsAsync(true, progress, token),
			(progress, token) => Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.SetPs4ButtonsAsync(false, progress, token),
			enabled,
			"The PS4 buttons are installed. Relaunch Roblox to see them.",
			"The PS4 buttons were removed.",
			RollbackPs4Buttons);
	}

	private void RollbackPs4Buttons(bool enabled)
	{
		App.Settings.Prop.Ps4ButtonsEnabled = !enabled;
		OnPropertyChanged(nameof(Ps4ButtonsEnabled));
	}

	public bool ClassicTopBarBusy
	{
		get => _classicTopBarBusy;
		private set
		{
			_classicTopBarBusy = value;
			OnPropertyChanged(nameof(ClassicTopBarBusy));
			OnPropertyChanged(nameof(ClassicTopBarReady));
			OnPropertyChanged(nameof(ClassicTopBarCoreGuiEditable));
		}
	}

	public bool ClassicTopBarReady => !_classicTopBarBusy;

	public bool ClassicTopBarCoreGuiEditable => !_classicTopBarBusy && !App.Settings.Prop.ClassicTopBarEnabled;

	private Task ApplyClassicTopBarAsync(bool enabled)
	{
		return RunClassicTopBarAsync(
			(progress, token) => Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.EnableAsync(progress, token),
			(progress, token) => Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.DisableAsync(token),
			enabled,
			"The classic topbar is ready. Relaunch Roblox to see it.",
			"The classic topbar was turned off and Roblox was put back to normal.",
			RollbackClassicTopBar);
	}

	private Task ApplyHideCoreGuiAsync(bool enabled)
	{
		return RunClassicTopBarAsync(
			(progress, token) => Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.SetHideCoreGuiAsync(true, progress, token),
			(progress, token) => Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.SetHideCoreGuiAsync(false, progress, token),
			enabled,
			"Every CoreGui icon is hidden. Relaunch Roblox to see it.",
			"The Roblox interface art was put back.",
			RollbackHideCoreGui);
	}

	private void RollbackClassicTopBar(bool enabled)
	{
		App.Settings.Prop.ClassicTopBarEnabled = !enabled;
		App.Settings.Prop.ClassicTopBarHideAllCoreGui = !enabled;
		OnPropertyChanged(nameof(ClassicTopBarEnabled));
		OnPropertyChanged(nameof(ClassicTopBarHideAllCoreGui));
		OnPropertyChanged(nameof(ClassicTopBarCoreGuiEditable));
	}

	private void RollbackHideCoreGui(bool enabled)
	{
		App.Settings.Prop.ClassicTopBarHideAllCoreGui = !enabled;
		OnPropertyChanged(nameof(ClassicTopBarHideAllCoreGui));
		OnPropertyChanged(nameof(ClassicTopBarCoreGuiEditable));
	}

	private async Task RunClassicTopBarAsync(
		Func<IProgress<string>, CancellationToken, Task> turnOn,
		Func<IProgress<string>, CancellationToken, Task> turnOff,
		bool enabled,
		string enabledMessage,
		string disabledMessage,
		Action<bool> rollback)
	{
		ClassicTopBarBusy = true;
		_classicTopBarFinished = false;
		CancellationTokenSource cancellation = new CancellationTokenSource();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _classicTopBarCancellation, cancellation);
		try
		{
			previous?.Cancel();
			previous?.Dispose();
		}
		catch (ObjectDisposedException)
		{
		}
		Voidstrap.UI.Elements.Settings.Pages.ExtensionViewModel.RegisterCancelAction(CancelClassicTopBar);
		Progress<string> progress = new Progress<string>(OnClassicTopBarProgress);
		CancellationToken token = cancellation.Token;
		try
		{
			Func<IProgress<string>, CancellationToken, Task> work = enabled ? turnOn : turnOff;
			await Task.Run(() => work(progress, token), token).ConfigureAwait(true);
			ReportClassicTopBar(enabled ? enabledMessage : disabledMessage, 1.0, true);
			App.Settings.Save();
		}
		catch (OperationCanceledException)
		{
			rollback(enabled);
			ReportClassicTopBar("The change was cancelled.", 1.0, true);
		}
		catch (Exception ex)
		{
			rollback(enabled);
			ReportClassicTopBar("It could not be changed: " + ex.Message, 1.0, true);
			App.Logger.WriteLine("ModsViewModel", "The classic topbar change failed: " + ex.Message);
		}
		finally
		{
			ClassicTopBarBusy = false;
			Voidstrap.UI.Elements.Settings.Pages.ExtensionViewModel.UnregisterCancelAction(CancelClassicTopBar);
			if (ReferenceEquals(Volatile.Read(ref _classicTopBarCancellation), cancellation))
			{
				Interlocked.Exchange(ref _classicTopBarCancellation, null);
			}
			cancellation.Dispose();
			Voidstrap.UI.Elements.ClassicTopBar.ClassicTopBarOverlay.Reconcile();
			RefreshManagedModsCommand.Execute(null);
		}
	}

	private void OnClassicTopBarProgress(string message)
	{
		if (_classicTopBarFinished)
		{
			return;
		}
		ReportClassicTopBar(message, -1.0, false);
	}

	private static void CancelClassicTopBar()
	{
		try
		{
			Volatile.Read(ref _classicTopBarCancellation)?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private static void ReportClassicTopBar(string message, double fraction, bool final)
	{
		if (final)
		{
			_classicTopBarFinished = true;
		}
		int generation = Interlocked.Increment(ref _classicTopBarCard);
		Voidstrap.UI.Elements.Settings.Pages.ExtensionViewModel.ReportProgress(message, fraction, true);
		if (final)
		{
			_ = HideClassicTopBarCardAsync(generation);
		}
	}

	private static async Task HideClassicTopBarCardAsync(int generation)
	{
		await Task.Delay(6000).ConfigureAwait(true);
		if (Volatile.Read(ref _classicTopBarCard) == generation)
		{
			Voidstrap.UI.Elements.Settings.Pages.ExtensionViewModel.ReportProgress("", -1.0, false);
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
				OnPropertyChanged(nameof(ColorBlindnessEnabled));
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
				OnPropertyChanged(nameof(ColorBlindnessType));
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
				OnPropertyChanged(nameof(ColorBlindnessSeverity));
				OnPropertyChanged(nameof(ColorBlindnessSeverityDisplay));
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
				OnPropertyChanged(nameof(ColorBlindnessSimulate));
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
				OnPropertyChanged(nameof(SelectedSkyboxPack));
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

	public ICommand EditManagedModCommand { get; }

	public ICommand RemoveManagedModCommand { get; }

	public ICommand ToggleManagedModCommand { get; }

	public ICommand CopyManagedModIdCommand { get; }

	public ICommand ViewModPackCommand { get; }

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
		CancellationToken token = cancellation.Token;
		_fontPreviewCts = cancellation;
		_ = LoadSelectedFontPreviewAsync(selected, cancellation, token);
	}

	private async Task LoadSelectedFontPreviewAsync(GoogleFontOption selected, CancellationTokenSource cancellation, CancellationToken token)
	{
		try
		{
			await Task.Delay(220, token);
			string path = await GoogleFontsService.DownloadAsync(selected, token);
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
		catch (OperationCanceledException) when (token.IsCancellationRequested)
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
			OnPropertyChanged(nameof(CustomFontScale));

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
			OnPropertyChanged(nameof(CustomDeathSoundVolume));

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

	public CursorSettingsViewModel CursorSettings { get; } = new CursorSettingsViewModel();

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
			{
				string message = Voidstrap.Utility.Platform.IsLinux
					? "Sober dark mode is required. Voidstrap will use X11 or XWayland the next time Sober launches."
					: "Roblox light mode is not supported. Make sure Roblox dark mode is selected.";
				Frontend.ShowMessageBox(message, MessageBoxImage.Warning);
			}
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
				_homepagePreviewMessage = Voidstrap.Utility.Platform.IsLinux ? "This video could not be previewed." : "Video previews are not supported on Linux yet.";
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
				{
					still = BuildStillPreview(path);
					if (still == null)
						message = DescribeUnpreviewableFile(path);
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ModsViewModel::HomepageMediaPreview", "The preview could not be built: " + ex.Message);
				message = DescribeUnpreviewableFile(path);
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
				if (!Voidstrap.Utility.Platform.IsWindows)
					return;

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

	private static System.Windows.Media.ImageSource? BuildStillPreview(string path)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			return Voidstrap.Utility.SafeImaging.FromFile(path, HomepagePreviewDecodeWidth);
		}

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

	private static string DescribeUnpreviewableFile(string path)
	{
		try
		{
			FileInfo info = new(path);
			string size = info.Length >= 1024L * 1024
				? (info.Length / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
				: Math.Max(1L, info.Length / 1024L).ToString(CultureInfo.InvariantCulture) + " KB";
			string dimensions = Voidstrap.Utility.SafeImaging.TryReadDimensions(path, out int width, out int height)
				? width.ToString(CultureInfo.InvariantCulture) + " by " + height.ToString(CultureInfo.InvariantCulture) + ", "
				: string.Empty;
			return info.Name + " (" + dimensions + size + ")";
		}
		catch (Exception)
		{
			return "This file could not be previewed.";
		}
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

	public string[] MotionBlurStrengthNames => Voidstrap.Integrations.MotionBlur.MotionBlurSettings.StrengthNames;

	public int MotionBlurStrengthIndex
	{
		get
		{
			return Voidstrap.Integrations.MotionBlur.MotionBlurSettings.StrengthIndex;
		}
		set
		{
			if (value < 0)
				return;
			Voidstrap.Integrations.MotionBlur.MotionBlurManager.SetStrength(value);
			OnPropertyChanged(nameof(MotionBlurStrengthIndex));
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
				OnPropertyChanged(nameof(Brightness));
				OnPropertyChanged(nameof(BrightnessDisplay));
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
			if (App.Settings.Prop.Crosshair == value)
				return;
			App.Settings.Prop.Crosshair = value;
			OnPropertyChanged(nameof(Crosshair));
			App.Settings.SaveDeferred();
			Voidstrap.Integrations.Overlays.OverlayHub.RefreshCrosshair();
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
			OnPropertyChanged(nameof(CurrentTimeDisplay));
		}
	}

	public FontModPresetTask TextFontTask { get; } = new FontModPresetTask();

	public Visibility ChooseCustomDeathSoundVisibility => GetVisibility(Path.Combine(Paths.Mods, "Content", "sounds"), DeathSoundFiles, checkExist: false);

	public Visibility DeleteCustomDeathSoundVisibility => GetVisibility(Path.Combine(Paths.Mods, "Content", "sounds"), DeathSoundFiles, checkExist: true);

	public ObservableCollection<GradientStopViewModel> GradientStops { get; set; } = new ObservableCollection<GradientStopViewModel>();

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
			if (SetProperty(ref _selectedShape, value, nameof(SelectedShape)))
			{
				UseImageCrosshair = value == CrosshairShape.Image;
				ApplyCrosshairChange();
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
			if (SetProperty(ref _useImageCrosshair, value, nameof(UseImageCrosshair)))
			{
				ApplyCrosshairChange();
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
			if (SetProperty(ref _imageUrl, value, nameof(ImageUrl)))
			{
				ApplyCrosshairChange();
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
			SetProperty(ref _cursorColorHex, value, nameof(CursorColorHex));
			ApplyCrosshairChange();
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
			SetProperty(ref _cursorOutlineColorHex, value, nameof(CursorOutlineColorHex));
			ApplyCrosshairChange();
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
			SetProperty(ref _cursorSize, value, nameof(CursorSize));
			ApplyCrosshairChange();
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
			SetProperty(ref _crosshairThickness, value, nameof(CrosshairThickness));
			ApplyCrosshairChange();
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
			SetProperty(ref _gap, value, nameof(Gap));
			ApplyCrosshairChange();
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
			SetProperty(ref _cursorOpacity, value, nameof(CursorOpacity));
			ApplyCrosshairChange();
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
			SetProperty(ref _cursorCode, value, nameof(CursorCode));
		}
	}

	public ImageSource? CursorPreview
	{
		get
		{
			return _cursorPreview;
		}
		set
		{
			SetProperty(ref _cursorPreview, value, nameof(CursorPreview));
		}
	}

	public bool ModExplorerVisible
	{
		get
		{
			return _modExplorerVisible;
		}
		set
		{
			_modExplorerVisible = value;
			OnPropertyChanged(nameof(ModExplorerVisible));
			OnPropertyChanged(nameof(MainContentVisibility));
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
			if (ReferenceEquals(_selectedModFile, value))
				return;
			_selectedModFile = value;
			OnPropertyChanged(nameof(SelectedModFile));
			OnPropertyChanged(nameof(ExplorerHasSelection));
			OnPropertyChanged(nameof(ExplorerSelectedFile));
			OnPropertyChanged(nameof(ExplorerSelectedImage));
		}
	}

	public bool ExplorerHasSelection => SelectedModFile is not null;

	public bool ExplorerSelectedFile => SelectedModFile is { IsFolder: false };

	public bool ExplorerSelectedImage => SelectedModFile is { IsImage: true };

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
			OnPropertyChanged(nameof(CurrentExplorerPath));
			OnPropertyChanged(nameof(ExplorerPathDisplay));
			OnPropertyChanged(nameof(ExplorerCanGoBack));
		}
	}

	public string ExplorerPathDisplay
	{
		get
		{
			string robloxPlayerDir = ResolveRobloxPlayerDir();
			try
			{
				string relative = Path.GetRelativePath(robloxPlayerDir, CurrentExplorerPath);
				return relative == "." ? "Roblox" : "Roblox  >  " + relative.Replace(Path.DirectorySeparatorChar.ToString(), "  >  ");
			}
			catch
			{
				return CurrentExplorerPath;
			}
		}
	}

	public bool ExplorerCanGoBack
	{
		get
		{
			string root = ResolveRobloxPlayerDir().TrimEnd(trimChars);
			return !CurrentExplorerPath.TrimEnd(trimChars).Equals(root, StringComparison.OrdinalIgnoreCase);
		}
	}

	public ICommand ToggleModExplorerCommand => new RelayCommand(async delegate
	{
		ModExplorerVisible = !ModExplorerVisible;
		if (ModExplorerVisible)
		{
			await Task.Run(() => Voidstrap.Utility.RobloxInstallCompression.EnsureExtracted(new RobloxPlayerData()));
			CurrentExplorerPath = ResolveRobloxPlayerDir(forceRefresh: true);
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
		string text = ResolveRobloxPlayerDir().TrimEnd(trimChars);
		string text2 = CurrentExplorerPath.TrimEnd(trimChars);
		if (text2.Equals(text, StringComparison.OrdinalIgnoreCase))
			return;
		CurrentExplorerPath = Path.GetDirectoryName(text2) ?? text;
		RefreshModFiles();
	});

	public string ExplorerSearchText
	{
		get
		{
			return _explorerSearchText;
		}
		set
		{
			if (_explorerSearchText == value)
				return;
			_explorerSearchText = value;
			OnPropertyChanged(nameof(ExplorerSearchText));
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
			OnPropertyChanged(nameof(CacheFilter));
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
			OnPropertyChanged(nameof(CaptureSearch));
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
			OnPropertyChanged(nameof(ShowAssetNames));
			foreach (CapturedAsset capturedAsset in CapturedAssets)
			{
				capturedAsset.RaiseLabelChanged();
			}
			if (value)
			{
				_ = ResolveNamesAsync();
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
			OnPropertyChanged(nameof(CaptureStatsText));
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
		if (!Voidstrap.Utility.PlatformShell.TryOpenFolder(Paths.Mods))
		{
			Frontend.ShowMessageBox("The mods folder could not be opened. It is at " + Paths.Mods, MessageBoxImage.Warning);
		}
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
		OnPropertyChanged(nameof(ChooseCustomFontVisibility));
		OnPropertyChanged(nameof(DeleteCustomFontVisibility));
		OnPropertyChanged(nameof(DeleteCustomFontFontName));
		OnPropertyChanged(nameof(DeleteCustomFontFontFamily));
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

	private static async Task<string?> GetPreviewImageUrl(string folder, HttpClient http)
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

	private Dictionary<string, string> GetCustomSkyboxSources()
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
		Voidstrap.Utility.RobloxInstallCompression.EnsureExtracted(new RobloxPlayerData());
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

	private static Visibility GetVisibility(string directory, string[] filenames, bool checkExist)
	{
		bool flag = filenames.Any((string name) => File.Exists(Path.Combine(directory, name)));
		if (!(checkExist ? flag : (!flag)))
		{
			return Visibility.Collapsed;
		}
		return Visibility.Visible;
	}

	private static void RemoveCustomFile(string[] targetFiles, string targetDir, string notFoundMessage, Action? postAction = null)
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
		catch (InvalidDataException ex)
		{
			Frontend.ShowMessageBox("Failed to add death sound:\n" + ex.Message, MessageBoxImage.Warning);
			return;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::AddCustomDeathSound", ex);
			Frontend.ShowMessageBox("Failed to add death sound:\n" + ex.Message, MessageBoxImage.Hand);
			return;
		}

		await ApplyDeathSoundVolumeAsync();

		OnPropertyChanged(nameof(ChooseCustomDeathSoundVisibility));
		OnPropertyChanged(nameof(DeleteCustomDeathSoundVisibility));
		OnPropertyChanged(nameof(CustomDeathSoundVolumeVisibility));
	}

	public void AddCustomDeathSound()
	{
		_ = AddCustomDeathSoundAsync();
	}

	public void RemoveCustomDeathSound()
	{
		CancelDeathSoundConversion();
        RemoveCustomFile(DeathSoundFiles, Path.Combine(Paths.Mods, "Content", "sounds"), "No custom death sound found to remove.", delegate
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

			OnPropertyChanged(nameof(ChooseCustomDeathSoundVisibility));
			OnPropertyChanged(nameof(DeleteCustomDeathSoundVisibility));
			OnPropertyChanged(nameof(CustomDeathSoundVolumeVisibility));
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
			error = await Task.Run(() => Math.Abs(volume - 1.0) < 0.005
				? CopyDeathSoundAtFullVolume()
				: AudioGain.TryApplyGain(Paths.CustomDeathSoundSource, Paths.CustomDeathSound, volume, cancellation.Token, out string conversionError) ? string.Empty : conversionError);
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

	private static string CopyDeathSoundAtFullVolume()
	{
		try
		{
			Filesystem.CopyWritableFile(Paths.CustomDeathSoundSource, Paths.CustomDeathSound);
			App.Logger.WriteLine("ModsViewModel::ApplyDeathSoundVolume", "Death sound set at 100 percent volume, no conversion needed");
			return string.Empty;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return ex.Message;
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
		EditManagedModCommand = new AsyncRelayCommand<ManagedModItem>(EditManagedModAsync);
		RemoveManagedModCommand = new AsyncRelayCommand<ManagedModItem>(RemoveManagedModAsync);
		ToggleManagedModCommand = new AsyncRelayCommand<ManagedModItem>(ToggleManagedModAsync);
		CopyManagedModIdCommand = new RelayCommand<ManagedModItem>(CopyManagedModId);
		ViewModPackCommand = new AsyncRelayCommand<ManagedModItem>(ViewModPackAsync);
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
		if (dialog.ShowOwnedDialog() == true)
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
		if (dialog.ShowOwnedDialog() == true)
			HomepageBackgroundOverlayGradientColor = $"#{dialog.SelectedColor.R:X2}{dialog.SelectedColor.G:X2}{dialog.SelectedColor.B:X2}";
	}

	private static string NormalizeHomepageColor(string? value)
	{
		string text = (value ?? "").Trim();
		if (HexColorPattern.IsMatch(text))
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

	private const long CatalogRefreshMilliseconds = 600000;

	private long _catalogsLoadedAt;

	public async Task InitializeAsync()
	{
		long now = Environment.TickCount64;
		if (_catalogsLoadedAt != 0 && now - _catalogsLoadedAt < CatalogRefreshMilliseconds)
		{
			await LoadManagedModsAsync();
			return;
		}
		_catalogsLoadedAt = now;
		await Task.WhenAll(LoadModsAsync(), LoadManagedModsAsync(), LoadSkyboxPacksFromGithub(), LoadGoogleFontsAsync());
	}

	public void CancelTransientOperations()
	{
		if (FontManagerBusy || SkyboxManagerBusy)
			_catalogsLoadedAt = 0;
		CancellationTokenSource? explorer = Interlocked.Exchange(ref _explorerCts, null);
		explorer?.Cancel();
		explorer?.Dispose();
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
		CommunityMods.CancelTransientOperations();
	}

	private async Task LoadManagedModsAsync()
	{
		await _managedModsLoadGate.WaitAsync();
		ManagedModsBusy = true;
		try
		{
			ManagedModItem[] items = await Task.Run(() =>
			{
				IReadOnlyList<ManagedModLibraryEntry> library = ManagedModStore.ScanLibrary();
				Dictionary<string, int> pathCounts = new(StringComparer.OrdinalIgnoreCase);
				try
				{
					if (Directory.Exists(Paths.Mods))
					{
						foreach (string file in Directory.EnumerateFiles(Paths.Mods, "*", SearchOption.AllDirectories))
						{
							string relative = Path.GetRelativePath(Paths.Mods, file);
							if (!IsConflictExempt(relative))
								pathCounts[relative] = pathCounts.GetValueOrDefault(relative) + 1;
						}
					}
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("ModsViewModel::LoadManagedMods", "Could not compare the standard mod folder: " + ex.Message);
				}
				Dictionary<string, ManagedModLibraryEntry> owners = new(StringComparer.OrdinalIgnoreCase);
				foreach (ManagedModLibraryEntry entry in library.Where(entry => entry.Record.Enabled).OrderByDescending(entry => entry.AppliedOnTop))
				{
					foreach (string relative in entry.RelativePaths)
					{
						if (IsConflictExempt(relative))
							continue;
						pathCounts[relative] = pathCounts.GetValueOrDefault(relative) + 1;
						owners.TryAdd(relative, entry);
					}
				}
				return library.Select(entry =>
				{
					int conflicts = 0;
					int overridden = 0;
					if (entry.Record.Enabled)
					{
						foreach (string path in entry.RelativePaths)
						{
							if (IsConflictExempt(path) || pathCounts.GetValueOrDefault(path) <= 1)
								continue;
							conflicts++;
							if (!ReferenceEquals(owners[path], entry))
								overridden++;
						}
					}
					bool editable = ExternalModConfigs.IsExternal(entry.Record.Id) || Voidstrap.Integrations.CommunityMods.ModVariantStore.HasSlots(entry.Record.Id);
					return new ManagedModItem(entry.Record.Id, entry.Record.Name, entry.Record.Enabled, entry.Record.CreatedUtc, entry.FileCount, entry.TotalBytes, conflicts, entry.Failure, entry.Pack, editable, overridden, entry.AppliedOnTop);
				}).ToArray();
			});
			MergeManagedMods(items);
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

	private static bool IsConflictExempt(string relative)
	{
		return relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(relative, "README.txt", StringComparison.OrdinalIgnoreCase);
	}

	private void MergeManagedMods(IReadOnlyList<ManagedModItem> fresh)
	{
		Dictionary<string, ManagedModItem> existing = new(StringComparer.OrdinalIgnoreCase);
		foreach (ManagedModItem item in _allManagedMods)
			existing[item.Id] = item;
		List<ManagedModItem> merged = new(fresh.Count);
		foreach (ManagedModItem item in fresh)
		{
			if (existing.TryGetValue(item.Id, out ManagedModItem? current))
			{
				current.Apply(item);
				merged.Add(current);
			}
			else
			{
				merged.Add(item);
			}
		}
		_allManagedMods.Clear();
		_allManagedMods.AddRange(merged);
	}

	private void ApplyManagedModFilter()
	{
		string query = ManagedModSearchText.Trim();
		List<ManagedModItem> filtered = (string.IsNullOrEmpty(query)
			? _allManagedMods
			: _allManagedMods.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
		bool sameOrder = filtered.Count == ManagedMods.Count;
		for (int index = 0; sameOrder && index < filtered.Count; index++)
			sameOrder = ReferenceEquals(ManagedMods[index], filtered[index]);
		if (!sameOrder)
		{
			ManagedMods.Clear();
			foreach (ManagedModItem item in filtered)
				ManagedMods.Add(item);
		}
		bool searchHasNoResults = ManagedMods.Count == 0 && _allManagedMods.Count > 0 && !string.IsNullOrEmpty(query);
		ManagedModsEmptyTitle = searchHasNoResults ? "No Mods match your search" : "No Mods found";
		ManagedModsEmptyDescription = searchHasNoResults ? "Try a different name or identifier." : "Roblox is better with mods. Add your first mod to create its indexed folder, then place its files inside.";
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

	private async Task EditManagedModAsync(ManagedModItem? item)
	{
		if (item is null)
			return;
		Voidstrap.UI.Elements.Dialogs.ModEditorWindow window = new(item.Id, item.Name, item.Pack)
		{
			Owner = System.Windows.Application.Current?.MainWindow
		};
		window.ShowDialog();
		if (window.Changed)
			await LoadManagedModsAsync();
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
			await Task.Run(() =>
			{
				RestoreAssetCache(item.Id);
				ManagedModStore.Delete(item.Id);
			});
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
		bool enable = !item.Enabled;
		bool saved = false;
		await _managedModsMutationGate.WaitAsync();
		try
		{
			await Task.Run(() =>
			{
				ApplyAssetCacheState(item.Id, enable);
				ExternalModConfigs.SetEnabled(item.Id, enable);
				ManagedModStore.SetEnabled(item.Id, enable);
			});
			saved = true;
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
		if (saved && enable)
		{
			Voidstrap.Integrations.AssetProxy.AssetWarpAutoEnable.EnsureEnabled(allowPrompt: true, userAction: true);
		}
	}

	private static string GetAssetCacheBackupFolder(string id)
	{
		return Path.Combine(ManagedModStore.GetFolder(id), RobloxAssetCache.BackupFolderName);
	}

	private static void RestoreAssetCache(string id)
	{
		try
		{
			string folder = GetAssetCacheBackupFolder(id);
			if (RobloxAssetCache.HasBackups(folder))
				RobloxAssetCache.Restore(folder);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModsViewModel::RestoreAssetCache", "The cached assets could not be restored: " + ex.Message);
		}
	}

	private static void ApplyAssetCacheState(string id, bool enable)
	{
		try
		{
			string folder = GetAssetCacheBackupFolder(id);
			if (!RobloxAssetCache.HasBackups(folder))
				return;
			if (enable)
				RobloxAssetCache.Reapply(folder);
			else
				RobloxAssetCache.Restore(folder);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModsViewModel::ApplyAssetCacheState", "The cached assets could not be updated: " + ex.Message);
		}
	}

	public event EventHandler? ModPackViewRequested;

	private async Task ViewModPackAsync(ManagedModItem? item)
	{
		if (item?.Pack is not ModPackInfo pack)
			return;
		try
		{
			ModPackViewRequested?.Invoke(this, EventArgs.Empty);
			await CommunityMods.OpenPackAsync(pack);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::ViewModPack", ex);
		}
	}

	private static string? AskForManagedModName(string title, string initial)
	{
		Voidstrap.UI.Elements.Dialogs.TextInputDialog dialog = new(title, initial);
		dialog.Owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
		dialog.ShowOwnedDialog();
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
		if (!Voidstrap.Utility.PlatformShell.TryOpenFolder(folder))
		{
			Frontend.ShowMessageBox("The folder could not be opened. It is at " + folder, MessageBoxImage.Warning);
		}
	}

	private static void CopyManagedModId(ManagedModItem? item)
	{
		if (item is null)
			return;
		try
		{
			Voidstrap.Utility.ClipboardService.SetText(item.Id);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("The mod identifier could not be copied:\n" + ex.Message, MessageBoxImage.Warning);
		}
	}

	private void PickColor(bool main)
	{
		var dlg = new Voidstrap.UI.Elements.Controls.RinColorPickerDialog();
		if (dlg.ShowOwnedDialog() == true)
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

	private static BitmapSource? LoadImageFromUrl(string url)
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
		try
		{
			BitmapSource bitmap = CreateCrosshairExportBitmap(64);
			byte[] cursor = EncodeCrosshairCursor(bitmap, 32, 32);
			Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
			{
				Filter = "Cursor File (*.cur)|*.cur",
				DefaultExt = ".cur",
				AddExtension = true,
				FileName = "crosshair.cur"
			};
			if (dialog.ShowDialog() != true)
				return;
			File.WriteAllBytes(dialog.FileName, cursor);
			Frontend.ShowMessageBox("Crosshair CUR saved to:\n" + dialog.FileName);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::DownloadCurFile", ex);
			Frontend.ShowMessageBox("Failed to generate cursor:\n" + ex.Message, MessageBoxImage.Warning);
		}
	}

	private void DownloadPngFile()
	{
		try
		{
			BitmapSource bitmap = CreateCrosshairExportBitmap(128);
			byte[] png = EncodeCrosshairPng(bitmap);
			Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
			{
				Filter = "PNG Image (*.png)|*.png",
				DefaultExt = ".png",
				AddExtension = true,
				FileName = "crosshair.png"
			};
			if (dialog.ShowDialog() != true)
				return;
			File.WriteAllBytes(dialog.FileName, png);
			Frontend.ShowMessageBox("Crosshair PNG saved to:\n" + dialog.FileName);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsViewModel::DownloadPngFile", ex);
			Frontend.ShowMessageBox("Failed to save PNG:\n" + ex.Message, MessageBoxImage.Warning);
		}
	}

	private BitmapSource CreateCrosshairExportBitmap(int dimension)
	{
		if (SelectedShape == CrosshairShape.Image && !string.IsNullOrWhiteSpace(ImageUrl))
		{
			if (LoadImageFromUrl(ImageUrl) is not BitmapSource image)
				throw new InvalidOperationException("The selected crosshair image could not be loaded.");
			return ResizeCrosshairImage(image, dimension, CursorOpacity);
		}
		return CreateCrosshairBitmap(dimension, 1.0);
	}

	private static BitmapSource ResizeCrosshairImage(BitmapSource source, int dimension, double opacity)
	{
		byte[] sourcePixels = ReadStraightBgraPixels(source, out int sourceWidth, out int sourceHeight);
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(sourcePixels, sourceWidth, sourceHeight);
		image.Mutate(context => context.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
		{
			Size = new SixLabors.ImageSharp.Size(dimension, dimension),
			Mode = SixLabors.ImageSharp.Processing.ResizeMode.Pad,
			PadColor = SixLabors.ImageSharp.Color.Transparent,
			Sampler = SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3
		}));
		byte[] pixels = new byte[dimension * dimension * 4];
		image.CopyPixelDataTo(pixels);
		double alphaScale = Math.Clamp(opacity, 0.0, 1.0);
		if (alphaScale < 1.0)
		{
			for (int offset = 3; offset < pixels.Length; offset += 4)
				pixels[offset] = (byte)Math.Round(pixels[offset] * alphaScale);
		}
		BitmapSource bitmap = BitmapSource.Create(dimension, dimension, 96.0, 96.0, PixelFormats.Bgra32, null, pixels, dimension * 4);
		bitmap.Freeze();
		return bitmap;
	}

	internal static byte[] EncodeCrosshairPng(BitmapSource bitmap)
	{
		byte[] pixels = ReadStraightBgraPixels(bitmap, out int width, out int height);
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(pixels, width, height);
		using MemoryStream stream = new MemoryStream();
		image.Save(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
		return stream.ToArray();
	}

	internal static byte[] EncodeCrosshairCursor(BitmapSource bitmap, ushort hotspotX, ushort hotspotY)
	{
		byte[] png = EncodeCrosshairPng(bitmap);
		int width = bitmap.PixelWidth;
		int height = bitmap.PixelHeight;
		if (width < 1 || width > 256 || height < 1 || height > 256)
			throw new InvalidOperationException("Cursor dimensions must be between 1 and 256 pixels.");
		if (hotspotX >= width || hotspotY >= height)
			throw new InvalidOperationException("Cursor hotspot must be inside the image.");
		using MemoryStream stream = new MemoryStream(22 + png.Length);
		using BinaryWriter writer = new BinaryWriter(stream);
		writer.Write((ushort)0);
		writer.Write((ushort)2);
		writer.Write((ushort)1);
		writer.Write((byte)(width == 256 ? 0 : width));
		writer.Write((byte)(height == 256 ? 0 : height));
		writer.Write((byte)0);
		writer.Write((byte)0);
		writer.Write(hotspotX);
		writer.Write(hotspotY);
		writer.Write((uint)png.Length);
		writer.Write(22u);
		writer.Write(png);
		writer.Flush();
		return stream.ToArray();
	}

	private static byte[] ReadStraightBgraPixels(BitmapSource bitmap, out int width, out int height)
	{
		BitmapSource source = bitmap;
		if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32 && source.Format != PixelFormats.Bgr32)
		{
			if (!Voidstrap.Utility.Platform.IsWindows)
				throw new InvalidOperationException("The selected image uses an unsupported pixel format.");
			source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
		}
		width = source.PixelWidth;
		height = source.PixelHeight;
		int stride = checked(width * 4);
		byte[] pixels = new byte[checked(stride * height)];
		source.CopyPixels(pixels, stride, 0);
		if (source.Format == PixelFormats.Bgr32)
		{
			for (int offset = 3; offset < pixels.Length; offset += 4)
				pixels[offset] = 255;
		}
		else if (source.Format == PixelFormats.Pbgra32)
		{
			for (int offset = 0; offset < pixels.Length; offset += 4)
			{
				int alpha = pixels[offset + 3];
				if (alpha == 0)
				{
					pixels[offset] = 0;
					pixels[offset + 1] = 0;
					pixels[offset + 2] = 0;
					continue;
				}
				pixels[offset] = (byte)Math.Min(255, (pixels[offset] * 255 + alpha / 2) / alpha);
				pixels[offset + 1] = (byte)Math.Min(255, (pixels[offset + 1] * 255 + alpha / 2) / alpha);
				pixels[offset + 2] = (byte)Math.Min(255, (pixels[offset + 2] * 255 + alpha / 2) / alpha);
			}
		}
		return pixels;
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

	private bool _loadingCrosshair;

	private void ApplyCrosshairChange()
	{
		if (_loadingCrosshair)
			return;
		SaveIni();
		UpdatePreview();
	}

	private void UpdatePreview()
	{
		if (System.Windows.Application.Current == null)
			return;
		((DispatcherObject)System.Windows.Application.Current).Dispatcher.Invoke((Action)delegate
		{
			try
			{
				if (SelectedShape == CrosshairShape.Image && !string.IsNullOrWhiteSpace(ImageUrl))
				{
					ImageSource? imageSource = LoadImageFromUrl(ImageUrl);
					if (imageSource != null)
					{
						CursorPreview = imageSource;
						return;
					}
				}
				CursorPreview = CreateCrosshairBitmap(128, 0.75);
			}
			catch (Exception ex)
			{
				CursorPreview = null;
				App.Logger.WriteException("ModsViewModel::UpdatePreview", ex);
			}
		});
	}

	private BitmapSource CreateCrosshairBitmap(int dimension, double scale)
	{
		const int samples = 4;
		const int sampleCount = samples * samples;
		int width = dimension;
		int height = dimension;
		byte[] pixels = new byte[width * height * 4];
		System.Windows.Media.Color fill = ParsePreviewColor(CursorColorHex, System.Windows.Media.Color.FromRgb(0, 255, 0));
		System.Windows.Media.Color outline = ParsePreviewColor(CursorOutlineColorHex, System.Windows.Media.Colors.Black);
		double opacity = Math.Clamp(CursorOpacity, 0.0, 1.0);
		double size = Math.Clamp(CursorSize, 2, 60) * scale;
		double gap = Math.Clamp(Gap, 0, 40) * scale;
		double thickness = Math.Max(1.0, Math.Clamp(CrosshairThickness, 1, 16) * scale);
		double centerX = width / 2.0;
		double centerY = height / 2.0;
		double reach = size + thickness + 4.0;
		double band = thickness / 2.0 + 3.0;
		bool cross = SelectedShape == CrosshairShape.Cross;

		for (int y = 0; y < height; y++)
		{
			double rowOffset = Math.Abs(y + 0.5 - centerY);
			if (rowOffset > reach)
				continue;
			for (int x = 0; x < width; x++)
			{
				double columnOffset = Math.Abs(x + 0.5 - centerX);
				if (columnOffset > reach || cross && columnOffset > band && rowOffset > band)
					continue;
				int alphaSum = 0;
				int redSum = 0;
				int greenSum = 0;
				int blueSum = 0;
				for (int sampleY = 0; sampleY < samples; sampleY++)
				{
					for (int sampleX = 0; sampleX < samples; sampleX++)
					{
						double px = x + (sampleX + 0.5) / samples - centerX;
						double py = y + (sampleY + 0.5) / samples - centerY;
						bool fillHit = PreviewHit(px, py, size, gap, thickness, false);
						bool outlineHit = !fillHit && PreviewHit(px, py, size, gap, thickness, true);
						if (!fillHit && !outlineHit)
							continue;
						System.Windows.Media.Color color = fillHit ? fill : outline;
						int alpha = (int)Math.Round(color.A * opacity);
						alphaSum += alpha;
						redSum += color.R * alpha / 255;
						greenSum += color.G * alpha / 255;
						blueSum += color.B * alpha / 255;
					}
				}
				int offset = (y * width + x) * 4;
				pixels[offset] = (byte)(blueSum / sampleCount);
				pixels[offset + 1] = (byte)(greenSum / sampleCount);
				pixels[offset + 2] = (byte)(redSum / sampleCount);
				pixels[offset + 3] = (byte)(alphaSum / sampleCount);
			}
		}

		BitmapSource bitmap = BitmapSource.Create(width, height, 96.0, 96.0, PixelFormats.Pbgra32, null, pixels, width * 4);
		bitmap.Freeze();
		return bitmap;
	}

	private bool PreviewHit(double x, double y, double size, double gap, double thickness, bool outline)
	{
		double radius = outline ? thickness / 2.0 + 1.0 : thickness / 2.0;
		switch (SelectedShape)
		{
		case CrosshairShape.Cross:
			return DistanceToSegment(x, y, -size, 0, -gap, 0) <= radius
				|| DistanceToSegment(x, y, gap, 0, size, 0) <= radius
				|| DistanceToSegment(x, y, 0, -size, 0, -gap) <= radius
				|| DistanceToSegment(x, y, 0, gap, 0, size) <= radius;
		case CrosshairShape.Dot:
			return Math.Sqrt(x * x + y * y) <= size / 3.0 + (outline ? 2.0 : 0.0);
		case CrosshairShape.Circle:
			double circleRadius = outline ? size / 2.0 : Math.Max(0.0, size / 2.0 - 2.0);
			return Math.Abs(Math.Sqrt(x * x + y * y) - circleRadius) <= radius;
		default:
			return false;
		}
	}

	private static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
	{
		double dx = x2 - x1;
		double dy = y2 - y1;
		double lengthSquared = dx * dx + dy * dy;
		double t = lengthSquared == 0 ? 0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lengthSquared, 0.0, 1.0);
		double nearestX = x1 + t * dx;
		double nearestY = y1 + t * dy;
		double distanceX = px - nearestX;
		double distanceY = py - nearestY;
		return Math.Sqrt(distanceX * distanceX + distanceY * distanceY);
	}

	private static System.Windows.Media.Color ParsePreviewColor(string? value, System.Windows.Media.Color fallback)
	{
		try
		{
			return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value ?? "");
		}
		catch
		{
			return fallback;
		}
	}

	private void MirrorCrosshairToSettings()
	{
		try
		{
			var prop = App.Settings.Prop;
			prop.CrosshairShapeIndex = (int)SelectedShape;
			prop.CrosshairImagePath = ImageUrl ?? "";
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
		Voidstrap.Integrations.Overlays.OverlayHub.RefreshCrosshair();
	}

	private void LoadIni()
	{
		_loadingCrosshair = true;
		try
		{
			LoadIniValues();
		}
		finally
		{
			_loadingCrosshair = false;
		}
		if (File.Exists(_file) && !CrosshairSettingsMatch())
			MirrorCrosshairToSettings();
		UpdatePreview();
	}

	private bool CrosshairSettingsMatch()
	{
		var prop = App.Settings.Prop;
		return prop.CrosshairShapeIndex == (int)SelectedShape
			&& string.Equals(prop.CrosshairImagePath, ImageUrl ?? "", StringComparison.Ordinal)
			&& string.Equals(prop.CrosshairColorHex, CursorColorHex ?? "", StringComparison.Ordinal)
			&& string.Equals(prop.CrosshairOutlineColorHex, CursorOutlineColorHex ?? "", StringComparison.Ordinal)
			&& prop.CrosshairSize == CursorSize
			&& prop.CrosshairLineThickness == CrosshairThickness
			&& prop.CrosshairGap == Gap
			&& prop.CrosshairOpacity.Equals(CursorOpacity);
	}

	private void LoadIniValues()
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
			OnPropertyChanged(nameof(ExplorerStatusMessage));
			OnPropertyChanged(nameof(ExplorerHasStatus));
		}
	}

	public bool ExplorerHasStatus => !string.IsNullOrEmpty(_explorerStatusMessage);

	public string ExplorerItemCountText => ModFiles.Count == 1 ? "1 item" : $"{ModFiles.Count} items";

	private string ResolveRobloxPlayerDir(bool forceRefresh = false)
	{
		if (!forceRefresh && _robloxPlayerDirCache != null && Directory.Exists(_robloxPlayerDirCache))
			return _robloxPlayerDirCache;
		_robloxPlayerDirCache = GetRobloxPlayerDir();
		return _robloxPlayerDirCache;
	}

	private static string GetRobloxPlayerDir()
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
				string[] directories = Directory.GetDirectories(versionsRoot)
					.OrderByDescending(Directory.GetLastWriteTimeUtc)
					.ToArray();
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
		string? directoryName = Path.GetDirectoryName(text);
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
		if (SelectedModFile != null && SelectedModFile.IsImage)
		{
			ImageRecolorWindow imageRecolorWindow = new ImageRecolorWindow(SelectedModFile.FullPath, SelectedModFile.RelativePath);
			imageRecolorWindow.Owner = System.Windows.Application.Current.MainWindow;
			imageRecolorWindow.ShowOwnedDialog();
			RefreshModFiles();
		}
	}

	public void AdjustImage()
	{
		if (SelectedModFile != null && SelectedModFile.IsImage)
		{
			ImageAdjustWindow imageAdjustWindow = new ImageAdjustWindow(SelectedModFile.FullPath, SelectedModFile.RelativePath);
			imageAdjustWindow.Owner = System.Windows.Application.Current.MainWindow;
			imageAdjustWindow.ShowOwnedDialog();
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
		SelectedModFile = null;
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
			OnPropertyChanged(nameof(CurrentExplorerPath));
			OnPropertyChanged(nameof(ExplorerPathDisplay));
		}

		if (!Directory.Exists(path))
		{
			root = ResolveRobloxPlayerDir(forceRefresh: true);
			if (Directory.Exists(root))
			{
				_currentExplorerPath = root;
				OnPropertyChanged(nameof(CurrentExplorerPath));
				OnPropertyChanged(nameof(ExplorerPathDisplay));
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
		OnPropertyChanged(nameof(ExplorerItemCountText));
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

	private static void UpdateModStatus(ModFile file, string robloxPlayerDir)
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
    private static readonly string[] DeathSoundFiles = ["oof.ogg"];
    private static readonly char[] trimChars = new char[2] { '\\', '/' };

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
			_ = AssetCaptureStore.ScanAvatarAssetsAsync();
		}
		catch
		{
		}
		try
		{
			_ = AssetCaptureStore.ResolveHashesAsync(150);
		}
		catch
		{
		}
		try
		{
			_ = AssetCaptureStore.PrefetchUnsizedAsync(6);
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
			_ = ResolveNamesAsync();
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
			Voidstrap.Utility.ClipboardService.SetText(text);
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
		byte[]? array = AssetCaptureStore.ReadContent(asset);
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
				BitmapSource? bitmapSource = KtxDecoder.DecodeToBitmap(array);
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

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorPattern { get; }
}
