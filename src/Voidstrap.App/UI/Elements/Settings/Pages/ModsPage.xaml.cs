using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ModsPage : UiPage{
	private const long MaxPreviewFileBytes = 32L * 1024 * 1024;

	private ModsViewModel ViewModel;

	private readonly System.Windows.Threading.DispatcherTimer _fgWarningTimer;

	private static readonly string[] PreviewableCategories = new string[9] { "Image", "Mesh", "Audio", "Model", "Data", "Pending", "Animation", "Texture", "Other" };

	private Point _managedModDragStart;

	private ManagedModItem? _managedModDragItem;

	private Border? _managedModDragCard;

	private Border? _managedModDropCard;

	private CancellationTokenSource? _previewCancellation;

	public ModsPage()
	{
		ViewModel = new ModsViewModel();
		base.DataContext = ViewModel;
		InitializeComponent();
		_fgWarningTimer = new System.Windows.Threading.DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(2.0),
		};
		base.Loaded += ModsPage_Loaded;
		base.Unloaded += ModsPage_Unloaded;
	}

	private void HomepageMediaPreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
	{
		if (sender is not MediaElement media)
			return;
		try
		{
			media.Position = TimeSpan.Zero;
			media.Play();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModsPage::HomepageMediaPreview", "The preview could not loop: " + ex.Message);
		}
	}

	private async void ModsPage_Loaded(object sender, RoutedEventArgs e)
	{
		try
		{
			ViewModel.RefreshFrameGenWarning();
			_fgWarningTimer.Tick -= FgWarningTimer_Tick;
			_fgWarningTimer.Tick += FgWarningTimer_Tick;
			_fgWarningTimer.Start();
			await ViewModel.InitializeAsync();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ModsPage::Loaded", ex);
		}
	}

	private void ModsPage_Unloaded(object sender, RoutedEventArgs e)
	{
		CancellationTokenSource? previewCancellation = _previewCancellation;
		_previewCancellation = null;
		previewCancellation?.Cancel();
		previewCancellation?.Dispose();
		_fgWarningTimer.Stop();
		_fgWarningTimer.Tick -= FgWarningTimer_Tick;
		ViewModel.ReleaseHomepageMediaPreview();
		ViewModel.CancelTransientOperations();
		ResetManagedModDrag();
	}

	private void FgWarningTimer_Tick(object? sender, EventArgs e)
	{
		ViewModel.RefreshFrameGenWarning();
	}

	private async void ModGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		CancellationTokenSource? previous = _previewCancellation;
		_previewCancellation = null;
		previous?.Cancel();
		previous?.Dispose();
		object selectedItem = ModGrid.SelectedItem;
		if (!(selectedItem is ModFile { IsFolder: false } file))
		{
			ModPreviewPanel.ClearPreview();
			return;
		}
		try
		{
			FileInfo info = new FileInfo(file.FullPath);
			if (!info.Exists || info.Length <= 0 || info.Length > MaxPreviewFileBytes)
			{
				ModPreviewPanel.ShowInfo("That file is too large to preview.");
				return;
			}
			CancellationTokenSource current = new CancellationTokenSource();
			_previewCancellation = current;
			byte[] data = await ReadPreviewBytesAsync(file.FullPath, current.Token);
			if (current.IsCancellationRequested || !ReferenceEquals(_previewCancellation, current) || ModGrid.SelectedItem is not ModFile selected || selected.FullPath != file.FullPath)
				return;
			ModPreviewPanel.Preview(file.Name, data);
		}
		catch (OperationCanceledException)
		{
		}
		catch
		{
			ModPreviewPanel.ShowInfo("Couldn't load preview.");
		}
	}

	private static async Task<byte[]> ReadPreviewBytesAsync(string path, CancellationToken token)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaxPreviewFileBytes)
			throw new InvalidDataException("The preview file size is invalid");
		byte[] data = new byte[checked((int)stream.Length)];
		int offset = 0;
		while (offset < data.Length)
		{
			int read = await stream.ReadAsync(data.AsMemory(offset), token);
			if (read == 0)
				throw new EndOfStreamException();
			offset += read;
		}
		if (await stream.ReadAsync(new byte[1], token) != 0)
			throw new InvalidDataException("The preview file changed while it was being read");
		return data;
	}

	private void ModGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (ModGrid.SelectedItem is ModFile { IsFolder: not false } modFile)
		{
			ViewModel.CurrentExplorerPath = modFile.FullPath;
			ViewModel.RefreshModFiles();
		}
	}

	private void ManagedModDragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is not FrameworkElement { DataContext: ManagedModItem item })
			return;
		_managedModDragStart = e.GetPosition(this);
		_managedModDragItem = item;
		_managedModDragCard = FindAncestor<Border>((DependencyObject)sender);
	}

	private void ManagedModDragHandle_MouseMove(object sender, MouseEventArgs e)
	{
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			ResetManagedModDrag();
			return;
		}
		if (_managedModDragItem is null || sender is not FrameworkElement handle)
			return;
		Point current = e.GetPosition(this);
		if (Math.Abs(current.X - _managedModDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _managedModDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
			return;
		try
		{
			if (_managedModDragCard is not null)
				_managedModDragCard.Opacity = 0.55;
			DragDrop.DoDragDrop(handle, _managedModDragItem, DragDropEffects.Move);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ModsPage::ManagedModDrag", "The mod could not be dragged: " + ex.Message);
		}
		finally
		{
			ResetManagedModDrag();
		}
	}

	private void ManagedModsList_DragOver(object sender, DragEventArgs e)
	{
		if (!TryGetDraggedManagedMod(e, out _))
			return;
		e.Effects = DragDropEffects.Move;
		AutoScrollManagedMods(e);
		e.Handled = true;
	}

	private void AutoScrollManagedMods(DragEventArgs e)
	{
		ScrollViewer? scrollViewer = FindAncestor<ScrollViewer>(ManagedModsList);
		if (scrollViewer is not null)
		{
			Point position = e.GetPosition(scrollViewer);
			if (position.Y < 48)
				scrollViewer.LineUp();
			else if (position.Y > scrollViewer.ViewportHeight - 48)
				scrollViewer.LineDown();
		}
	}

	private void ManagedModCard_DragEnter(object sender, DragEventArgs e)
	{
		UpdateManagedModDropVisual(sender, e);
	}

	private void ManagedModCard_DragOver(object sender, DragEventArgs e)
	{
		UpdateManagedModDropVisual(sender, e);
	}

	private void ManagedModCard_DragLeave(object sender, DragEventArgs e)
	{
		if (ReferenceEquals(sender, _managedModDropCard))
			ClearManagedModDropVisual();
	}

	private async void ManagedModCard_Drop(object sender, DragEventArgs e)
	{
		if (sender is not Border { DataContext: ManagedModItem target } card || !TryGetDraggedManagedMod(e, out ManagedModItem? source) || source.Id == target.Id)
		{
			ResetManagedModDrag();
			return;
		}
		bool insertAfter = e.GetPosition(card).Y >= card.ActualHeight / 2;
		ResetManagedModDrag();
		e.Handled = true;
		await ViewModel.ReorderManagedModAsync(source, target, insertAfter);
	}

	private void UpdateManagedModDropVisual(object sender, DragEventArgs e)
	{
		AutoScrollManagedMods(e);
		if (sender is not Border { DataContext: ManagedModItem target } card || !TryGetDraggedManagedMod(e, out ManagedModItem? source) || source.Id == target.Id)
		{
			e.Effects = DragDropEffects.None;
			return;
		}
		if (!ReferenceEquals(_managedModDropCard, card))
		{
			ClearManagedModDropVisual();
			_managedModDropCard = card;
			card.SetResourceReference(Border.BorderBrushProperty, "SystemAccentColorPrimaryBrush");
		}
		bool insertAfter = e.GetPosition(card).Y >= card.ActualHeight / 2;
		card.BorderThickness = insertAfter ? new Thickness(1, 1, 1, 3) : new Thickness(1, 3, 1, 1);
		e.Effects = DragDropEffects.Move;
		e.Handled = true;
	}

	private bool TryGetDraggedManagedMod(DragEventArgs e, out ManagedModItem? item)
	{
		item = e.Data.GetDataPresent(typeof(ManagedModItem)) ? e.Data.GetData(typeof(ManagedModItem)) as ManagedModItem : null;
		return item is not null && _managedModDragItem is not null && item.Id == _managedModDragItem.Id;
	}

	private void ResetManagedModDrag()
	{
		ClearManagedModDropVisual();
		if (_managedModDragCard is not null)
			_managedModDragCard.Opacity = 1;
		_managedModDragCard = null;
		_managedModDragItem = null;
	}

	private void ClearManagedModDropVisual()
	{
		if (_managedModDropCard is null)
			return;
		_managedModDropCard.ClearValue(Border.BorderBrushProperty);
		_managedModDropCard.ClearValue(Border.BorderThicknessProperty);
		_managedModDropCard = null;
	}

	private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
	{
		DependencyObject? current = element;
		while (current is not null)
		{
			current = VisualTreeHelper.GetParent(current);
			if (current is T match)
				return match;
		}
		return null;
	}
}
