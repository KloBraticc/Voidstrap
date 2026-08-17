using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Settings;

public class VoidstrapViewModel : NotifyPropertyChangedViewModel
{
	private const string HardwareAccelerationRestartKey = "application.hardwareAcceleration";

	private bool _hardwareAccelerationEnabled;

	public VoidstrapViewModel()
	{
		bool savedHardwareAccelerationDisabled = App.Settings.Prop.WPFSoftwareRender;
		RestartNotificationService.RegisterSetting(HardwareAccelerationRestartKey, savedHardwareAccelerationDisabled);
		bool disabled = RestartNotificationService.TryGetPendingValue(HardwareAccelerationRestartKey, out bool pendingHardwareAccelerationDisabled)
			? pendingHardwareAccelerationDisabled
			: savedHardwareAccelerationDisabled;
		_hardwareAccelerationEnabled = !disabled;
	}

	public bool ShouldExportConfig { get; set; } = true;

	public bool HWAsselEnabled
	{
		get
		{
			return _hardwareAccelerationEnabled;
		}
		set
		{
			if (_hardwareAccelerationEnabled == value)
				return;
			_hardwareAccelerationEnabled = value;
			OnPropertyChanged(nameof(HWAsselEnabled));
			RestartNotificationService.TrackApplicationSetting(
				HardwareAccelerationRestartKey,
				!value,
				"Hardware acceleration changed",
				value ? Strings.Menu_Channel_HWAccel_EnableRestart : Strings.Menu_Channel_HWAccel_DisableRestart,
				value ? ApplyHardwareAccelerationEnabled : ApplyHardwareAccelerationDisabled);
		}
	}

	private static void ApplyHardwareAccelerationDisabled()
	{
		App.Settings.Prop.WPFSoftwareRender = true;
		App.Settings.SaveDeferred();
	}

	private static void ApplyHardwareAccelerationEnabled()
	{
		App.Settings.Prop.WPFSoftwareRender = false;
		App.Settings.SaveDeferred();
	}

	public bool ShouldExportLogs { get; set; } = true;

	public ICommand ExportDataCommand => new RelayCommand(ExportData);

	private void ExportData()
	{
		string text = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			FileName = "Voidstrap-export-" + text + ".zip",
			Filter = Strings.FileTypes_ZipArchive + "|*.zip"
		};
		if (saveFileDialog.ShowDialog() != true)
		{
			return;
		}
		using MemoryStream memoryStream = new MemoryStream();
		using ZipOutputStream zipOutputStream = new ZipOutputStream(memoryStream);
		if (ShouldExportConfig)
		{
			List<string> files = new List<string>
			{
				App.Settings.FileLocation,
				App.State.FileLocation,
				App.FastFlags.FileLocation
			};
			AddFilesToZipStream(zipOutputStream, files, "Config/");
		}
		if (ShouldExportLogs && Directory.Exists(Paths.Logs))
		{
			IEnumerable<string> files2 = from x in Directory.GetFiles(Paths.Logs)
				where !x.Equals(App.Logger.FileLocation, StringComparison.OrdinalIgnoreCase)
				select x;
			AddFilesToZipStream(zipOutputStream, files2, "Logs/");
		}
		zipOutputStream.CloseEntry();
		zipOutputStream.Finish();
		memoryStream.Position = 0L;
		using FileStream destination = new(saveFileDialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
		memoryStream.CopyTo(destination);
		Process.Start("explorer.exe", "/select,\"" + saveFileDialog.FileName + "\"");
	}

	private void AddFilesToZipStream(ZipOutputStream zipStream, IEnumerable<string> files, string directory)
	{
		foreach (string file in files)
		{
			if (File.Exists(file))
			{
				ZipEntry zipEntry = new ZipEntry(directory + Path.GetFileName(file));
				zipEntry.DateTime = DateTime.Now;
				zipStream.PutNextEntry(zipEntry);
				using FileStream fileStream = File.OpenRead(file);
				fileStream.CopyTo(zipStream);
			}
		}
	}
}
