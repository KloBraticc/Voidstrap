using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.Utility;
using Wpf.Ui.Controls.Interfaces;

namespace Voidstrap.UI;

internal static class LiveLanguageRefresher
{
	private sealed record TextEntry(string Original, string Written);

	private sealed class ImageEntry
	{
		public object? Original { get; init; }

		public object? Written { get; set; }
	}

	private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, TextEntry>> _dependencyPropertyOriginals = [];

	private static readonly ConditionalWeakTable<DependencyObject, ImageEntry> _imageOriginals = [];

	private static bool IsActive => App.Settings?.Prop?.AutoTranslate == true || Branding.Active;

	private static bool _touched;

	private static bool _walkHidden;

	private static readonly List<WeakReference<DependencyObject>> _detachedRoots = [];

	private static DispatcherTimer? _coalesceTimer;

	private static DispatcherTimer? _sweepTimer;

	private static bool _initialized;

	private static bool _loadedHandlerRegistered;

	public static void Initialize()
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;

		UpdateSweepTimer();
	}

	private static void EnsureLoadedHandler()
	{
		if (_loadedHandlerRegistered)
		{
			return;
		}
		_loadedHandlerRegistered = true;
		EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, (RoutedEventHandler)OnElementLoaded);
		EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent, (SizeChangedEventHandler)OnTextSizeChanged);
		EventManager.RegisterClassHandler(typeof(AccessText), FrameworkElement.SizeChangedEvent, (SizeChangedEventHandler)OnTextSizeChanged);
	}

	public static void Shutdown()
	{
		StopTimer(ref _coalesceTimer, CoalescedWalkTick);
		StopTimer(ref _sweepTimer, SweepTick);
		lock (_detachedRoots)
		{
			_detachedRoots.Clear();
		}
	}

	private static void StopTimer(ref DispatcherTimer? timer, EventHandler handler)
	{
		DispatcherTimer? local = timer;
		timer = null;
		if (local == null)
		{
			return;
		}
		try
		{
			local.Stop();
			local.Tick -= handler;
		}
		catch
		{
		}
	}

	private static void OnElementLoaded(object sender, RoutedEventArgs e)
	{
		if (!IsActive && !_touched)
		{
			return;
		}
		if (App.Settings?.Prop?.AutoTranslate != true)
		{
			if (sender is DependencyObject node)
			{
				TranslateNode(node, IsActive, "");
				if (sender is Visual loadedVisual && PresentationSource.FromVisual(loadedVisual)?.RootVisual is DependencyObject loadedRoot && loadedRoot is not Window)
				{
					TrackDetachedRoot(loadedRoot);
				}
			}
			return;
		}
		if (sender is not Visual visual)
		{
			return;
		}
		try
		{
			if (PresentationSource.FromVisual(visual)?.RootVisual is DependencyObject root && root is not Window)
			{
				TrackDetachedRoot(root);
			}
		}
		catch
		{
		}
		ScheduleCoalescedWalk();
	}

	private static void OnTextSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (App.Settings?.Prop?.AutoTranslate == true || (!Branding.RenameActive && !_touched))
		{
			return;
		}
		if (sender is DependencyObject node)
		{
			TranslateNode(node, IsActive, "");
		}
	}

	private static void TrackDetachedRoot(DependencyObject root)
	{
		lock (_detachedRoots)
		{
			for (int i = _detachedRoots.Count - 1; i >= 0; i--)
			{
				if (!_detachedRoots[i].TryGetTarget(out DependencyObject? existing))
				{
					_detachedRoots.RemoveAt(i);
				}
				else if (ReferenceEquals(existing, root))
				{
					return;
				}
			}
			_detachedRoots.Add(new WeakReference<DependencyObject>(root));
		}
	}

	private static void SweepTick(object? sender, EventArgs e) // bratick
	{
		if (!IsActive)
		{
			return;
		}
		Application app = Application.Current;
		if (app == null)
		{
			return;
		}
		foreach (Window window in app.Windows)
		{
			if (window.IsActive)
			{
				ScheduleCoalescedWalk();
				return;
			}
		}
	}

	private static void UpdateSweepTimer()
	{
		if (!IsActive)
		{
			StopTimer(ref _sweepTimer, SweepTick);
			return;
		}
		EnsureLoadedHandler();
		if (_sweepTimer == null)
		{
			_sweepTimer = new DispatcherTimer(DispatcherPriority.Background);
			_sweepTimer.Tick += SweepTick;
		}
		_sweepTimer.Interval = TimeSpan.FromMilliseconds(Branding.RenameActive ? 2000.0 : 5000.0);
		if (!_sweepTimer.IsEnabled)
		{
			_sweepTimer.Start();
		}
	}

	public static void RefreshAllOpenWindows()
	{
		UpdateSweepTimer();
		Application app = Application.Current;
		if (app == null)
		{
			return;
		}
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			foreach (Window window in app.Windows)
			{
				try
				{
					ApplyFlowDirection(window);
					RefreshWindow(window);
					TranslateWindow(window);
				}
				catch
				{
				}
			}
		}, DispatcherPriority.Background);
	}

	public static void RestoreAllOpenWindows()
	{
		UpdateSweepTimer();
		Application app = Application.Current;
		if (app == null)
		{
			return;
		}
		app.Dispatcher.BeginInvoke((Action)delegate
		{
			foreach (Window window in app.Windows)
			{
				try
				{
					ApplyFlowDirection(window);
					RefreshWindow(window);
					TranslateWindow(window);
				}
				catch
				{
				}
			}
		}, DispatcherPriority.Background);
	}

	public static void TranslateOpenWindows()
	{
		Application app = Application.Current;
		if (app == null)
		{
			return;
		}
		if (app.CheckAccess())
		{
			ScheduleCoalescedWalk();
		}
		else
		{
			app.Dispatcher.BeginInvoke((Action)ScheduleCoalescedWalk, DispatcherPriority.Background);
		}
	}

	private static void ScheduleCoalescedWalk()
	{
		if (_coalesceTimer == null)
		{
			_coalesceTimer = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromMilliseconds(120.0)
			};
			_coalesceTimer.Tick += CoalescedWalkTick;
		}
		if (!_coalesceTimer.IsEnabled)
		{
			_coalesceTimer.Start();
		}
	}

	private static void CoalescedWalkTick(object? sender, EventArgs e)
	{
		_coalesceTimer?.Stop();
		Application app = Application.Current;
		if (app == null)
		{
			return;
		}
		bool includeHidden = _walkHidden;
		_walkHidden = false;
		foreach (Window window in app.Windows)
		{
			try
			{
				if (!window.IsVisible && !includeHidden)
				{
					continue;
				}
				ApplyFlowDirection(window);
				TranslateWindow(window);
			}
			catch
			{
			}
		}
		WalkDetachedRoots();
	}

	private static void WalkDetachedRoots()
	{
		bool on = IsActive;
		string lang = App.Settings?.Prop?.AutoTranslateLanguage ?? "";
		if (on && string.IsNullOrEmpty(lang))
		{
			lang = "en";
		}

		DependencyObject[] roots;
		lock (_detachedRoots)
		{
			var alive = new List<DependencyObject>(_detachedRoots.Count);
			for (int i = _detachedRoots.Count - 1; i >= 0; i--)
			{
				if (_detachedRoots[i].TryGetTarget(out DependencyObject? root))
				{
					alive.Add(root);
				}
				else
				{
					_detachedRoots.RemoveAt(i);
				}
			}
			roots = [.. alive];
		}

		foreach (DependencyObject root in roots)
		{
			try
			{
				TranslateNode(root, on, lang);
				Walk(root, on, lang);
			}
			catch
			{
			}
		}
	}

	private static void TranslateWindow(Window window)
	{
		bool on = IsActive;
		string lang = App.Settings?.Prop?.AutoTranslateLanguage ?? "";
		if (on && string.IsNullOrEmpty(lang))
		{
			lang = "en";
		}
		TranslateNode(window, on, lang);
		Walk(window, on, lang);
	}

	private static void Walk(DependencyObject node, bool on, string lang)
	{
		if (IsIconNode(node))
		{
			return;
		}
		int count = VisualTreeHelper.GetChildrenCount(node);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(node, i);
			TranslateNode(child, on, lang);
			Walk(child, on, lang);
		}
	}

	private static void TranslateNode(DependencyObject node, bool on, string lang)
	{
		try
		{
			if (IsIconNode(node))
			{
				return;
			}
			if (node is Window window)
			{
				ApplyDependencyText(window, Window.TitleProperty, on, lang);
				ApplyBrandImage(window, Window.IconProperty);
			}
			if (node is Image image)
			{
				ApplyBrandImage(image, Image.SourceProperty);
				return;
			}
			if (node is FrameworkElement fe)
			{
				if (fe.ToolTip is string)
				{
					ApplyDependencyText(fe, FrameworkElement.ToolTipProperty, on, lang);
				}
				else if (fe.ToolTip is ToolTip tip && tip.Content is string)
				{
					ApplyDependencyText(tip, ContentControl.ContentProperty, on, lang);
				}
				if (fe is Wpf.Ui.Controls.TextBox uiBox && !string.IsNullOrEmpty(uiBox.PlaceholderText))
				{
					ApplyDependencyText(uiBox, Wpf.Ui.Controls.TextBox.PlaceholderTextProperty, on, lang);
				}
			}
			if (node is TextBlock textBlock)
			{
				if (textBlock.Inlines.Count == 0)
				{
					ApplyDependencyText(textBlock, TextBlock.TextProperty, on, lang);
				}
				else
				{
					TranslateInlines(textBlock.Inlines, on, lang);
				}
				return;
			}
			if (node is AccessText accessText)
			{
				ApplyDependencyText(accessText, AccessText.TextProperty, on, lang);
			}
			if (node is System.Windows.Controls.RichTextBox richTextBox)
			{
				TranslateBlocks(richTextBox.Document.Blocks, on, lang);
			}
			if (node is FlowDocumentScrollViewer documentViewer && documentViewer.Document != null)
			{
				TranslateBlocks(documentViewer.Document.Blocks, on, lang);
			}
			if (node is OptionControl option)
			{
				ApplyDependencyText(option, OptionControl.HeaderProperty, on, lang);
				ApplyDependencyText(option, OptionControl.DescriptionProperty, on, lang);
			}
			if (node is Elements.Controls.Expander expander)
			{
				ApplyDependencyText(expander, Elements.Controls.Expander.HeaderTextProperty, on, lang);
			}
			if (node is Wpf.Ui.Controls.TitleBar titleBar)
			{
				ApplyDependencyText(titleBar, Wpf.Ui.Controls.TitleBar.TitleProperty, on, lang);
			}
			if (node is Wpf.Ui.Controls.InfoBar infoBar)
			{
				ApplyDependencyText(infoBar, Wpf.Ui.Controls.InfoBar.TitleProperty, on, lang);
				ApplyDependencyText(infoBar, Wpf.Ui.Controls.InfoBar.MessageProperty, on, lang);
			}
			if (node is Wpf.Ui.Controls.CardControl card && card.Header is string)
			{
				ApplyDependencyText(card, Wpf.Ui.Controls.CardControl.HeaderProperty, on, lang);
			}
			if (node is Wpf.Ui.Controls.Dialog dialog)
			{
				ApplyDependencyText(dialog, Wpf.Ui.Controls.Dialog.TitleProperty, on, lang);
				ApplyDependencyText(dialog, Wpf.Ui.Controls.Dialog.MessageProperty, on, lang);
				ApplyDependencyText(dialog, Wpf.Ui.Controls.Dialog.ButtonLeftNameProperty, on, lang);
				ApplyDependencyText(dialog, Wpf.Ui.Controls.Dialog.ButtonRightNameProperty, on, lang);
				ApplyDependencyText(dialog, Wpf.Ui.Controls.Dialog.FooterProperty, on, lang);
			}
			if (node is Wpf.Ui.Controls.Snackbar snackbar)
			{
				ApplyDependencyText(snackbar, Wpf.Ui.Controls.Snackbar.TitleProperty, on, lang);
				ApplyDependencyText(snackbar, Wpf.Ui.Controls.Snackbar.MessageProperty, on, lang);
			}
			if (node is Wpf.Ui.Controls.MessageBox messageBox)
			{
				ApplyDependencyText(messageBox, Wpf.Ui.Controls.MessageBox.ButtonLeftNameProperty, on, lang);
				ApplyDependencyText(messageBox, Wpf.Ui.Controls.MessageBox.ButtonRightNameProperty, on, lang);
				ApplyDependencyText(messageBox, Wpf.Ui.Controls.MessageBox.FooterProperty, on, lang);
			}
			if (node is Wpf.Ui.Controls.Navigation.NavigationHeader navigationHeader)
			{
				ApplyDependencyText(navigationHeader, Wpf.Ui.Controls.Navigation.NavigationHeader.TextProperty, on, lang);
			}
			if (node is MenuItem menuItem)
			{
				ApplyDependencyText(menuItem, MenuItem.InputGestureTextProperty, on, lang);
			}
			if (node is DataGrid dataGrid)
			{
				foreach (DataGridColumn column in dataGrid.Columns)
				{
					ApplyDependencyText(column, DataGridColumn.HeaderProperty, on, lang);
				}
			}
			if (node is HeaderedItemsControl headeredItems)
			{
				if (headeredItems.Header is string)
				{
					ApplyDependencyText(headeredItems, HeaderedItemsControl.HeaderProperty, on, lang);
				}
			}
			if (node is HeaderedContentControl headered && headered.Header is string)
			{
				ApplyDependencyText(headered, HeaderedContentControl.HeaderProperty, on, lang);
			}
			if (node is ContentControl content && content.Content is string && node is not (ComboBoxItem or ListBoxItem))
			{
				ApplyDependencyText(content, ContentControl.ContentProperty, on, lang);
			}
		}
		catch
		{
		}
	}

	private static bool IsIconNode(DependencyObject node)
	{
		if (node is Wpf.Ui.Controls.SymbolIcon or Wpf.Ui.Controls.FontIcon)
		{
			return true;
		}
		if (node.GetValue(TextElement.FontFamilyProperty) is not System.Windows.Media.FontFamily fontFamily)
		{
			return false;
		}
		string source = fontFamily.Source;
		return source.Contains("Icon", StringComparison.OrdinalIgnoreCase) ||
			source.Contains("Symbol", StringComparison.OrdinalIgnoreCase) ||
			source.Contains("Wingdings", StringComparison.OrdinalIgnoreCase) ||
			source.Contains("Webdings", StringComparison.OrdinalIgnoreCase);
	}

	private static void TranslateInlines(InlineCollection inlines, bool on, string lang)
	{
		foreach (Inline inline in inlines)
		{
			if (inline is Run run)
			{
				ApplyDependencyText(run, Run.TextProperty, on, lang);
			}
			else if (inline is Span span)
			{
				TranslateInlines(span.Inlines, on, lang);
			}
		}
	}

	private static void TranslateBlocks(BlockCollection blocks, bool on, string lang)
	{
		foreach (Block block in blocks)
		{
			if (block is Paragraph paragraph)
			{
				TranslateInlines(paragraph.Inlines, on, lang);
			}
			else if (block is Section section)
			{
				TranslateBlocks(section.Blocks, on, lang);
			}
			else if (block is List list)
			{
				foreach (ListItem item in list.ListItems)
				{
					TranslateBlocks(item.Blocks, on, lang);
				}
			}
			else if (block is Table table)
			{
				foreach (TableRowGroup group in table.RowGroups)
				{
					foreach (TableRow row in group.Rows)
					{
						foreach (TableCell cell in row.Cells)
						{
							TranslateBlocks(cell.Blocks, on, lang);
						}
					}
				}
			}
		}
	}

	private static string Transform(string source, bool translate, string lang)
	{
		return Branding.Apply(translate ? TranslationService.Translate(source, lang) : source);
	}

	private static void ApplyDependencyText(DependencyObject target, DependencyProperty property, bool on, string lang)
	{
		if (target.GetValue(property) is not string current)
		{
			return;
		}
		_dependencyPropertyOriginals.TryGetValue(target, out Dictionary<DependencyProperty, TextEntry>? entries);
		TextEntry? entry = null;
		entries?.TryGetValue(property, out entry);
		if (!on)
		{
			if (entry != null)
			{
				entries!.Remove(property);
				if (current == entry.Written && current != entry.Original)
				{
					target.SetCurrentValue(property, entry.Original);
				}
			}
			return;
		}
		if (string.IsNullOrWhiteSpace(current))
		{
			return;
		}
		bool translate = App.Settings?.Prop?.AutoTranslate == true;
		string source;
		if (entry != null && (current == entry.Written || current == entry.Original))
		{
			source = entry.Original;
		}
		else if (translate && TranslationService.TryGetOriginal(current, lang, out string original))
		{
			source = original;
		}
		else
		{
			source = current;
		}
		string expected = Transform(source, translate, lang);
		if (entry == null && expected == source && source == current)
		{
			return;
		}
		if (current != expected)
		{
			target.SetCurrentValue(property, expected);
		}
		entries ??= _dependencyPropertyOriginals.GetOrCreateValue(target);
		entries[property] = new TextEntry(source, expected);
		_touched = true;
	}

	internal static void ApplyBrandImage(DependencyObject target, DependencyProperty property)
	{
		object? current = target.GetValue(property);
		ImageSource? branded = Branding.Icon;
		if (_imageOriginals.TryGetValue(target, out ImageEntry? entry))
		{
			if (ReferenceEquals(current, entry.Written))
			{
				if (branded == null)
				{
					_imageOriginals.Remove(target);
					target.SetCurrentValue(property, entry.Original);
				}
				else if (!ReferenceEquals(branded, entry.Written))
				{
					entry.Written = branded;
					target.SetCurrentValue(property, branded);
				}
				return;
			}
			_imageOriginals.Remove(target);
		}
		if (branded == null || !Branding.IsDefaultLogo(current))
		{
			return;
		}
		_imageOriginals.Add(target, new ImageEntry { Original = current, Written = branded });
		target.SetCurrentValue(property, branded);
		_touched = true;
	}

	public static void ApplyBranding()
	{
		Application app = Application.Current;
		if (app == null)
		{
			return;
		}
		app.Dispatcher.BeginInvoke((Action)ApplyBrandingNow, DispatcherPriority.Background);
	}

	private static void ApplyBrandingNow()
	{
		Application app = Application.Current;
		if (app == null)
		{
			return;
		}
		UpdateSweepTimer();
		EnsureLoadedHandler();
		foreach (Window window in app.Windows)
		{
			try
			{
				TranslateWindow(window);
			}
			catch
			{
			}
		}
		WalkDetachedRoots();
	}

	private static DispatcherTimer? _brandNameTimer;

	private static string _pendingBrandName = "";

	public static void SetBrandName(string name)
	{
		_pendingBrandName = name;
		if (_brandNameTimer == null)
		{
			_brandNameTimer = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromMilliseconds(450.0)
			};
			_brandNameTimer.Tick += BrandNameTick;
		}
		_brandNameTimer.Stop();
		_brandNameTimer.Start();
	}

	private static void BrandNameTick(object? sender, EventArgs e)
	{
		_brandNameTimer?.Stop();
		if (App.Settings?.Prop == null || string.Equals(App.Settings.Prop.BrandName ?? "", _pendingBrandName, StringComparison.Ordinal))
		{
			return;
		}
		App.Settings.Prop.BrandName = _pendingBrandName;
		App.Settings.SaveDeferred();
		ApplyBrandingNow();
	}

	private static void ApplyFlowDirection(Window window)
	{
		bool rightToLeft = App.Settings?.Prop?.AutoTranslate == true
			? Locale.IsRightToLeftLanguage(App.Settings.Prop.AutoTranslateLanguage ?? "")
			: Locale.RightToLeft;
		FlowDirection flowDirection = (window.FlowDirection = (rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight));
		if (window.ContextMenu is { } contextMenu)
		{
			contextMenu.FlowDirection = flowDirection;
		}
	}

	private static void RefreshWindow(Window window)
	{
		INavigation? nav = FindNavigation(window);
		if (nav == null)
		{
			return;
		}
		int currentIndex;
		try
		{
			currentIndex = nav.SelectedPageIndex;
		}
		catch
		{
			return;
		}
		if (currentIndex < 0)
		{
			return;
		}
		try
		{
			nav.ClearCache();
		}
		catch
		{
		}
		window.Dispatcher.BeginInvoke((Action)delegate
		{
			try
			{
				nav.Navigate(currentIndex);
			}
			catch
			{
			}
		}, DispatcherPriority.Background);
	}

	private static INavigation? FindNavigation(DependencyObject root)
	{
		if (root == null)
		{
			return null;
		}
		int childrenCount = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < childrenCount; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is INavigation result)
			{
				return result;
			}
			INavigation? navigation = FindNavigation(child);
			if (navigation != null)
			{
				return navigation;
			}
		}
		return null;
	}
}
