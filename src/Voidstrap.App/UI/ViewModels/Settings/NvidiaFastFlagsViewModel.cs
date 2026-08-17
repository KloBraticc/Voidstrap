using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Voidstrap.Integrations;
using Voidstrap.Models;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed class NvidiaFastFlagsViewModel : INotifyPropertyChanged
{
	private static readonly string NipPath = Path.Combine(Paths.NipProfiles, "Voidstrap.nip");

	private static readonly Dictionary<string, int> BenchmarkOverlayMap = new()
	{
		{ "Disabled", 0 },
		{ "GRAPH_FLIP_FPS - FPS graph, measured on display hw flip", 1 },
		{ "GRAPH_PRESENT_FPS - FPS graph, measured when the user mode driver starts processing present", 2 },
		{ "GRAPH_APP_PRESENT_FPS - FPS graph, measured on app present", 4 },
		{ "DISPLAY_PAGING - Add red paging indicator bars to the GRAPH_PRESENT_FPS graph", 8 },
		{ "DISPLAY_APP_THREAD_WAIT - Add app thread wait time indiator bars to the GRAPH_APP_PRESENT_FPS graph", 16 },
		{ "Enabled - Enable everything", 511 }
	};

	private string _cplLatency = "Off";

	private string _benchmarkOverlay = "Disabled";

	private string _frlLatency = "Off";

	private string _selectedSilkSmoothness = "Off";

	private bool _rbar;

	private bool _gamma;

	private bool _dlssSR;

	private bool _dlssFG;

	private bool _mfaa;

	private bool _fxaa;

	private int _textureLodBias;

	private int _frameRateLimit;

	private int _backgroundFrameRateLimit;

	public ObservableCollection<string> CplLowLatencyModes { get; } = ["Off", "On", "Ultra"];

	public ObservableCollection<string> BenchMarkOverlayModes { get; } = new ObservableCollection<string>(BenchmarkOverlayMap.Keys);

	public ObservableCollection<string> FrlLowLatencyModes { get; } = ["Off", "On"];

	public ObservableCollection<string> SilkSmoothnessModes { get; } = ["Off", "Low", "Medium", "High", "Ultra"];

	public string SelectedCplLowLatencyMode
	{
		get => _cplLatency;
		set => Set(ref _cplLatency, value, "SelectedCplLowLatencyMode");
	}

	public string BenchMarkOverlayMode
	{
		get => _benchmarkOverlay;
		set => Set(ref _benchmarkOverlay, value, "BenchMarkOverlayMode");
	}

	public string SelectedFrlLowLatencyMode
	{
		get => _frlLatency;
		set => Set(ref _frlLatency, value, "SelectedFrlLowLatencyMode");
	}

	public string SelectedSilkSmoothness
	{
		get => _selectedSilkSmoothness;
		set => Set(ref _selectedSilkSmoothness, value, "SelectedSilkSmoothness");
	}

	public bool EnableRbar
	{
		get => _rbar;
		set => Set(ref _rbar, value, "EnableRbar");
	}

	public bool EnableGamma
	{
		get => _gamma;
		set => Set(ref _gamma, value, "EnableGamma");
	}

	public bool EnableDlssSuperResolution
	{
		get => _dlssSR;
		set => Set(ref _dlssSR, value, "EnableDlssSuperResolution");
	}

	public bool EnableDlssFrameGen
	{
		get => _dlssFG;
		set => Set(ref _dlssFG, value, "EnableDlssFrameGen");
	}

	public bool EnableMFAA
	{
		get => _mfaa;
		set => Set(ref _mfaa, value, "EnableMFAA");
	}

	public bool EnableFXAA
	{
		get => _fxaa;
		set => Set(ref _fxaa, value, "EnableFXAA");
	}

	public int FrameRateLimit
	{
		get => _frameRateLimit;
		set => Set(ref _frameRateLimit, Math.Clamp(value, 0, 1000), "FrameRateLimit");
	}

	public int BackgroundFrameRateLimit
	{
		get => _backgroundFrameRateLimit;
		set => Set(ref _backgroundFrameRateLimit, Math.Clamp(value, 0, 1000), "BackgroundFrameRateLimit");
	}

	public int TextureLodBias
	{
		get => _textureLodBias;
		set
		{
			Set(ref _textureLodBias, Math.Clamp(value, -32, 120), "TextureLodBias");
			OnPropertyChanged("TextureLodBiasLabel");
		}
	}

	public string TextureLodBiasLabel
	{
		get
		{
			if (TextureLodBias != 0)
				return string.Format(CultureInfo.InvariantCulture, "LOD Bias Override: {0:0.###}", (double)TextureLodBias / 8.0);
			return "Default (Driver Controlled)";
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public NvidiaFastFlagsViewModel()
	{
		EnsureNipExists();
		Load();
	}

	private void Load()
	{
		List<NvidiaEditorEntry> entries = LoadNormalized();
		SelectedCplLowLatencyMode = ReadEnum(entries, "390467", CplLowLatencyModes, 0);
		BenchMarkOverlayMode = BenchmarkOverlayMap.FirstOrDefault(x => x.Value == ReadInt(entries, "2945366", 0)).Key ?? "Disabled";
		SelectedFrlLowLatencyMode = ReadEnum(entries, "277041152", FrlLowLatencyModes, 0);
		SelectedSilkSmoothness = ReadSilk(entries);
		EnableRbar = ReadBool(entries, "549198379");
		EnableDlssSuperResolution = ReadBool(entries, "283385345");
		EnableDlssFrameGen = ReadBool(entries, "283385347");
		FrameRateLimit = ReadInt(entries, "277041154", 0);
		BackgroundFrameRateLimit = ReadInt(entries, "277041157", 0);
		EnableMFAA = ReadBool(entries, "10011052");
		EnableFXAA = ReadBool(entries, "276089202") && ReadBool(entries, "276757595");
		EnableGamma = !ReadBool(entries, "276652957") || !ReadBool(entries, "545898348");
		TextureLodBias = ReadLodBias(entries);
	}

	private static int ReadLodBias(List<NvidiaEditorEntry> entries)
	{
		string? raw = entries.FirstOrDefault(x => x.SettingId == "7573135")?.Value;
		if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint stored))
			return unchecked((int)stored);
		return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed) ? signed : 0;
	}

	private static List<NvidiaEditorEntry> LoadNormalized()
	{
		List<NvidiaEditorEntry> entries = NvidiaProfileManager.LoadFromNip(NipPath);
		NormalizeIds(entries);
		return RemoveDuplicateSettingIds(entries);
	}

	public List<NvidiaEditorEntry> StageEntries()
	{
		List<NvidiaEditorEntry> entries = LoadNormalized();
		Set(entries, "Frame Rate Limiter", "277041154", FrameRateLimit.ToString(CultureInfo.InvariantCulture));
		Set(entries, "Background Application Max Frame Rate", "277041157", BackgroundFrameRateLimit.ToString(CultureInfo.InvariantCulture));
		Set(entries, "CPL Low Latency Mode", "390467", CplLowLatencyModes.IndexOf(SelectedCplLowLatencyMode).ToString(CultureInfo.InvariantCulture));
		Set(entries, "Benchmark Overlay", "2945366", BenchmarkOverlayMap.TryGetValue(BenchMarkOverlayMode, out int overlay) ? overlay.ToString(CultureInfo.InvariantCulture) : "0");
		Set(entries, "FRL Low Latency Mode", "277041152", FrlLowLatencyModes.IndexOf(SelectedFrlLowLatencyMode).ToString(CultureInfo.InvariantCulture));
		Set(entries, "SILK Smoothness", "9990737", SilkToValue(SelectedSilkSmoothness));
		Set(entries, "Resizable BAR", "549198379", EnableRbar ? "1" : "0");
		Set(entries, "Enable DLSS-SR override", "283385345", EnableDlssSuperResolution ? "1" : "0");
		Set(entries, "Enable DLSS-FG override", "283385347", EnableDlssFrameGen ? "1" : "0");
		string gamma = EnableGamma ? "0" : "1";
		Set(entries, "Gamma correction", "276652957", gamma);
		Set(entries, "Line gamma", "545898348", gamma);
		string antialiasing = EnableFXAA ? "1" : "0";
		Set(entries, "Enable FXAA", "276089202", antialiasing);
		Set(entries, "Antialiasing - Mode", "276757595", antialiasing);
		Set(entries, "MFAA", "10011052", EnableMFAA ? "1" : "0");
		Set(entries, "Texture filtering - LOD Bias", "7573135", unchecked((uint)TextureLodBias).ToString(CultureInfo.InvariantCulture));
		if (TextureLodBias != 0)
		{
			Set(entries, "Texture filtering - Quality", "13510289", "20");
			Set(entries, "Anisotropic filtering mode", "282245910", "1");
			Set(entries, "Antialiasing - Transparency Supersampling", "282364549", "8");
		}
		NvidiaProfileManager.SaveToNip(NipPath, entries);
		return entries;
	}

	public void ReloadFromDriver()
	{
		try
		{
			List<NvidiaEditorEntry> entries = LoadNormalized();
			if (NvidiaProfileManager.RefreshValuesFromDriver(entries) > 0)
				NvidiaProfileManager.SaveToNip(NipPath, entries);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("NvidiaFastFlagsViewModel", "Driver read-back failed: " + ex.Message);
		}

		Load();
	}

	private static void Set(List<NvidiaEditorEntry> entries, string name, string id, string newValue)
	{
		NvidiaEditorEntry? existing = entries.FirstOrDefault(x => x.SettingId == id);
		if (existing != null)
		{
			existing.Value = newValue;
			return;
		}
		entries.Add(new NvidiaEditorEntry
		{
			Name = name,
			SettingId = id,
			Value = newValue,
			ValueType = "Dword"
		});
	}

	private static void NormalizeIds(List<NvidiaEditorEntry> entries)
	{
		foreach (NvidiaEditorEntry entry in entries)
		{
			string text = (entry.SettingId ?? string.Empty).Trim();
			if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				&& uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
			{
				entry.SettingId = parsed.ToString(CultureInfo.InvariantCulture);
			}
		}
	}

	private static bool ReadBool(List<NvidiaEditorEntry> e, string id)
	{
		return e.FirstOrDefault(x => x.SettingId == id)?.Value == "1";
	}

	private static int ReadInt(List<NvidiaEditorEntry> e, string id, int d)
	{
		if (!int.TryParse(e.FirstOrDefault(x => x.SettingId == id)?.Value, out int result))
			return d;
		return result;
	}

	private static string ReadEnum(List<NvidiaEditorEntry> e, string id, ObservableCollection<string> v, int d)
	{
		if (!int.TryParse(e.FirstOrDefault(x => x.SettingId == id)?.Value, out int result) || result < 0 || result >= v.Count)
			return v[d];
		return v[result];
	}

	private static string ReadSilk(List<NvidiaEditorEntry> e)
	{
		return e.FirstOrDefault(x => x.SettingId == "9990737")?.Value switch
		{
			"1" => "Low",
			"2" => "Medium",
			"3" => "High",
			"4" => "Ultra",
			_ => "Off",
		};
	}

	private static string SilkToValue(string mode)
	{
		return mode switch
		{
			"Low" => "1",
			"Medium" => "2",
			"High" => "3",
			"Ultra" => "4",
			_ => "0",
		};
	}

	private static void EnsureNipExists()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(NipPath)!);
		if (!File.Exists(NipPath))
			NvidiaProfileManager.SaveToNip(NipPath, []);
	}

	private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (!object.Equals(field, value))
		{
			field = value;
			OnPropertyChanged(name);
		}
	}

	private static List<NvidiaEditorEntry> RemoveDuplicateSettingIds(List<NvidiaEditorEntry> entries)
	{
		return [.. (from x in entries
			group x by x.SettingId into g
			select g.First())];
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
