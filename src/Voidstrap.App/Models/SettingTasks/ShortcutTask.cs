using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SixLabors.ImageSharp.Processing;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.Models.SettingTasks;

public sealed class ShortcutAppearance
{
	public string Name { get; set; } = "";
	public BootstrapperIcon Icon { get; set; } = BootstrapperIcon.IconVoidstrap;
	public string CustomIconPath { get; set; } = "";
	public string IconFilePath { get; set; } = "";
}

public sealed record ShortcutIconEntry(BootstrapperIcon IconType, ImageSource? ImageSource);

public class ShortcutTask : BoolBaseTask, INotifyPropertyChanged
{
	private readonly string _key;
	private readonly string _folder;
	private readonly string _defaultName;
	private readonly string _exeFlags;
	private readonly bool _customizable;
	private string _shortcutPath;
	private string _shortcutName;
	private BootstrapperIcon _selectedIcon;
	private string _customIconPath;
	private string _originalName;
	private BootstrapperIcon _originalIcon;
	private string _originalCustomIconPath;
	private static readonly Lazy<ShortcutIconEntry[]> Presets = new(() => BootstrapperIconEx.Selections
		.Select(icon => new ShortcutIconEntry(icon, icon == BootstrapperIcon.IconCustom ? null
			: LoadPreset(icon, 32))).ToArray());

	public event PropertyChangedEventHandler? PropertyChanged;

	public ShortcutIconEntry[] Icons => Presets.Value;

	public string ShortcutName
	{
		get => _shortcutName;
		set
		{
			if (_shortcutName == value)
				return;
			_shortcutName = value ?? "";
			UpdatePending(nameof(ShortcutName));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValidationMessage)));
		}
	}

	public BootstrapperIcon SelectedIcon
	{
		get => _selectedIcon;
		set
		{
			if (!Enum.IsDefined(value) || _selectedIcon == value)
				return;
			_selectedIcon = value;
			UpdatePending(nameof(SelectedIcon));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomIconVisibility)));
		}
	}

	public string CustomIconPath => _customIconPath;

	public Visibility CustomIconVisibility => SelectedIcon == BootstrapperIcon.IconCustom ? Visibility.Visible : Visibility.Collapsed;

	public string ValidationMessage => IsValidName(ResolvedName) ? "" : Strings.Menu_Shortcuts_InvalidName;

	public ICommand BrowseIconCommand => new RelayCommand(BrowseIcon);

	private string ResolvedName => string.IsNullOrWhiteSpace(_shortcutName) ? _defaultName : _shortcutName.Trim();

	private bool AppearanceChanged => _shortcutName != _originalName
		|| _selectedIcon != _originalIcon || _customIconPath != _originalCustomIconPath;

	public override bool Changed => base.Changed || (_customizable && AppearanceChanged);

	public ShortcutTask(string name, string lnkFolder, string lnkName, string exeFlags = "", bool customizable = false)
		: base("Shortcut", name)
	{
		_key = name;
		_folder = lnkFolder;
		_defaultName = Path.GetFileNameWithoutExtension(lnkName);
		_exeFlags = exeFlags;
		_customizable = customizable;
		ShortcutAppearance? appearance = null;
		if (customizable)
		{
			App.Settings.Prop.ShortcutAppearances ??= new();
			App.Settings.Prop.ShortcutAppearances.TryGetValue(name, out appearance);
		}
		_shortcutName = IsValidName(appearance?.Name) ? appearance!.Name : _defaultName;
		_selectedIcon = appearance != null && Enum.IsDefined(appearance.Icon) ? appearance.Icon : BootstrapperIcon.IconVoidstrap;
		_customIconPath = appearance?.CustomIconPath ?? "";
		_originalName = _shortcutName;
		_originalIcon = _selectedIcon;
		_originalCustomIconPath = _customIconPath;
		_shortcutPath = Path.Combine(lnkFolder, _shortcutName + Path.GetExtension(lnkName));
		OriginalState = File.Exists(_shortcutPath);
	}

	private void UpdatePending(string property)
	{
		NewState = NewState;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
	}

	private void BrowseIcon()
	{
		var dialog = new OpenFileDialog
		{
			Title = Strings.Menu_Shortcuts_ChooseIcon,
			Filter = Strings.Menu_Shortcuts_IconFilter,
			CheckFileExists = true
		};
		if (dialog.ShowDialog() != true)
			return;
		try
		{
			if (new FileInfo(dialog.FileName).Length > 8 * 1024 * 1024 || SafeImaging.FromFile(dialog.FileName, 256) == null)
				throw new IOException(Strings.Menu_Shortcuts_InvalidIcon);
			_customIconPath = dialog.FileName;
			UpdatePending(nameof(CustomIconPath));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Voidstrap.UI.Frontend.ShowMessageBox(Strings.Menu_Shortcuts_InvalidIcon, MessageBoxImage.Warning);
		}
	}

	internal static bool IsValidName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.EndsWith('.')
			|| name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
			return false;
		string stem = name.Split('.')[0].ToUpperInvariant();
		return stem is not ("CON" or "PRN" or "AUX" or "NUL")
			&& !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
				&& stem[3] >= '1' && stem[3] <= '9');
	}

	private static BitmapSource? LoadPreset(BootstrapperIcon icon, int size)
	{
		return Voidstrap.Utility.Platform.IsWindows ? IconEx.GetIconSource(icon) as BitmapSource : IconEx.LoadPortableIcon(icon, size);
	}

	private string PrepareIcon()
	{
		BitmapSource? source = SelectedIcon == BootstrapperIcon.IconCustom
			? (File.Exists(_customIconPath) && new FileInfo(_customIconPath).Length <= 8 * 1024 * 1024 ? SafeImaging.FromFile(_customIconPath, 256) : null)
			: LoadPreset(SelectedIcon, 256);
		if (source == null)
			throw new IOException(Strings.Menu_Shortcuts_InvalidIcon);
		var pixelsSource = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
		int stride = checked(pixelsSource.PixelWidth * 4);
		byte[] pixels = new byte[checked(stride * pixelsSource.PixelHeight)];
		pixelsSource.CopyPixels(pixels, stride, 0);
		using var image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(pixels, pixelsSource.PixelWidth, pixelsSource.PixelHeight);
		image.Mutate(context => context.Resize(new ResizeOptions
		{
			Size = new SixLabors.ImageSharp.Size(256, 256), Mode = SixLabors.ImageSharp.Processing.ResizeMode.Pad
		}));
		using var buffer = new MemoryStream();
		image.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
		byte[] png = buffer.ToArray();
		string folder = Path.Combine(Paths.Config, "ShortcutIcons");
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, _key + "_" + Convert.ToHexString(SHA256.HashData(png)).Substring(0, 16) + (Voidstrap.Utility.Platform.IsLinux ? ".png" : ".ico"));
		if (!File.Exists(path))
		{
			string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				using (var stream = File.Create(temporary))
				{
					if (!Voidstrap.Utility.Platform.IsLinux)
					{
						using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
						writer.Write((ushort)0);
						writer.Write((ushort)1);
						writer.Write((ushort)1);
						writer.Write((byte)0);
						writer.Write((byte)0);
						writer.Write((ushort)0);
						writer.Write((ushort)1);
						writer.Write((ushort)32);
						writer.Write((uint)png.Length);
						writer.Write((uint)22);
					}
					stream.Write(png);
				}
				File.Move(temporary, path, true);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}
		return path;
	}

	public override void Execute()
	{
		string target = _shortcutPath;
		string icon = Paths.Application;
		string resolvedName = ResolvedName;
		var pathComparison = Voidstrap.Utility.Platform.IsLinux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		if (_customizable)
		{
			if (!IsValidName(resolvedName))
				throw new IOException(Strings.Menu_Shortcuts_InvalidName);
			string requestedName = resolvedName;
			int suffix = 1;
			target = Path.Combine(_folder, resolvedName + Path.GetExtension(_shortcutPath));
			while ((NewState || AppearanceChanged) && (File.Exists(target) || Directory.Exists(target))
				&& !(OriginalState && string.Equals(target, _shortcutPath, pathComparison)))
			{
				string number = " (" + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
				resolvedName = requestedName.Substring(0, Math.Min(requestedName.Length, 100 - number.Length)) + number;
				target = Path.Combine(_folder, resolvedName + Path.GetExtension(_shortcutPath));
			}
			icon = NewState || _selectedIcon != _originalIcon || _customIconPath != _originalCustomIconPath ? PrepareIcon()
				: App.Settings.Prop.ShortcutAppearances.GetValueOrDefault(_key)?.IconFilePath ?? Paths.Application;
		}
		if (NewState)
		{
			if (!Shortcut.Create(Paths.Application, _exeFlags, target, icon, _customizable))
				throw new IOException(Strings.Dialog_CannotCreateShortcuts);
			if (!string.Equals(target, _shortcutPath, pathComparison) && OriginalState && File.Exists(_shortcutPath))
			{
				try
				{
					File.Delete(_shortcutPath);
				}
				catch
				{
					File.Delete(target);
					throw;
				}
			}
		}
		else if (OriginalState && File.Exists(_shortcutPath))
		{
			File.Delete(_shortcutPath);
		}
		if (_customizable)
		{
			if (SelectedIcon == BootstrapperIcon.IconCustom && (NewState || AppearanceChanged))
				_customIconPath = icon;
			App.Settings.Prop.ShortcutAppearances[_key] = new ShortcutAppearance
			{
				Name = resolvedName, Icon = SelectedIcon, CustomIconPath = _customIconPath, IconFilePath = icon
			};
			App.Settings.SaveDeferred();
			_shortcutName = resolvedName;
			_originalName = _shortcutName;
			_originalIcon = _selectedIcon;
			_originalCustomIconPath = _customIconPath;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortcutName)));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CustomIconPath)));
		}
		_shortcutPath = target;
		OriginalState = NewState;
	}
}
