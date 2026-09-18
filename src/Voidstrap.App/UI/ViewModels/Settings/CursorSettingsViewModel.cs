using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Enums;
using Voidstrap.Models.Attributes;
using Voidstrap.UI.Elements.Dialogs;
using Voidstrap.Utility;
using CursorType = Voidstrap.Enums.CursorType;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed class CursorSlotViewModel : NotifyPropertyChangedViewModel
{
	private readonly Func<string?> _previewPath;

	private ImageSource? _preview;

	public CursorSlotViewModel(CursorSlot slot, string title, string hint, Func<string?> previewPath, ICommand chooseCommand, ICommand clearCommand)
	{
		Slot = slot;
		Title = title;
		Hint = hint;
		_previewPath = previewPath;
		ChooseCommand = chooseCommand;
		ClearCommand = clearCommand;
	}

	public CursorSlot Slot { get; }

	public string Title { get; }

	public string Hint { get; }

	public ICommand ChooseCommand { get; }

	public ICommand ClearCommand { get; }

	public ImageSource? Preview
	{
		get => _preview;
		private set
		{
			_preview = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(HasImage));
			OnPropertyChanged(nameof(HasNoImage));
		}
	}

	public bool HasImage => _preview != null;

	public bool HasNoImage => _preview == null;

	internal void Refresh()
	{
		string? path = _previewPath();
		Preview = path == null ? null : SafeImaging.FromFile(path, 128);
	}
}

public sealed class CursorSettingsViewModel : NotifyPropertyChangedViewModel
{
	private string? _selectedSet;

	public CursorSettingsViewModel()
	{
		CursorManager.EnsureInitialized();
		Styles = Enum.GetValues<CursorType>().OrderBy(GetSortOrder).ToArray();
		Pointer = CreateCustomSlot(CursorSlot.Arrow, "Pointer", "The normal mouse cursor.");
		Drag = CreateCustomSlot(CursorSlot.Drag, "Drag", "Shown while dragging in Roblox Studio.");
		CustomSlots = new[]
		{
			Pointer,
			CreateCustomSlot(CursorSlot.ArrowFar, "Out of reach", "Shown over things too far away to click."),
			CreateCustomSlot(CursorSlot.Text, "Text", "Shown over text boxes."),
			Drag
		};
		ShiftLock = CreateCustomSlot(CursorSlot.ShiftLock, "Shift lock", "Shown while shift lock is on.");
		SetSlots = new[]
		{
			CreateSetSlot(CursorSlot.ShiftLock, "Shift lock", "Shown while shift lock is on."),
			CreateSetSlot(CursorSlot.Arrow, "Pointer", "The normal mouse cursor."),
			CreateSetSlot(CursorSlot.ArrowFar, "Out of reach", "Shown over things too far away to click."),
			CreateSetSlot(CursorSlot.Text, "Text", "Shown over text boxes."),
			CreateSetSlot(CursorSlot.Drag, "Drag", "Shown while dragging in Roblox Studio.")
		};
		ChooseCustomCursorCommand = new RelayCommand(ChooseCustomCursor);
		RemoveCustomCursorCommand = new RelayCommand(RemoveCustomCursor);
		NewSetCommand = new RelayCommand(NewSet);
		UseSetCommand = new RelayCommand(UseSet);
		CopyCurrentToSetCommand = new RelayCommand(CopyCurrentToSet);
		RenameSetCommand = new RelayCommand(RenameSet);
		DeleteSetCommand = new RelayCommand(DeleteSet);
		ImportSetCommand = new RelayCommand(ImportSet);
		ExportSetCommand = new RelayCommand(ExportSet);
		OpenSetsFolderCommand = new RelayCommand(OpenSetsFolder);
		RefreshSlots();
		ReloadSets(null);
	}

	public IReadOnlyList<CursorType> Styles { get; }

	public CursorType SelectedStyle
	{
		get => App.Settings.Prop.CursorType;
		set
		{
			if (App.Settings.Prop.CursorType != value)
				Run("Could not change the cursor style", () => CursorManager.ApplyStyle(value));
			NotifyStyle();
		}
	}

	public bool IsCustom => App.Settings.Prop.CursorType == CursorType.Custom;

	public IReadOnlyList<CursorSlotViewModel> CustomSlots { get; }

	public CursorSlotViewModel Pointer { get; }

	public CursorSlotViewModel Drag { get; }

	public CursorSlotViewModel ShiftLock { get; }

	public bool HasCustomCursor => CustomSlots.Any(static slot => CursorManager.OneImageSlots.Contains(slot.Slot) && slot.HasImage);

	public IReadOnlyList<CursorSlotViewModel> SetSlots { get; }

	public ObservableCollection<string> SavedSets { get; } = new ObservableCollection<string>();

	public string? SelectedSet
	{
		get => _selectedSet;
		set
		{
			if (_selectedSet == value)
				return;
			_selectedSet = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(HasSelectedSet));
			RefreshSetSlots();
		}
	}

	public bool HasSelectedSet => !string.IsNullOrEmpty(_selectedSet);

	public bool HasSavedSets => SavedSets.Count > 0;

	public bool HasNoSavedSets => SavedSets.Count == 0;

	public ICommand ChooseCustomCursorCommand { get; }

	public ICommand RemoveCustomCursorCommand { get; }

	public ICommand NewSetCommand { get; }

	public ICommand UseSetCommand { get; }

	public ICommand CopyCurrentToSetCommand { get; }

	public ICommand RenameSetCommand { get; }

	public ICommand DeleteSetCommand { get; }

	public ICommand ImportSetCommand { get; }

	public ICommand ExportSetCommand { get; }

	public ICommand OpenSetsFolderCommand { get; }

	private CursorSlotViewModel CreateCustomSlot(CursorSlot slot, string title, string hint)
	{
		return new CursorSlotViewModel(slot, title, hint, () => CursorManager.PreviewPath(slot), new RelayCommand(() => ChooseImage(slot)), new RelayCommand(() => ClearImage(slot)));
	}

	private CursorSlotViewModel CreateSetSlot(CursorSlot slot, string title, string hint)
	{
		return new CursorSlotViewModel(slot, title, hint, () => CursorManager.SetPreviewPath(_selectedSet, slot), new RelayCommand(() => ChooseSetImage(slot)), new RelayCommand(() => ClearSetImage(slot)));
	}

	private void ChooseImage(CursorSlot slot)
	{
		string? path = PickImage(slot == CursorSlot.ShiftLock ? "Choose a shift lock icon" : "Choose a cursor image");
		if (path == null)
			return;
		Run("Could not use that image", () =>
		{
			CursorManager.SetImage(slot, path);
			if (slot != CursorSlot.ShiftLock && !IsCustom)
				CursorManager.ApplyStyle(CursorType.Custom);
		});
		NotifyStyle();
		RefreshSlots();
	}

	private void ClearImage(CursorSlot slot)
	{
		Run("Could not remove that image", () => CursorManager.ClearImage(slot));
		RefreshSlots();
	}

	private void ChooseCustomCursor()
	{
		string? path = PickImage("Choose your custom cursor");
		if (path == null)
			return;
		Run("Could not use that image", () =>
		{
			foreach (CursorSlot slot in CursorManager.OneImageSlots)
				CursorManager.SetImage(slot, path);
			if (!IsCustom)
				CursorManager.ApplyStyle(CursorType.Custom);
		});
		NotifyStyle();
		RefreshSlots();
	}

	private void RemoveCustomCursor()
	{
		Run("Could not remove the custom cursor", () =>
		{
			foreach (CursorSlot slot in CursorManager.OneImageSlots)
				CursorManager.ClearImage(slot);
		});
		RefreshSlots();
	}

	private void ChooseSetImage(CursorSlot slot)
	{
		string? name = _selectedSet;
		if (name == null)
			return;
		string? path = PickImage(slot == CursorSlot.ShiftLock ? "Choose a shift lock icon for this set" : "Choose a cursor image for this set");
		if (path == null)
			return;
		Run("Could not add that image to the set", () => CursorManager.SetSetImage(name, slot, path));
		RefreshSetSlots();
	}

	private void ClearSetImage(CursorSlot slot)
	{
		string? name = _selectedSet;
		if (name == null)
			return;
		Run("Could not remove that image from the set", () => CursorManager.ClearSetImage(name, slot));
		RefreshSetSlots();
	}

	private void NewSet()
	{
		string? name = AskName("Name the new cursor set", CursorManager.SuggestSetName());
		if (name == null)
			return;
		string? created = null;
		Run("Could not create the cursor set", () => created = CursorManager.CreateSet(name));
		if (created != null)
			ReloadSets(created);
	}

	private void UseSet()
	{
		string? name = _selectedSet;
		if (name == null)
			return;
		Run("Could not use that cursor set", () => CursorManager.UseSet(name));
		NotifyStyle();
		RefreshSlots();
	}

	private void CopyCurrentToSet()
	{
		string? name = _selectedSet;
		if (name == null)
			return;
		if (Frontend.ShowMessageBox("Replace the images in \"" + name + "\" with your current custom cursor and shift lock icon?", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
			return;
		Run("Could not copy your cursor into the set", () => CursorManager.CopyCurrentToSet(name));
		RefreshSetSlots();
	}

	private void RenameSet()
	{
		string? oldName = _selectedSet;
		if (oldName == null)
			return;
		string? name = AskName("Rename this cursor set", oldName);
		if (name == null)
			return;
		string? renamed = null;
		Run("Could not rename the cursor set", () => renamed = CursorManager.RenameSet(oldName, name));
		ReloadSets(renamed ?? oldName);
	}

	private void DeleteSet()
	{
		string? name = _selectedSet;
		if (name == null)
			return;
		if (Frontend.ShowMessageBox("Delete the cursor set \"" + name + "\"? This cannot be undone.", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
			return;
		Run("Could not delete the cursor set", () => CursorManager.DeleteSet(name));
		ReloadSets(null);
	}

	private void ImportSet()
	{
		Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Import a cursor set",
			Filter = "Zip files (*.zip)|*.zip"
		};
		if (dialog.ShowDialog() != true)
			return;
		string? imported = null;
		Run("Could not import that zip", () => imported = CursorManager.ImportSet(dialog.FileName));
		if (imported != null)
			ReloadSets(imported);
	}

	private void ExportSet()
	{
		string? name = _selectedSet;
		if (name == null)
			return;
		Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
		{
			Title = "Export cursor set",
			FileName = name + ".zip",
			Filter = "Zip files (*.zip)|*.zip"
		};
		if (dialog.ShowDialog() != true)
			return;
		bool exported = false;
		Run("Could not export the cursor set", () =>
		{
			CursorManager.ExportSet(name, dialog.FileName);
			exported = true;
		});
		if (exported)
			PlatformShell.TryRevealFile(dialog.FileName);
	}

	private static void OpenSetsFolder()
	{
		try
		{
			Directory.CreateDirectory(CursorManager.SetsFolder);
			PlatformShell.TryOpenFolder(CursorManager.SetsFolder);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("CursorSettingsViewModel", "Could not open the cursor sets folder: " + ex.Message);
		}
	}

	private void NotifyStyle()
	{
		OnPropertyChanged(nameof(SelectedStyle));
		OnPropertyChanged(nameof(IsCustom));
	}

	private void RefreshSlots()
	{
		foreach (CursorSlotViewModel slot in CustomSlots)
			slot.Refresh();
		ShiftLock.Refresh();
		OnPropertyChanged(nameof(HasCustomCursor));
	}

	private void RefreshSetSlots()
	{
		foreach (CursorSlotViewModel slot in SetSlots)
			slot.Refresh();
	}

	private void ReloadSets(string? select)
	{
		string? keep = select ?? _selectedSet;
		SavedSets.Clear();
		foreach (string name in CursorManager.ListSets())
			SavedSets.Add(name);
		SelectedSet = keep != null && SavedSets.Contains(keep) ? keep : SavedSets.FirstOrDefault();
		OnPropertyChanged(nameof(HasSavedSets));
		OnPropertyChanged(nameof(HasNoSavedSets));
		RefreshSetSlots();
	}

	private static string? PickImage(string title)
	{
		Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = title,
			Filter = "Images|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.dib;*.gif;*.webp;*.tif;*.tiff;*.tga;*.ico;*.cur;*.qoi;*.pbm;*.pgm;*.ppm;*.heic;*.heif;*.avif;*.jxr;*.wdp|All files|*.*"
		};
		return dialog.ShowDialog() == true ? dialog.FileName : null;
	}

	private static string? AskName(string prompt, string initial)
	{
		TextInputDialog dialog = new TextInputDialog(prompt, initial);
		dialog.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(static window => window.IsActive);
		dialog.ShowOwnedDialog();
		return dialog.Confirmed ? dialog.Value : null;
	}

	private static void Run(string failure, Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("CursorSettingsViewModel", ex);
			Frontend.ShowMessageBox(failure + ": " + ex.Message, MessageBoxImage.Error);
		}
	}

	private static int GetSortOrder(CursorType type)
	{
		EnumSortAttribute? attribute = typeof(CursorType).GetField(type.ToString())?
			.GetCustomAttributes(typeof(EnumSortAttribute), false)
			.OfType<EnumSortAttribute>()
			.FirstOrDefault();
		return attribute?.Order ?? 0;
	}
}
