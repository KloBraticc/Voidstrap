using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Core.AssetProxy;
using Voidstrap.Enums.FlagPresets;
using Voidstrap.Integrations.AssetProxy;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public class FastFlagsViewModel : NotifyPropertyChangedViewModel
{
	private Dictionary<string, object>? _preResetFlags;

	public const string Enabled = "True";

	public const string Disabled = "False";

	private static readonly string[] LODLevels = new string[4] { "L0", "L12", "L23", "L34" };

	private const int DefaultMinGrassDistance = 100;

	private const int DefaultMaxGrassDistance = 290;

	private IEnumerable? profileModes;

	private string selectedProfileMods = string.Empty;

	public ICommand OpenFastFlagEditorCommand => new RelayCommand(OpenFastFlagEditor);

	public bool DisableTelemetry
	{
		get
		{
			return App.FastFlags?.GetPreset("Telemetry.TelemetryV2Url") == "0.0.0.0";
		}
		set
		{
			if (App.FastFlags != null)
			{
				App.FastFlags.SetPreset("Telemetry.TelemetryV2Url", value ? "0.0.0.0" : null);
				App.FastFlags.SetPreset("Telemetry.Protocol", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.GraphicsQualityUsage", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.GpuVsCpuBound", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.RenderFidelity", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.RenderDistance", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.AudioPlugin", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.FmodErrors", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.SoundLength", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.AssetRequestV1", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.DeviceRAM", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.V2FrameRateMetrics", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.GlobalSkipUpdating", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.CallbackSafety", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.V2PointEncoding", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.ReplaceSeparator", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.OpenTelemetry", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.FLogTelemetry", value ? "0" : null);
				App.FastFlags.SetPreset("Telemetry.TelemetryService", value ? "False" : null);
				App.FastFlags.SetPreset("Telemetry.PropertiesTelemetry", value ? "False" : null);
			}
		}
	}

	public bool GoogleToggle
	{
		get
		{
			return string.Equals(App.FastFlags.GetPreset("VoiceChat.VoiceChat1"), "False", StringComparison.OrdinalIgnoreCase);
		}
		set
		{
			if (value)
			{
				App.FastFlags.SetPreset("VoiceChat.VoiceChat1", "False");
				App.FastFlags.SetPreset("VoiceChat.VoiceChat2", "https://google.com");
				App.FastFlags.SetPreset("VoiceChat.VoiceChat3", "https://google.com");
			}
			else
			{
				App.FastFlags.SetPreset("VoiceChat.VoiceChat1", "True");
				App.FastFlags.SetPreset("VoiceChat.VoiceChat2", null);
				App.FastFlags.SetPreset("VoiceChat.VoiceChat3", null);
			}
		}
	}

	public bool LightCulling
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.GpuCulling") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.GpuCulling", value ? "True" : null);
			App.FastFlags.SetPreset("Rendering.CpuCulling", value ? "True" : null);
		}
	}

	public bool RainbowTheme
	{
		get
		{
			return App.FastFlags.GetPreset("UI.RainbowText") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("UI.RainbowText", value ? "True" : null);
		}
	}

	public bool RobloxStudioCoreUI
	{
		get
		{
			return App.FastFlags.GetPreset("UI.OLDUIRobloxStudio") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("UI.OLDUIRobloxStudio", value ? "True" : null);
		}
	}

	public bool FRMQualityOverrideEnabled
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.FRMQualityOverride") != null;
		}
		set
		{
			if (value)
			{
				FRMQualityOverride = 21;
			}
			else
			{
				App.FastFlags.SetPreset("Rendering.FRMQualityOverride", null);
			}
			OnPropertyChanged("FRMQualityOverride");
			OnPropertyChanged("FRMQualityOverrideEnabled");
		}
	}

	public int FRMQualityOverride
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Rendering.FRMQualityOverride"), out var result))
			{
				return 21;
			}
			return result;
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.FRMQualityOverride", value);
			OnPropertyChanged("FRMQualityOverride");
		}
	}

	public int MeshQuality
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Geometry.MeshLOD.L0"), out var result))
			{
				return 0;
			}
			return result;
		}
		set
		{
			int num = Math.Clamp(value, 0, LODLevels.Length - 1);
			for (int i = 0; i < LODLevels.Length; i++)
			{
				int num2 = Math.Clamp(num - i, 0, 3);
				string text = LODLevels[i];
				App.FastFlags.SetPreset("Geometry.MeshLOD." + text, num2);
			}
			OnPropertyChanged("MeshQuality");
			OnPropertyChanged("MeshQualityEnabled");
		}
	}

	public bool MeshQualityEnabled
	{
		get
		{
			return App.FastFlags.GetPreset("Geometry.MeshLOD.L0") != null;
		}
		set
		{
			if (value)
			{
				MeshQuality = 3;
			}
			else
			{
				string[] lODLevels = LODLevels;
				foreach (string text in lODLevels)
				{
					App.FastFlags.SetPreset("Geometry.MeshLOD." + text, null);
				}
				App.FastFlags.SetPreset("Geometry.MeshLOD.Static", null);
			}
			OnPropertyChanged("MeshQualityEnabled");
		}
	}

	public bool MemoryProbing
	{
		get
		{
			return App.FastFlags.GetPreset("Memory.Probe") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Memory.Probe", value ? "True" : null);
			if (value)
			{
				App.FastFlags.SetPreset("Memory.probe2", "0");
				App.FastFlags.SetPreset("Memory.probe3", "1");
				App.FastFlags.SetPreset("Memory.probe4", "1");
				App.FastFlags.SetPreset("Memory.probe5", "3");
				App.FastFlags.SetPreset("Memory.probe6", "2000000000");
				App.FastFlags.SetPreset("Memory.probe7", "1");
				App.FastFlags.SetPreset("Memory.probe8", "102400");
			}
			else
			{
				App.FastFlags.SetPreset("Memory.probe2", null);
				App.FastFlags.SetPreset("Memory.probe3", null);
				App.FastFlags.SetPreset("Memory.probe4", null);
				App.FastFlags.SetPreset("Memory.probe5", null);
				App.FastFlags.SetPreset("Memory.probe6", null);
				App.FastFlags.SetPreset("Memory.probe7", null);
				App.FastFlags.SetPreset("Memory.probe8", null);
			}
		}
	}

	public bool MoreSensetivityNumbers
	{
		get
		{
			return App.FastFlags.GetPreset("UI.SensetivityNumbers") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("UI.SensetivityNumbers", value ? "False" : null);
		}
	}

	public bool NoGuiBlur
	{
		get
		{
			return App.FastFlags.GetPreset("UI.NoGuiBlur") == "0";
		}
		set
		{
			App.FastFlags.SetPreset("UI.NoGuiBlur", value ? "0" : null);
		}
	}

	public bool Layered
	{
		get
		{
			return App.FastFlags.GetPreset("Layered.Clothing") == "-1";
		}
		set
		{
			App.FastFlags.SetPreset("Layered.Clothing", value ? "-1" : null);
		}
	}

	public bool UnlimitedCameraZoom
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.Camerazoom") == "2147483647";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.Camerazoom", value ? "2147483647" : null);
		}
	}

	public bool Preload
	{
		get
		{
			return App.FastFlags.GetPreset("Preload.Preload2") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Preload.Preload2", value ? "True" : null);
			App.FastFlags.SetPreset("Preload.SoundPreload", value ? "True" : null);
			App.FastFlags.SetPreset("Preload.Texture", value ? "True" : null);
			App.FastFlags.SetPreset("Preload.TeleportPreload", value ? "True" : null);
			App.FastFlags.SetPreset("Preload.FontsPreload", value ? "True" : null);
			App.FastFlags.SetPreset("Preload.ItemPreload", value ? "True" : null);
			App.FastFlags.SetPreset("Preload.Teleport2", value ? "True" : null);
		}
	}

	public bool OptimizeCFrameUpdates
	{
		get
		{
			return App.FastFlags.GetPreset("OptimizeCFrameUpdates") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("OptimizeCFrameUpdates", value ? "True" : null);
			App.FastFlags.SetPreset("OptimizeCFrameUpdatesIC", value ? "True" : null);
		}
	}

	public bool TextSizeChanger
	{
		get
		{
			return App.FastFlags.GetPreset("UI.TextSize1") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("UI.TextSize1", value ? "True" : null);
			App.FastFlags.SetPreset("UI.TextSize2", value ? "True" : null);
		}
	}

	public bool TextureRemover
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.RemoveTexture1") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.RemoveTexture1", value ? "True" : null);
			App.FastFlags.SetPreset("Rendering.RemoveTexture2", value ? "10000" : null);
		}
	}

	public bool Threading
	{
		get
		{
			return App.FastFlags.GetPreset("Hyper.Threading1") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Hyper.Threading1", value ? "True" : null);
		}
	}

	public bool LessLagSpikes
	{
		get
		{
			return App.FastFlags.GetPreset("Network.DefaultBps") == "796850000";
		}
		set
		{
			App.FastFlags.SetPreset("Network.DefaultBps", value ? "796850000" : null);
			App.FastFlags.SetPreset("Network.MaxWorkCatchupMs", value ? "5" : null);
		}
	}

	public bool DisableAds
	{
		get
		{
			return App.FastFlags.GetPreset("UI.DisableAds1") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("UI.DisableAds1", value ? "False" : null);
			App.FastFlags.SetPreset("UI.DisableAds2", value ? "False" : null);
			App.FastFlags.SetPreset("UI.DisableAds3", value ? "False" : null);
			App.FastFlags.SetPreset("UI.DisableAds4", value ? "False" : null);
			App.FastFlags.SetPreset("UI.DisableAds5", value ? "False" : null);
			App.FastFlags.SetPreset("UI.DisableAds6", value ? "False" : null);
		}
	}

	public bool RobloxCore
	{
		get
		{
			return App.FastFlags.GetPreset("Network.RCore1") == "20000";
		}
		set
		{
			App.FastFlags.SetPreset("Network.RCore1", value ? "20000" : null);
			App.FastFlags.SetPreset("Network.RCore2", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.RCore3", value ? "10" : null);
			App.FastFlags.SetPreset("Network.RCore4", value ? "3000" : null);
			App.FastFlags.SetPreset("Network.RCore5", value ? "25" : null);
			App.FastFlags.SetPreset("Network.RCore6", value ? "5000" : null);
		}
	}

	public bool NoPayloadLimit
	{
		get
		{
			return App.FastFlags.GetPreset("Network.Payload1") == "2147483647";
		}
		set
		{
			App.FastFlags.SetPreset("Network.Payload1", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload2", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload3", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload4", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload5", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload6", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload7", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload8", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload9", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload10", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.Payload11", value ? "2147483647" : null);
		}
	}

	public bool ShadersEnabled
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.Shaders2") == "21";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.Shaders2", value ? "21" : "0");
		}
	}

	public bool EnableLargeReplicator
	{
		get
		{
			return App.FastFlags.GetPreset("Network.EnableLargeReplicator") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Network.EnableLargeReplicator", value ? "True" : null);
			App.FastFlags.SetPreset("Network.LargeReplicatorWrite", value ? "True" : null);
			App.FastFlags.SetPreset("Network.LargeReplicatorRead", value ? "True" : null);
			App.FastFlags.SetPreset("Network.EngineModule1", value ? "False" : null);
			App.FastFlags.SetPreset("Network.EngineModule2", value ? "True" : null);
			App.FastFlags.SetPreset("Network.SerializeRead", value ? "True" : null);
			App.FastFlags.SetPreset("Network.SerializeWrite", value ? "True" : null);
		}
	}

	public bool PingBreakdown
	{
		get
		{
			return App.FastFlags.GetPreset("Debug.PingBreakdown") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Debug.PingBreakdown", value ? "True" : null);
		}
	}

	public bool EnableDarkMode
	{
		get
		{
			return App.FastFlags.GetPreset("DarkMode.BlueMode") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("DarkMode.BlueMode", value ? "False" : null);
		}
	}

	public bool ChatBubble
	{
		get
		{
			return App.FastFlags.GetPreset("UI.Chatbubble") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("UI.Chatbubble", value ? "False" : null);
		}
	}

	public bool NoMoreMiddle
	{
		get
		{
			return App.FastFlags.GetPreset("UI.RemoveMiddle") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("UI.RemoveMiddle", value ? "False" : null);
		}
	}

	public bool DisplayFps
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.DisplayFps") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.DisplayFps", value ? "True" : null);
		}
	}

	public bool GrayAvatar
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.GrayAvatar") == "0";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.GrayAvatar", value ? "0" : null);
		}
	}

	public bool UseFastFlagManager
	{
		get
		{
			return App.Settings.Prop.UseFastFlagManager;
		}
		set
		{
			App.Settings.Prop.UseFastFlagManager = value;
		}
	}

	public int FramerateLimit
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Rendering.Framerate"), out var result))
			{
				return 0;
			}
			return result;
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.Framerate", (value == 0) ? null : ((object)value));
			if (value > 240)
			{
				Frontend.ShowMessageBox("Going above 240 FPS is not recommended, as this may cause latency issues.", MessageBoxImage.Exclamation);
				App.FastFlags.SetPreset("Rendering.LimitFramerate", "False");
			}
			else
			{
				App.FastFlags.SetPreset("Rendering.LimitFramerate", null);
			}
		}
	}

	public int ShadersLimit
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Rendering.Shaders"), out var result))
			{
				return 0;
			}
			return result;
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.Shaders", (value == 0) ? null : value.ToString());
			if (value < -64000000)
			{
				Frontend.ShowMessageBox("Going below -64000000 is not recommended for Performance.", MessageBoxImage.Exclamation);
			}
		}
	}

	public int VolChatLimit
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("VoiceChat.VoiceChat4"), out var result))
			{
				return 1000;
			}
			return result;
		}
		set
		{
			App.FastFlags.SetPreset("VoiceChat.VoiceChat4", (value > 0) ? value.ToString() : null);
		}
	}

	public int HideGUI
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("UI.Hide"), out var result))
			{
				return 0;
			}
			return result;
		}
		set
		{
			App.FastFlags.SetPreset("UI.Hide", (value > 0) ? value.ToString() : null);
		}
	}

	public int MtuSize
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Network.Mtusize"), out var result))
			{
				return 0;
			}
			return result;
		}
		set
		{
			int num = Math.Max(0, Math.Min(1500, value));
			App.FastFlags.SetPreset("Network.Mtusize", (num >= 576) ? num.ToString() : null);
		}
	}

	public bool BGRA
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.BGRA") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.BGRA", value ? "True" : null);
		}
	}

	public bool NewFpsSystem
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.NewFpsSystem") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.NewFpsSystem", value ? "True" : null);
		}
	}

	public bool DisableWebview2Telemetry
	{
		get
		{
			return App.FastFlags?.GetPreset("Telemetry.Webview1") == "www.youtube-nocookie.com";
		}
		set
		{
			App.FastFlags.SetPreset("Telemetry.Webview1", value ? "www.youtube-nocookie.com" : null);
			App.FastFlags.SetPreset("Telemetry.Webview2", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Webview3", value ? "0" : null);
			App.FastFlags.SetPreset("Telemetry.Webview4", value ? "0" : null);
			App.FastFlags.SetPreset("Telemetry.Webview5", value ? "0" : null);
			App.FastFlags.SetPreset("Telemetry.Webview6", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Webview7", value ? "False" : null);
		}
	}

	public bool WorserParticles
	{
		get
		{
			return App.FastFlags?.GetPreset("Rendering.WorserParticles1") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.WorserParticles1", value ? "False" : null);
			App.FastFlags.SetPreset("Rendering.WorserParticles2", value ? "False" : null);
			App.FastFlags.SetPreset("Rendering.WorserParticles3", value ? "False" : null);
			App.FastFlags.SetPreset("Rendering.WorserParticles4", value ? "False" : null);
		}
	}

	public bool LowPolyMeshes
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.LowPolyMeshes1") == "0";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.LowPolyMeshes1", value ? "0" : null);
			App.FastFlags.SetPreset("Rendering.LowPolyMeshes2", value ? "0" : null);
			App.FastFlags.SetPreset("Rendering.LowPolyMeshes3", value ? "0" : null);
			App.FastFlags.SetPreset("Rendering.LowPolyMeshes4", value ? "0" : null);
		}
	}

	public bool CacheSizeImprovement
	{
		get
		{
			return App.FastFlags.GetPreset("Cache.Increase1") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Cache.Increase1", value ? "True" : null);
			App.FastFlags.SetPreset("Cache.Increase2", value ? "False" : null);
			App.FastFlags.SetPreset("Cache.Increase3", value ? "True" : null);
			App.FastFlags.SetPreset("Cache.Increase4", value ? "1" : null);
			App.FastFlags.SetPreset("Cache.Increase5", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase6", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase7", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase8", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase9", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase10", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase11", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase12", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase13", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase14", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase15", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Cache.Increase16", value ? "True" : null);
			App.FastFlags.SetPreset("Cache.Increase17", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase18", value ? "1036372536" : null);
			App.FastFlags.SetPreset("Cache.Increase19", value ? "1036372536" : null);
		}
	}

	public bool AndroidVfs
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.AndroidVfs") == "{\"and\":[ {\"=\":[\"app_bitness()\",32]}, {\"not\":[ {\"is_any_of\":[\"manufacturer()\",\"samsung\",\"amazon\",\"lge\",\"lg\",\"lg electronics\",\"vivo\"]} ]} ]}";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.AndroidVfs", value ? "{\"and\":[ {\"=\":[\"app_bitness()\",32]}, {\"not\":[ {\"is_any_of\":[\"manufacturer()\",\"samsung\",\"amazon\",\"lge\",\"lg\",\"lg electronics\",\"vivo\"]} ]} ]}" : null);
		}
	}

	public bool FasterLoading
	{
		get
		{
			return App.FastFlags.GetPreset("Network.MaxAssetPreload") == "2147483647";
		}
		set
		{
			App.FastFlags.SetPreset("Network.MaxAssetPreload", value ? "2147483647" : null);
			App.FastFlags.SetPreset("Network.PlayerImageDefault", value ? "1" : null);
			App.FastFlags.SetPreset("Network.MeshPreloadding", value ? "True" : null);
		}
	}

	public bool EnableCustomDisconnectError
	{
		get
		{
			return App.FastFlags.GetPreset("UI.CustomDisconnectError1") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("UI.CustomDisconnectError1", value ? "True" : null);
		}
	}

	public string? CustomDisconnectError
	{
		get
		{
			return App.FastFlags.GetPreset("UI.CustomDisconnectError2");
		}
		set
		{
			App.FastFlags.SetPreset("UI.CustomDisconnectError2", value);
		}
	}

	public string? FakeVerify
	{
		get
		{
			return App.FastFlags.GetPreset("Fake.Verify");
		}
		set
		{
			App.FastFlags.SetPreset("Fake.Verify", value);
		}
	}

	public string? NewCamera
	{
		get
		{
			return App.FastFlags.GetPreset("Camera.Controls");
		}
		set
		{
			App.FastFlags.SetPreset("Camera.Controls", value);
		}
	}

	public string? ChatUI
	{
		get
		{
			return App.FastFlags.GetPreset("Camera.Chat");
		}
		set
		{
			App.FastFlags.SetPreset("Camera.Chat", value);
		}
	}

	public IReadOnlyDictionary<MSAAMode, string?> MSAALevels => FastFlagManager.MSAAModes;

	public MSAAMode SelectedMSAALevel
	{
		get
		{
			return MSAALevels.FirstOrDefault<KeyValuePair<MSAAMode, string>>((KeyValuePair<MSAAMode, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.MSAA1")).Key;
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.MSAA1", MSAALevels[value]);
			App.FastFlags.SetPreset("Rendering.MSAA2", null);
		}
	}

	public IReadOnlyDictionary<TextureQuality, string?> TextureQualities => FastFlagManager.TextureQualityLevels;

	public TextureQuality SelectedTextureQuality
	{
		get
		{
			return TextureQualities.FirstOrDefault<KeyValuePair<TextureQuality, string>>((KeyValuePair<TextureQuality, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.TextureQuality.Level")).Key;
		}
		set
		{
			if (value == TextureQuality.Default)
			{
				App.FastFlags.SetPreset("Rendering.TextureQuality.Level", null);
				App.FastFlags.SetPreset("Rendering.TextureQuality.OverrideEnabled", null);
				return;
			}
			App.FastFlags.SetPreset("Rendering.TextureQuality.OverrideEnabled", "True");
			App.FastFlags.SetPreset("Rendering.TextureQuality.Level", TextureQualities[value]);
		}
	}

	public IReadOnlyDictionary<RenderingMode, string> RenderingModes => FastFlagManager.RenderingModes;

	public RenderingMode SelectedRenderingMode
	{
		get
		{
			return App.FastFlags.GetPresetEnum(RenderingModes, "Rendering.Mode", "True");
		}
		set
		{
			App.FastFlags.SetPresetEnum("Rendering.Mode", value.ToString(), "True");
			App.FastFlags.SetPreset("Rendering.Mode.DisableD3D11", null);
		}
	}

	public bool FixDisplayScaling
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.DisableScaling") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.DisableScaling", value ? "True" : null);
		}
	}

	public bool MoreLighting
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.BrighterVisual") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.BrighterVisual", value ? "True" : null);
		}
	}

	public int MinGrassDistance
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Rendering.Nograss1"), out var result))
			{
				return 100;
			}
			return result;
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.Nograss1", value.ToString());
			OnPropertyChanged("MinGrassDistance");
		}
	}

	public int MaxGrassDistance
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Rendering.Nograss2"), out var result))
			{
				return 290;
			}
			return result;
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.Nograss2", value.ToString());
			OnPropertyChanged("MaxGrassDistance");
		}
	}

	public string? FlagState
	{
		get
		{
			return App.FastFlags.GetPreset("Debug.FlagState");
		}
		set
		{
			App.FastFlags.SetPreset("Debug.FlagState", value);
		}
	}

	public IReadOnlyDictionary<InGameMenuVersion, Dictionary<string, string?>> IGMenuVersions => FastFlagManager.IGMenuVersions;

	public InGameMenuVersion SelectedIGMenuVersion
	{
		get
		{
			foreach (KeyValuePair<InGameMenuVersion, Dictionary<string, string>> iGMenuVersion in IGMenuVersions)
			{
				bool flag = true;
				foreach (KeyValuePair<string, string> flag2 in iGMenuVersion.Value)
				{
					foreach (KeyValuePair<string, string> item in FastFlagManager.PresetFlags.Where<KeyValuePair<string, string>>((KeyValuePair<string, string> x) => x.Key.StartsWith("UI.Menu.Style." + flag2.Key)))
					{
						if (App.FastFlags.GetValue(item.Value) != flag2.Value)
						{
							flag = false;
						}
					}
				}
				if (flag)
				{
					return iGMenuVersion.Key;
				}
			}
			return IGMenuVersions.First().Key;
		}
		set
		{
			foreach (KeyValuePair<string, string> item in IGMenuVersions[value])
			{
				App.FastFlags.SetPreset("UI.Menu.Style." + item.Key, item.Value);
			}
		}
	}

	public IReadOnlyDictionary<LightingMode, string> LightingModes => FastFlagManager.LightingModes;

	public LightingMode SelectedLightingMode
	{
		get
		{
			return App.FastFlags.GetPresetEnum(LightingModes, "Rendering.Lighting", "True");
		}
		set
		{
			App.FastFlags.SetPresetEnum("Rendering.Lighting", LightingModes[value], "True");
		}
	}

	public bool FullscreenTitlebarDisabled
	{
		get
		{
			if (int.TryParse(App.FastFlags.GetPreset("UI.FullscreenTitlebarDelay"), out var result))
			{
				return result > 5000;
			}
			return false;
		}
		set
		{
			App.FastFlags.SetPreset("UI.FullscreenTitlebarDelay", value ? "3600000" : null);
		}
	}

	public IReadOnlyDictionary<TextureSkipping, string?> TextureSkippings => FastFlagManager.TextureSkippingSkips;

	public TextureSkipping SelectedTextureSkipping
	{
		get
		{
			return TextureSkippings.FirstOrDefault<KeyValuePair<TextureSkipping, string>>((KeyValuePair<TextureSkipping, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.TextureSkipping.Skips")).Key;
		}
		set
		{
			if (value == TextureSkipping.Noskip)
			{
				App.FastFlags.SetPreset("Rendering.TextureSkipping", null);
			}
			else
			{
				App.FastFlags.SetPreset("Rendering.TextureSkipping.Skips", TextureSkippings[value]);
			}
		}
	}

	public IReadOnlyDictionary<DistanceRendering, string?> DistanceRenderings => FastFlagManager.DistanceRenderings;

	public DistanceRendering SelectedDistanceRendering
	{
		get
		{
			return DistanceRenderings.FirstOrDefault<KeyValuePair<DistanceRendering, string>>((KeyValuePair<DistanceRendering, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.Distance.Chunks")).Key;
		}
		set
		{
			if (value == DistanceRendering.Chunks1x)
			{
				App.FastFlags.SetPreset("Rendering.Distance.Chunks", null);
			}
			else
			{
				App.FastFlags.SetPreset("Rendering.Distance.Chunks", DistanceRenderings[value]);
			}
		}
	}

	public IReadOnlyDictionary<int, string?> GrassMovementOptions { get; } = new Dictionary<int, string>
	{
		{ 0, "No Movement" },
		{ 1, "Minimal Movement" },
		{ 2, "Medium Movement" },
		{ 3, "High Movement" },
		{ 4, "Ultra Movement" },
		{ 5, "Default Movement" }
	};

	public int SelectedGrassMovementFactor
	{
		get
		{
			if (int.TryParse(App.FastFlags.GetPreset("Grass.Movement"), out var result) && GrassMovementOptions.ContainsKey(result))
			{
				return result;
			}
			return 5;
		}
		set
		{
			if (value == 5)
			{
				App.FastFlags.SetPreset("Grass.Movement", null);
			}
			else
			{
				App.FastFlags.SetPreset("Grass.Movement", value.ToString());
			}
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("SelectedGrassMovementFactor"));
		}
	}

	public IReadOnlyDictionary<DynamicResolution, string?> DynamicResolutions => FastFlagManager.DynamicResolutions;

	public DynamicResolution SelectedDynamicResolution
	{
		get
		{
			return DynamicResolutions.FirstOrDefault<KeyValuePair<DynamicResolution, string>>((KeyValuePair<DynamicResolution, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.Dynamic.Resolution")).Key;
		}
		set
		{
			if (value == DynamicResolution.Resolution2)
			{
				App.FastFlags.SetPreset("Rendering.Dynamic.Resolution", null);
			}
			else
			{
				App.FastFlags.SetPreset("Rendering.Dynamic.Resolution", DynamicResolutions[value]);
			}
		}
	}

	public IReadOnlyDictionary<RomarkStart, string?> RomarkStartMappings => FastFlagManager.RomarkStartMappings;

	public RomarkStart SelectedRomarkStart
	{
		get
		{
			return FastFlagManager.RomarkStartMappings.FirstOrDefault<KeyValuePair<RomarkStart, string>>((KeyValuePair<RomarkStart, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.Start.Graphic")).Key;
		}
		set
		{
			if (value == RomarkStart.Disabled)
			{
				App.FastFlags.SetPreset("Rendering.Start.Graphic", null);
			}
			else
			{
				App.FastFlags.SetPreset("Rendering.Start.Graphic", FastFlagManager.RomarkStartMappings[value]);
			}
		}
	}

	public IReadOnlyDictionary<Presents, string?> PresentsLevels => FastFlagManager.PresentsStartMappings;

	public Presents SelectedPresentsLevel
	{
		get
		{
			return PresentsLevels.FirstOrDefault<KeyValuePair<Presents, string>>((KeyValuePair<Presents, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.MSAA")).Key;
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.MSAA", PresentsLevels[value]);
		}
	}

	public IReadOnlyDictionary<QualityLevel, string?> QualityLevels => FastFlagManager.QualityLevels;

	public QualityLevel SelectedQualityLevel
	{
		get
		{
			return FastFlagManager.QualityLevels.FirstOrDefault<KeyValuePair<QualityLevel, string>>((KeyValuePair<QualityLevel, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.FrmQuality")).Key;
		}
		set
		{
			if (value == QualityLevel.Disabled)
			{
				App.FastFlags.SetPreset("Rendering.FrmQuality", null);
			}
			else
			{
				App.FastFlags.SetPreset("Rendering.FrmQuality", FastFlagManager.QualityLevels[value]);
			}
		}
	}

	public bool DisablePostFX
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.DisablePostFX") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.DisablePostFX", value ? "True" : null);
		}
	}

	public bool TaskSchedulerAvoidingSleep
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.AvoidSleep") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.AvoidSleep", value ? "True" : null);
		}
	}

	public bool AdsToggle
	{
		get
		{
			return App.FastFlags.GetPreset("UI.Disable.Ads") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("UI.Disable.Ads", value ? "True" : null);
		}
	}

	public bool DisablePlayerShadows
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.ShadowIntensity") == "0";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.ShadowIntensity", value ? "0" : null);
			App.FastFlags.SetPreset("Rendering.Pause.Voxelizer", value ? "True" : null);
			App.FastFlags.SetPreset("Rendering.ShadowMapBias", value ? "-1" : null);
		}
	}

	public bool RenderOcclusion
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.Occlusion1") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.Occlusion1", value ? "True" : null);
			App.FastFlags.SetPreset("Rendering.Occlusion2", value ? "True" : null);
			App.FastFlags.SetPreset("Rendering.Occlusion3", value ? "True" : null);
		}
	}

	public bool EnableGraySky
	{
		get
		{
			return App.FastFlags.GetPreset("Graphic.GraySky") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Graphic.GraySky", value ? "True" : null);
		}
	}

	public int? FontSize
	{
		get
		{
			int result;
			return (!int.TryParse(App.FastFlags.GetPreset("UI.FontSize"), out result)) ? 1 : result;
		}
		set
		{
			App.FastFlags.SetPreset("UI.FontSize", (value == 1) ? ((int?)null) : value);
		}
	}

	public bool RedFont
	{
		get
		{
			return App.FastFlags.GetPreset("UI.RedFont") == "rbxasset://fonts/families/BuilderSans.json";
		}
		set
		{
			App.FastFlags.SetPreset("UI.RedFont", value ? "rbxasset://fonts/families/BuilderSans.json" : null);
		}
	}

	public bool DisableLayeredClothing
	{
		get
		{
			return App.FastFlags.GetPreset("UI.DisableLayeredClothing") == "-1";
		}
		set
		{
			App.FastFlags.SetPreset("UI.DisableLayeredClothing", value ? "-1" : null);
		}
	}

	public bool DisableTerrainTextures
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.TerrainTextureQuality") == "0";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.TerrainTextureQuality", value ? "0" : null);
		}
	}

	public bool Prerender
	{
		get
		{
			if (App.FastFlags.GetPreset("Rendering.Prerender") == "True")
			{
				return App.FastFlags.GetPreset("Rendering.PrerenderV2") == "True";
			}
			return false;
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.Prerender", value ? "True" : null);
			App.FastFlags.SetPreset("Rendering.PrerenderV2", value ? "True" : null);
		}
	}

	public string ForceBuggyVulkan
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.ForceVulkan") ?? "Automatic";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.ForceVulkan", (value == "Automatic") ? null : value);
		}
	}

	public bool PartyToggle
	{
		get
		{
			return App.FastFlags.GetPreset("VoiceChat.VoiceChat4") == "False";
		}
		set
		{
			string value2 = (value ? "False" : "True");
			App.FastFlags.SetPreset("VoiceChat.VoiceChat4", value2);
			App.FastFlags.SetPreset("VoiceChat.VoiceChat5", value2);
		}
	}

	public bool ChromeUI
	{
		get
		{
			if (App.FastFlags.GetPreset("UI.Menu.ChromeUI") == "True")
			{
				return App.FastFlags.GetPreset("UI.Menu.ChromeUI2") == "True";
			}
			return false;
		}
		set
		{
			App.FastFlags.SetPreset("UI.Menu.ChromeUI", value ? "True" : null);
			App.FastFlags.SetPreset("UI.Menu.ChromeUI2", value ? "True" : null);
		}
	}

	public bool VRToggle
	{
		get
		{
			return GetFlagAsBool("Menu.VRToggles");
		}
		set
		{
			SetFlagFromBool("Menu.VRToggles", value);
		}
	}

	public bool SoothsayerCheck
	{
		get
		{
			return GetFlagAsBool("Menu.Feedback");
		}
		set
		{
			SetFlagFromBool("Menu.Feedback", value);
		}
	}

	public bool LanguageSelector
	{
		get
		{
			return App.FastFlags.GetPreset("Menu.LanguageSelector") != "0";
		}
		set
		{
			SetFlagFromBool("Menu.LanguageSelector", value, "0");
		}
	}

	public bool Haptics
	{
		get
		{
			return GetFlagAsBool("Menu.Haptics");
		}
		set
		{
			SetFlagFromBool("Menu.Haptics", value);
		}
	}

	public bool ChatTranslation
	{
		get
		{
			return GetFlagAsBool("Menu.ChatTranslation");
		}
		set
		{
			SetFlagFromBool("Menu.ChatTranslation", value);
		}
	}

	public bool FrameRateCap
	{
		get
		{
			return GetFlagAsBool("Menu.Framerate");
		}
		set
		{
			SetFlagFromBool("Menu.Framerate", value);
		}
	}

	public bool DisableVoiceChatTelemetry
	{
		get
		{
			return App.FastFlags?.GetPreset("Telemetry.Voicechat1") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("Telemetry.Voicechat1", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat2", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat3", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat4", value ? "0" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat5", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat6", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat7", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat8", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat9", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat10", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat11", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat12", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat13", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat14", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat15", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat16", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat17", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat18", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat19", value ? "0" : null);
			App.FastFlags.SetPreset("Telemetry.Voicechat20", value ? "-1" : null);
		}
	}

	public bool OldChromeUI
	{
		get
		{
			return App.FastFlags?.GetPreset("UI.OldChromeUI1") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("UI.OldChromeUI1", value ? "False" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI2", value ? "False" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI3", value ? "False" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI4", value ? "False" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI5", value ? "False" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI6", value ? "False" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI7", value ? "False" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI8", value ? "True" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI9", value ? "False" : null);
			App.FastFlags.SetPreset("UI.OldChromeUI10", value ? "False" : null);
		}
	}

	public bool BlockTencent
	{
		get
		{
			return App.FastFlags?.GetPreset("Telemetry.Tencent1") == "/tencent/";
		}
		set
		{
			App.FastFlags.SetPreset("Telemetry.Tencent1", value ? "/tencent/" : null);
			App.FastFlags.SetPreset("Telemetry.Tencent2", value ? "/tencent/" : null);
			App.FastFlags.SetPreset("Telemetry.Tencent3", value ? "https://www.gov.cn" : null);
			App.FastFlags.SetPreset("Telemetry.Tencent4", value ? "https://www.gov.cn" : null);
			App.FastFlags.SetPreset("Telemetry.Tencent5", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Tencent6", value ? "False" : null);
			App.FastFlags.SetPreset("Telemetry.Tencent7", value ? "10000" : null);
		}
	}

	public bool WhiteSky
	{
		get
		{
			return App.FastFlags.GetPreset("Graphic.WhiteSky") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Graphic.WhiteSky", value ? "True" : null);
			App.FastFlags.SetPreset("Graphic.GraySky", value ? "True" : null);
		}
	}

	public bool ShowChunks
	{
		get
		{
			return App.FastFlags.GetPreset("Debug.Chunks") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Debug.Chunks", value ? "True" : null);
		}
	}

	public bool Pseudolocalization
	{
		get
		{
			return App.FastFlags.GetPreset("UI.Pseudolocalization") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("UI.Pseudolocalization", value ? "True" : null);
		}
	}

	public bool ResetConfiguration
	{
		get
		{
			return _preResetFlags != null;
		}
		set
		{
			if (value)
			{
				_preResetFlags = new Dictionary<string, object>(App.FastFlags.Prop);
				App.FastFlags.Prop.Clear();
			}
			else
			{
				App.FastFlags.Prop = _preResetFlags;
				_preResetFlags = null;
			}
			this.RequestPageReloadEvent?.Invoke(this, EventArgs.Empty);
		}
	}

	public int FPSBufferPercentage
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Rendering.FrameRateBufferPercentage"), out var result))
			{
				return 0;
			}
			return result;
		}
		set
		{
			int num = Math.Max(0, Math.Min(100, value));
			App.FastFlags.SetPreset("Rendering.FrameRateBufferPercentage", (num >= 1) ? num.ToString() : null);
		}
	}

	public bool BetterPacketSending
	{
		get
		{
			return App.FastFlags?.GetPreset("Network.BetterPacketSending1") == "0";
		}
		set
		{
			App.FastFlags.SetPreset("Network.BetterPacketSending1", value ? "0" : null);
			App.FastFlags.SetPreset("Network.BetterPacketSending2", value ? "1" : null);
			App.FastFlags.SetPreset("Network.BetterPacketSending3", value ? "1" : null);
			App.FastFlags.SetPreset("Network.BetterPacketSending4", value ? "1" : null);
			App.FastFlags.SetPreset("Network.BetterPacketSending5", value ? "1" : null);
			App.FastFlags.SetPreset("Network.BetterPacketSending6", value ? "1047483647" : null);
			App.FastFlags.SetPreset("Network.BetterPacketSending7", value ? "5000000" : null);
			App.FastFlags.SetPreset("Network.BetterPacketSending8", value ? "1" : null);
			App.FastFlags.SetPreset("Network.BetterPacketSending9", value ? "1047483647" : null);
		}
	}

	public int BufferArrayLength
	{
		get
		{
			if (!int.TryParse(App.FastFlags.GetPreset("Recommended.Buffer"), out var result))
			{
				return 0;
			}
			return result;
		}
		set
		{
			App.FastFlags.SetPreset("Recommended.Buffer", (value == 0) ? null : ((object)value));
		}
	}

	public bool MinimalRendering
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.MinimalRendering") == "True";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.MinimalRendering", value ? "True" : null);
		}
	}

	public bool DisableSky
	{
		get
		{
			return App.FastFlags.GetPreset("Rendering.NoFrmBloom") == "False";
		}
		set
		{
			App.FastFlags.SetPreset("Rendering.NoFrmBloom", value ? "False" : null);
			App.FastFlags.SetPreset("Rendering.FRMRefactor", value ? "False" : null);
		}
	}

	public IReadOnlyDictionary<RefreshRate, string?> RefreshRates => FastFlagManager.RefreshRates;

	public RefreshRate SelectedRefreshRate
	{
		get
		{
			return RefreshRates.FirstOrDefault<KeyValuePair<RefreshRate, string>>((KeyValuePair<RefreshRate, string> x) => x.Value == App.FastFlags.GetPreset("System.TargetRefreshRate1")).Key;
		}
		set
		{
			if (value == RefreshRate.Default)
			{
				App.FastFlags.SetPreset("System.TargetRefreshRate1", null);
				App.FastFlags.SetPreset("System.TargetRefreshRate2", null);
				App.FastFlags.SetPreset("System.TargetRefreshRate3", null);
				App.FastFlags.SetPreset("System.TargetRefreshRate4", null);
			}
			else
			{
				App.FastFlags.SetPreset("System.TargetRefreshRate1", RefreshRates[value]);
				App.FastFlags.SetPreset("System.TargetRefreshRate2", RefreshRates[value]);
				App.FastFlags.SetPreset("System.TargetRefreshRate3", RefreshRates[value]);
				App.FastFlags.SetPreset("System.TargetRefreshRate4", RefreshRates[value]);
			}
		}
	}

	public IReadOnlyDictionary<Shader, string?> Shaders => FastFlagManager.Shaders;

	public Shader SelectedShaderLevel
	{
		get
		{
			return Shaders.FirstOrDefault<KeyValuePair<Shader, string>>((KeyValuePair<Shader, string> x) => x.Value == App.FastFlags.GetPreset("Rendering.Shaders")).Key;
		}
		set
		{
			if (value == Shader.Disabled)
			{
				App.FastFlags.SetPreset("Rendering.Shaders", null);
				App.FastFlags.SetPreset("Rendering.Shaders2", null);
			}
			else
			{
				App.FastFlags.SetPreset("Rendering.Shaders", Shaders[value]);
				App.FastFlags.SetPreset("Rendering.Shaders2", "21");
			}
		}
	}

	public string BypassVulkan
	{
		get
		{
			return App.FastFlags.GetPreset("System.BypassVulkan") ?? "Automatic";
		}
		set
		{
			App.FastFlags.SetPreset("System.BypassVulkan", (value == "Automatic") ? null : value);
		}
	}

	public IReadOnlyDictionary<string, string?>? CpuThreads => GetCpuThreads();

	public KeyValuePair<string, string?> SelectedCpuThreads
	{
		get
		{
			string currentValue = App.FastFlags.GetPreset("System.CpuCore1") ?? "Automatic";
			return CpuThreads?.FirstOrDefault((KeyValuePair<string, string> kvp) => kvp.Key == currentValue) ?? default(KeyValuePair<string, string>);
		}
		set
		{
			App.FastFlags.SetPreset("System.CpuCore1", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			App.FastFlags.SetPreset("System.CpuCore2", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			App.FastFlags.SetPreset("System.CpuCore3", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			App.FastFlags.SetPreset("System.CpuCore4", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			App.FastFlags.SetPreset("System.CpuCore5", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			App.FastFlags.SetPreset("System.CpuCore6", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			App.FastFlags.SetPreset("System.CpuCore7", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			App.FastFlags.SetPreset("System.CpuCore9", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			if (value.Value != null && int.TryParse(value.Value, out var result))
			{
				int num = Math.Max(result - 1, 1);
				App.FastFlags.SetPreset("System.CpuThreads", num.ToString());
				OnPropertyChanged("SelectedCpuThreads");
				App.FastFlags.SetPreset("System.CpuCore8", num.ToString());
				OnPropertyChanged("SelectedCpuThreads");
			}
			else
			{
				App.FastFlags.SetPreset("System.CpuThreads", null);
				OnPropertyChanged("SelectedCpuThreads");
				App.FastFlags.SetPreset("System.CpuCore8", null);
				OnPropertyChanged("SelectedCpuThreads");
			}
		}
	}

	public IReadOnlyDictionary<string, string?>? CpuCoreMinThreadCount => GetCpuCoreMinThreadCount();

	public KeyValuePair<string, string?> SelectedCpuCoreMinThreadCount
	{
		get
		{
			string currentValue = App.FastFlags.GetPreset("System.CpuCoreMinThreadCount") ?? "Automatic";
			return CpuThreads?.FirstOrDefault((KeyValuePair<string, string> kvp) => kvp.Key == currentValue) ?? default(KeyValuePair<string, string>);
		}
		set
		{
			App.FastFlags.SetPreset("System.CpuCoreMinThreadCount", value.Value);
			OnPropertyChanged("SelectedCpuThreads");
			if (value.Value != null && int.TryParse(value.Value, out var result))
			{
				int num = Math.Max(result - 1, 1);
				App.FastFlags.SetPreset("System.CpuCoreMinThreadCount", num.ToString());
			}
			else
			{
				App.FastFlags.SetPreset("System.CpuCoreMinThreadCount", null);
			}
			OnPropertyChanged("SelectedCpuCoreMinThreadCount");
		}
	}

	public IEnumerable? ProfileModes
	{
		get
		{
			return profileModes;
		}
		set
		{
			SetProperty(ref profileModes, value, "ProfileModes");
		}
	}

	public string SelectedProfileMods
	{
		get
		{
			return selectedProfileMods;
		}
		set
		{
			SetProperty(ref selectedProfileMods, value, "SelectedProfileMods");
		}
	}

	public bool IsWindows => Voidstrap.Utility.Platform.IsWindows;

	// true when any installed adapter is NVIDIA, so a hybrid laptop with an Intel or
	// AMD integrated part plus an NVIDIA discrete one still gets access
	public bool HasNvidiaGpu => Voidstrap.Utility.GpuInventory.HasNvidia;

	public string NvidiaTabUnavailableReason => Voidstrap.Utility.GpuInventory.HasNvidia
		? string.Empty
		: "These options need an NVIDIA graphics card. Detected: " + Voidstrap.Utility.GpuInventory.Summary + ".";

	public bool AssetWarpEnabled
	{
		get => App.Settings.Prop.AssetWarpEnabled;
		set
		{
			if (App.Settings.Prop.AssetWarpEnabled == value)
			{
				return;
			}
			if (value && Frontend.ShowMessageBox("AssetWarp redirects selected Roblox asset requests through a secure local proxy. When an AssetWarp feature requires the proxy, Voidstrap will request administrator permission and temporarily install a local certificate so Roblox can trust the connection. The certificate and routing changes are removed when AssetWarp stops. Enable AssetWarp?", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
			{
				OnPropertyChanged(nameof(AssetWarpEnabled));
				return;
			}
			App.Settings.Prop.AssetWarpEnabled = value;
			if (value)
			{
				try
				{
					AssetProxyServer.PrepareCertificate();
					App.Settings.Prop.AssetWarpCertificateApproved = true;
				}
				catch (Exception ex)
				{
					App.Settings.Prop.AssetWarpEnabled = false;
					App.Settings.Prop.AssetWarpCertificateApproved = false;
					Frontend.ShowMessageBox("AssetWarp could not install its local certificate: " + ex.Message, MessageBoxImage.Error);
					OnPropertyChanged(nameof(AssetWarpEnabled));
					return;
				}
			}
			else
			{
				App.Settings.Prop.AssetWarpCertificateApproved = false;
			}
			AssetProxyServer.ReconcileRuntimeState();
			App.FastFlags.SaveDeferred();
			App.Settings.SaveDeferred();
			if (!value && Voidstrap.Utility.Platform.IsLinux)
			{
				_ = RemoveLinuxAssetWarpCertificateAsync();
			}
			OnPropertyChanged(nameof(AssetWarpEnabled));
		}
	}

	private static async Task RemoveLinuxAssetWarpCertificateAsync()
	{
		const string logIdent = "FastFlagsViewModel::AssetWarpEnabled";
		try
		{
			await Task.Run(AssetProxyServer.Stop).ConfigureAwait(false);
			Voidstrap.Platform.OperationResult result = await LinuxAssetWarpBridge.RemoveCertificateAsync().ConfigureAwait(false);
			if (result.Succeeded)
			{
				App.Logger?.WriteLine(logIdent, "AssetWarp certificate removed from the system trust store");
				return;
			}

			if (string.Equals(result.Failure?.Code, "TrustStoreAuthorizationDeclined", StringComparison.Ordinal))
			{
				App.Logger?.WriteLine(logIdent, "AssetWarp certificate removal was not authorized, the certificate is still trusted");
				return;
			}

			ReportCertificateRemovalFailure(logIdent, result.Failure?.Message ?? "unknown failure");
		}
		catch (Exception ex)
		{
			ReportCertificateRemovalFailure(logIdent, ex.Message);
		}
	}

	private static void ReportCertificateRemovalFailure(string logIdent, string reason)
	{
		App.Logger?.WriteLine(logIdent, "AssetWarp certificate removal failed: " + reason);
		try
		{
			Frontend.ShowMessageBox(
				"AssetWarp could not remove its local certificate from the system trust store, so it is still trusted. You can remove it with your system certificate tools. Reason: " + reason,
				MessageBoxImage.Warning);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(logIdent, "The certificate removal warning could not be shown: " + ex.Message);
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
				OnPropertyChanged("DisableAllTextures");
			}
		}
	}

	public bool DisableAllDecals
	{
		get
		{
			return App.Settings.Prop.AssetWarpDisableAllDecals;
		}
		set
		{
			if (App.Settings.Prop.AssetWarpDisableAllDecals != value)
			{
				App.Settings.Prop.AssetWarpDisableAllDecals = value;
				AssetProxyServer.ReconcileRuntimeState();
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
				OnPropertyChanged(nameof(DisableAllDecals));
			}
		}
	}

	public bool DisableAllImages
	{
		get
		{
			return App.Settings.Prop.AssetWarpDisableAllImages;
		}
		set
		{
			if (App.Settings.Prop.AssetWarpDisableAllImages != value)
			{
				App.Settings.Prop.AssetWarpDisableAllImages = value;
				AssetProxyServer.ReconcileRuntimeState();
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
				OnPropertyChanged(nameof(DisableAllImages));
			}
		}
	}

	public bool DisableAllAnimations
	{
		get
		{
			return App.Settings.Prop.AssetWarpDisableAllAnimations;
		}
		set
		{
			if (App.Settings.Prop.AssetWarpDisableAllAnimations != value)
			{
				App.Settings.Prop.AssetWarpDisableAllAnimations = value;
				AssetProxyServer.ReconcileRuntimeState();
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
				OnPropertyChanged(nameof(DisableAllAnimations));
			}
		}
	}

	public bool DisableAllMeshes
	{
		get
		{
			return App.Settings.Prop.AssetWarpDisableAllMeshes;
		}
		set
		{
			if (App.Settings.Prop.AssetWarpDisableAllMeshes != value)
			{
				App.Settings.Prop.AssetWarpDisableAllMeshes = value;
				AssetProxyServer.ReconcileRuntimeState();
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
				OnPropertyChanged(nameof(DisableAllMeshes));
			}
		}
	}

	public bool AssetWarpPreloadEnabled
	{
		get => App.Settings.Prop.AssetWarpPreloadEnabled;
		set
		{
			if (App.Settings.Prop.AssetWarpPreloadEnabled == value)
			{
				return;
			}
			if (value && Frontend.ShowMessageBox("Preloading makes some content load more slowly at first to reduce stuttering. You will most likely notice more input lag. Enable preloading anyway?", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
			{
				OnPropertyChanged(nameof(AssetWarpPreloadEnabled));
				return;
			}
			App.Settings.Prop.AssetWarpPreloadEnabled = value;
			App.FastFlags.ApplyPreloadFlags();
			App.FastFlags.SaveDeferred();
			AssetProxyServer.ReconcileRuntimeState();
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(AssetWarpPreloadEnabled));
		}
	}

	public int AssetWarpPreloadCacheMb
	{
		get => Math.Clamp(App.Settings.Prop.AssetWarpPreloadCacheMb, 256, 8192);
		set
		{
			int normalized = Math.Clamp(value, 256, 8192);
			if (App.Settings.Prop.AssetWarpPreloadCacheMb == normalized)
			{
				return;
			}
			App.Settings.Prop.AssetWarpPreloadCacheMb = normalized;
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(AssetWarpPreloadCacheMb));
			OnPropertyChanged(nameof(AssetWarpPreloadCacheSizeDisplay));
		}
	}

	public string AssetWarpPreloadCacheSizeDisplay => AssetWarpPreloadCacheMb >= 1024
		? $"{AssetWarpPreloadCacheMb / 1024.0:0.#} GB"
		: $"{AssetWarpPreloadCacheMb} MB";

	public bool AssetWarpPreloadAvatar
	{
		get => App.Settings.Prop.AssetWarpPreloadAvatar;
		set
		{
			if (App.Settings.Prop.AssetWarpPreloadAvatar == value)
			{
				return;
			}
			App.Settings.Prop.AssetWarpPreloadAvatar = value;
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(AssetWarpPreloadAvatar));
		}
	}

	public bool AssetWarpPreloadCrossGame
	{
		get => App.Settings.Prop.AssetWarpPreloadCrossGame;
		set
		{
			if (App.Settings.Prop.AssetWarpPreloadCrossGame == value)
			{
				return;
			}
			App.Settings.Prop.AssetWarpPreloadCrossGame = value;
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(AssetWarpPreloadCrossGame));
		}
	}

	public int PresenceSpoofMode
	{
		get
		{
			return Math.Clamp(App.Settings.Prop.PresenceSpoofMode, 0, 3);
		}
		set
		{
			int mode = Math.Clamp(value, 0, 3);
			if (App.Settings.Prop.PresenceSpoofMode == mode)
			{
				return;
			}
			App.Settings.Prop.PresenceSpoofMode = mode;
			AssetProxyServer.ReconcileRuntimeState();
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(PresenceSpoofMode));
		}
	}

	public string SpoofOthersName
	{
		get => UsernameSpoofer.CurrentState.OthersName;
		set => UpdateUsernameSpooferState(state => state with { OthersName = value ?? "" }, nameof(SpoofOthersName));
	}

	public bool SpoofOthersApplyIngame
	{
		get => UsernameSpoofer.CurrentState.OthersApplyIngame;
		set => UpdateUsernameSpooferState(state => state with { OthersApplyIngame = value }, nameof(SpoofOthersApplyIngame));
	}

	public bool SpoofOthersVerified
	{
		get => UsernameSpoofer.CurrentState.OthersVerified;
		set => UpdateUsernameSpooferState(state => state with { OthersVerified = value }, nameof(SpoofOthersVerified));
	}

	public string SpoofSelfName
	{
		get => UsernameSpoofer.CurrentState.SelfName;
		set => UpdateUsernameSpooferState(state => state with { SelfName = value ?? "" }, nameof(SpoofSelfName));
	}

	public bool SpoofSelfApplyIngame
	{
		get => UsernameSpoofer.CurrentState.SelfApplyIngame;
		set => UpdateUsernameSpooferState(state => state with { SelfApplyIngame = value }, nameof(SpoofSelfApplyIngame));
	}

	public bool SpoofSelfVerified
	{
		get => UsernameSpoofer.CurrentState.SelfVerified;
		set => UpdateUsernameSpooferState(state => state with { SelfVerified = value }, nameof(SpoofSelfVerified));
	}

	public bool SpoofSelfGameCreator
	{
		get => UsernameSpoofer.CurrentState.SelfGameCreator;
		set => UpdateUsernameSpooferState(state => state with { SelfGameCreator = value }, nameof(SpoofSelfGameCreator));
	}

	private void UpdateUsernameSpooferState(Func<UsernameSpoofState, UsernameSpoofState> update, string propertyName)
	{
		UsernameSpoofState previous = UsernameSpoofer.CurrentState;
		UsernameSpoofState next = update(previous);
		if (next == previous)
		{
			return;
		}
		UsernameSpoofer.SetRuntimeState(next);
		PersistUsernameSpooferState(next);
		AssetProxyServer.ReconcileRuntimeState();
		OnPropertyChanged(propertyName);
	}

	private static void PersistUsernameSpooferState(UsernameSpoofState state)
	{
		var settings = App.Settings.Prop;
		settings.SpoofOthersName = state.OthersName;
		settings.SpoofOthersApplyIngame = state.OthersApplyIngame;
		settings.SpoofOthersVerified = state.OthersVerified;
		settings.SpoofSelfName = state.SelfName;
		settings.SpoofSelfApplyIngame = state.SelfApplyIngame;
		settings.SpoofSelfVerified = state.SelfVerified;
		settings.SpoofSelfGameCreator = state.SelfGameCreator;
		App.Settings.SaveDeferred();
	}

	private string _assetWarpStatus = "";

	public string AssetWarpStatus
	{
		get
		{
			return _assetWarpStatus;
		}
		set
		{
			_assetWarpStatus = value;
			OnPropertyChanged("AssetWarpStatus");
		}
	}

	public event EventHandler? RequestPageReloadEvent;

	public event EventHandler? OpenFlagEditorEvent;

	public new event PropertyChangedEventHandler? PropertyChanged;

	private void OpenFastFlagEditor()
	{
		this.OpenFlagEditorEvent?.Invoke(this, EventArgs.Empty);
	}

	public bool GetFlagAsBool(string flagKey, string falseValue = "False")
	{
		return App.FastFlags.GetPreset(flagKey) != falseValue;
	}

	public void SetFlagFromBool(string flagKey, bool value, string falseValue = "False")
	{
		App.FastFlags.SetPreset(flagKey, value ? null : falseValue);
	}

	public static IReadOnlyDictionary<string, string?> GetCpuThreads()
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Automatic"] = null };
		try
		{
			int logicalProcessorCount = SystemInfo.GetLogicalProcessorCount();
			if (logicalProcessorCount > 0)
			{
				for (int i = 1; i <= logicalProcessorCount; i++)
				{
					string text = i.ToString();
					dictionary[text] = text;
				}
			}
			else
			{
				App.Logger.WriteLine("FFlagPresets::GetCpuThreads", "Logical processor count returned 0.");
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("FFlagPresets::GetCpuThreads", "Failed to get CPU thread count: " + ex.Message);
		}
		return dictionary;
	}

	public static IReadOnlyDictionary<string, string?> GetCpuCoreMinThreadCount()
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		dictionary.Add("Automatic", null);
		try
		{
			int logicalProcessorCount = SystemInfo.GetLogicalProcessorCount();
			for (int i = 1; i <= logicalProcessorCount; i++)
			{
				dictionary.Add(i.ToString(), i.ToString());
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("FFlagPresets::GetCpuCoreMinThreadCount", "Failed to get CPU thread count: " + ex.Message);
		}
		return dictionary;
	}

	protected new bool SetProperty<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
	{
		if (!object.Equals(field, newValue))
		{
			field = newValue;
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
			return true;
		}
		return false;
	}
}
