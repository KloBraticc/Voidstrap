using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Voidstrap.UI;

public static class LinuxTextGuard
{
	public static readonly DependencyProperty PreserveCompactLayoutProperty = DependencyProperty.RegisterAttached(
		"PreserveCompactLayout",
		typeof(bool),
		typeof(LinuxTextGuard),
		new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

	private const double Tolerance = 1.0;

	private const double AlignmentHysteresis = 12.0;

	private const int MaxAlignmentFlips = 3;

	private const int MaxFlowCorrections = 6;

	private const int MaxSelfDrivenShrinks = 2;

	private const long SettleMilliseconds = 1000;
	private const double WrapSafety = 4.0;
	private const double MinimumLimit = 56.0;

	private static readonly DependencyPropertyDescriptor? TextDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
	private static readonly DependencyPropertyDescriptor? FontFamilyDescriptor = DependencyPropertyDescriptor.FromProperty(TextElement.FontFamilyProperty, typeof(TextBlock));
	private static readonly DependencyPropertyDescriptor? FontSizeDescriptor = DependencyPropertyDescriptor.FromProperty(TextElement.FontSizeProperty, typeof(TextBlock));
	private static readonly DependencyPropertyDescriptor? FontStyleDescriptor = DependencyPropertyDescriptor.FromProperty(TextElement.FontStyleProperty, typeof(TextBlock));
	private static readonly DependencyPropertyDescriptor? FontWeightDescriptor = DependencyPropertyDescriptor.FromProperty(TextElement.FontWeightProperty, typeof(TextBlock));
	private static readonly DependencyPropertyDescriptor? FontStretchDescriptor = DependencyPropertyDescriptor.FromProperty(TextElement.FontStretchProperty, typeof(TextBlock));
	private static readonly DependencyPropertyDescriptor? FlowDirectionDescriptor = DependencyPropertyDescriptor.FromProperty(FrameworkElement.FlowDirectionProperty, typeof(TextBlock));
	private static readonly DependencyPropertyDescriptor?[] ObservedDescriptors =
	[
		TextDescriptor,
		FontFamilyDescriptor,
		FontSizeDescriptor,
		FontStyleDescriptor,
		FontWeightDescriptor,
		FontStretchDescriptor,
		FlowDirectionDescriptor
	];

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, FlowState> FlowStates = new();
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Wpf.Ui.Controls.UiPage, TraversalState> PageStates = new();
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Wpf.Ui.Controls.UiPage, OwnerState> PageOwners = new();
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, TraversalState> WindowStates = new();
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, AlignmentState> AlignmentStates = new();

	private static bool _installed;
	private static int _corrected;
	private static int _reported;
	private static int _pageReported;

	private sealed class FlowState
	{
		public bool Initialized;
		public bool Sanitized;
		public bool Subscribed;
		public bool OwnsWrapping;
		public bool OwnsMaxWidth;
		public bool OwnsLineHeight;
		public bool OwnsMinHeight;
		public bool OwnsFontSize;
		public TextWrapping OriginalWrapping;
		public double OriginalMaxWidth;
		public double OriginalLineHeight;
		public double OriginalMinHeight;
		public double OriginalFontSize;
		public int Corrections;
		public long CorrectedAtTicks;
		public int Shrinks;
		public double OwnerWidth = double.NaN;
		public double AppliedMaxWidth = double.NaN;
		public double AppliedLineHeight = double.NaN;
		public double AppliedMinHeight = double.NaN;
		public double AppliedFontSize = double.NaN;
		public string Source = string.Empty;
		public string Rendered = string.Empty;
		public WeakReference<Window>? Owner;
		public int Pending;
	}

	private sealed class TraversalState
	{
		public int Pending;
		public int AlignmentPending;
		public int OwnerPending;
		public int OwnerGeneration;
	}

	private sealed class AlignmentState
	{
		public bool OwnsAlignment;
		public VerticalAlignment OriginalAlignment;
		public int Flips;
		public bool Latched;
		public long FlippedAtTicks;
	}

	private sealed class OwnerState
	{
		public WeakReference<Window>? Owner;
	}

	public static int CorrectedCount => _corrected;

	public static bool GetPreserveCompactLayout(DependencyObject element)
	{
		return (bool)element.GetValue(PreserveCompactLayoutProperty);
	}

	public static void SetPreserveCompactLayout(DependencyObject element, bool value)
	{
		element.SetValue(PreserveCompactLayoutProperty, value);
	}

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		_installed = true;
		EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTextLoaded));
		EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnTextUnloaded));
		EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnTextResized));
		EventManager.RegisterClassHandler(typeof(Wpf.Ui.Controls.UiPage), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnPageLoaded));
		EventManager.RegisterClassHandler(typeof(Wpf.Ui.Controls.UiPage), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnPageResized));
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnWindowResized));
	}

	private static void OnWindowLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is Window window)
		{
			QueueWindowCorrection(window);
		}
	}

	private static void OnWindowResized(object sender, SizeChangedEventArgs e)
	{
		if (e.WidthChanged && sender is Window window)
		{
			QueueWindowCorrection(window);
		}
	}

	private static void OnPageLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not Wpf.Ui.Controls.UiPage page)
		{
			return;
		}

		try
		{
			page.ApplyTemplate();
			if (page.Template?.FindName("PART_ScrollViewer", page) is ScrollViewer viewer
				&& viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
			{
				viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
				if (Interlocked.Exchange(ref _pageReported, 1) == 0)
				{
					App.Logger?.WriteLine("LinuxTextGuard", "Constrained page content to the viewport width so body text can flow vertically");
				}
			}

			QueuePageCorrection(page);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxTextGuard::OnPageLoaded", "The page could not be constrained: " + ex.Message);
		}
	}

	private static void OnPageResized(object sender, SizeChangedEventArgs e)
	{
		if (e.WidthChanged && sender is Wpf.Ui.Controls.UiPage page)
		{
			QueuePageCorrection(page);
		}
	}

	private static void OnTextLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not TextBlock block)
		{
			return;
		}

		RemoveUndrawableCharacters(block);

		FlowState state = FlowStates.GetValue(block, static _ => new FlowState());
		Initialize(block, state);
		Window? owner = FindTextOwner(block);
		if (owner != null)
		{
			state.Owner = new WeakReference<Window>(owner);
		}
		Subscribe(block, state);
		QueueCorrection(block);
	}

	private static Window? FindTextOwner(TextBlock block)
	{
		DependencyObject? current = block;
		while (current != null)
		{
			if (current is Wpf.Ui.Controls.UiPage page)
			{
				return FindPageOwner(page);
			}

			current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
		}

		return Window.GetWindow(block);
	}

	private static void OnTextUnloaded(object sender, RoutedEventArgs e)
	{
		if (sender is TextBlock block && FlowStates.TryGetValue(block, out FlowState? state) && state is not null)
		{
			Unsubscribe(block, state);
			Volatile.Write(ref state.Pending, 0);
		}
	}

	private static void OnTextResized(object sender, SizeChangedEventArgs e)
	{
		if (e.WidthChanged && sender is TextBlock block)
		{
			QueueCorrection(block);
		}
	}

	private static void OnObservedPropertyChanged(object? sender, EventArgs e)
	{
		if (sender is TextBlock block)
		{
			RemoveUndrawableCharacters(block);
			FlowState state = FlowStates.GetValue(block, static _ => new FlowState());
			Initialize(block, state);
			if (state.OwnsFontSize && Math.Abs(block.FontSize - state.AppliedFontSize) > Tolerance)
			{
				state.OriginalFontSize = block.FontSize;
				state.OwnsFontSize = false;
				state.AppliedFontSize = double.NaN;
			}
			else if (!state.OwnsFontSize)
			{
				state.OriginalFontSize = block.FontSize;
			}

			QueueCorrection(block);
		}
	}

	private static void RemoveUndrawableCharacters(TextBlock block)
	{
		InlineCollection inlines = block.Inlines;
		if (inlines.Count <= 1 && inlines.FirstInline is null or Run)
		{
			string text = block.Text;
			string drawable = ToDrawableText(text, block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
			if (!ReferenceEquals(text, drawable))
				block.SetCurrentValue(TextBlock.TextProperty, drawable);
			return;
		}

		foreach (Inline inline in inlines)
			RemoveUndrawableCharacters(inline);
	}

	private static void RemoveUndrawableCharacters(Inline inline)
	{
		switch (inline)
		{
			case Run run:
				string text = run.Text;
				string drawable = ToDrawableText(text, run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch);
				if (!ReferenceEquals(text, drawable))
					run.SetCurrentValue(Run.TextProperty, drawable);
				break;
			case Span span:
				foreach (Inline child in span.Inlines)
					RemoveUndrawableCharacters(child);
				break;
		}
	}

	private static string ToDrawableText(string text, System.Windows.Media.FontFamily family, FontStyle style, FontWeight weight, FontStretch stretch)
	{
		if (string.IsNullOrEmpty(text) || text.AsSpan().IndexOfAnyExceptInRange('\u0000', '\u024F') < 0)
			return text;

		if (!new Typeface(family, style, weight, stretch).TryGetGlyphTypeface(out GlyphTypeface glyphs))
			return text;

		IDictionary<int, ushort> map = glyphs.CharacterToGlyphMap;
		System.Text.StringBuilder? builder = null;
		int index = 0;
		while (index < text.Length)
		{
			int length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
			int codePoint = length == 2 ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];
			bool drawable = !IsSymbolOrEmoji(codePoint) || (!char.IsSurrogate((char)codePoint) || codePoint > 0xFFFF) && map.ContainsKey(codePoint);
			if (drawable)
				builder?.Append(text, index, length);
			else
				builder ??= new System.Text.StringBuilder(text.Length).Append(text, 0, index);
			index += length;
		}

		return builder?.ToString() ?? text;
	}

	internal static string ToDrawableText(TextBlock block, string text)
	{
		return ToDrawableText(text, block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
	}

	private static bool IsSymbolOrEmoji(int codePoint)
	{
		return codePoint is >= 0x2190 and <= 0x2BFF
			or 0x200D
			or >= 0xFE00 and <= 0xFE0F
			or >= 0x1F000 and <= 0x1FAFF
			or >= 0xE0020 and <= 0xE007F
			|| char.IsSurrogate((char)codePoint) && codePoint <= 0xFFFF;
	}

	private static void Initialize(TextBlock block, FlowState state)
	{
		if (state.Initialized)
		{
			return;
		}

		state.Initialized = true;
		state.OriginalWrapping = block.TextWrapping;
		state.OriginalMaxWidth = block.MaxWidth;
		state.OriginalLineHeight = block.LineHeight;
		state.OriginalMinHeight = block.MinHeight;
		state.OriginalFontSize = block.FontSize;
	}

	private static void Subscribe(TextBlock block, FlowState state)
	{
		if (state.Subscribed)
		{
			return;
		}

		foreach (DependencyPropertyDescriptor? descriptor in ObservedDescriptors)
		{
			descriptor?.AddValueChanged(block, OnObservedPropertyChanged);
		}

		state.Subscribed = true;
	}

	private static void Unsubscribe(TextBlock block, FlowState state)
	{
		if (!state.Subscribed)
		{
			return;
		}

		foreach (DependencyPropertyDescriptor? descriptor in ObservedDescriptors)
		{
			descriptor?.RemoveValueChanged(block, OnObservedPropertyChanged);
		}

		state.Subscribed = false;
	}

	public static void Refresh(TextBlock? block)
	{
		if (block == null || !Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		FlowState state = FlowStates.GetValue(block, static _ => new FlowState());
		Initialize(block, state);
		state.Sanitized = false;
		state.Source = string.Empty;
		state.Rendered = string.Empty;
		state.Corrections = 0;
		state.Shrinks = 0;
		state.OwnerWidth = double.NaN;
		QueueCorrection(block);
	}

	internal static void AttachOwner(DependencyObject root, Window owner)
	{
		if (root == null || owner == null || !Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		if (root is Wpf.Ui.Controls.UiPage page)
		{
			PageOwners.GetValue(page, static _ => new OwnerState()).Owner = new WeakReference<Window>(owner);
			TraversalState state = PageStates.GetValue(page, static _ => new TraversalState());
			Interlocked.Increment(ref state.OwnerGeneration);
			QueueOwnedPageCorrection(page, state, owner);
			return;
		}

		QueueDescendants(root, owner);
	}

	internal static void CorrectOwner(DependencyObject root, Window owner)
	{
		if (root == null || owner == null || !Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		CorrectOwnedDescendants(root, owner);
	}

	private static void QueueOwnedPageCorrection(Wpf.Ui.Controls.UiPage page, TraversalState state, Window owner)
	{
		if (Interlocked.Exchange(ref state.OwnerPending, 1) != 0)
		{
			return;
		}

		page.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
		{
			int generation = Volatile.Read(ref state.OwnerGeneration);
			try
			{
				CorrectOwnedDescendants(page, FindPageOwner(page) ?? owner);
			}
			finally
			{
				Volatile.Write(ref state.OwnerPending, 0);
			}

			if (generation != Volatile.Read(ref state.OwnerGeneration))
			{
				QueueOwnedPageCorrection(page, state, owner);
			}
		}));
	}

	private static void CorrectOwnedDescendants(DependencyObject root, Window owner)
	{
		foreach (TextBlock block in FindDescendants<TextBlock>(root))
		{
			FlowState state = FlowStates.GetValue(block, static _ => new FlowState());
			state.Owner = new WeakReference<Window>(owner);
			Initialize(block, state);
			Correct(block, state, true);
		}
	}

	private static void QueueCorrection(TextBlock block)
	{
		if (Wpf.Ui.Animations.RenderReady.IsLayoutMotionActive)
		{
			return;
		}

		FlowState state = FlowStates.GetValue(block, static _ => new FlowState());
		Initialize(block, state);
		if (!CanCorrect(block, state))
		{
			return;
		}

		long now = Environment.TickCount64;
		if (now - state.CorrectedAtTicks > SettleMilliseconds)
		{
			state.Corrections = 0;
			state.Shrinks = 0;
		}

		if (state.Corrections >= MaxFlowCorrections)
		{
			if (!HasOwnerWidthChanged(block, state))
			{
				return;
			}
			state.Corrections = 0;
		}

		if (Interlocked.Exchange(ref state.Pending, 1) != 0)
		{
			return;
		}

		block.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
		{
			try
			{
				Correct(block, state);
			}
			finally
			{
				Volatile.Write(ref state.Pending, 0);
			}
		}));
	}

	private static void QueuePageCorrection(Wpf.Ui.Controls.UiPage page)
	{
		TraversalState state = PageStates.GetValue(page, static _ => new TraversalState());
		if (Interlocked.Exchange(ref state.Pending, 1) == 0)
		{
			page.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
			{
				Volatile.Write(ref state.Pending, 0);
				QueueDescendants(page, FindPageOwner(page));
			}));
		}

		if (Interlocked.Exchange(ref state.AlignmentPending, 1) == 0)
		{
			page.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
			{
				try
				{
					CorrectScrollableAlignment(page);
				}
				finally
				{
					Volatile.Write(ref state.AlignmentPending, 0);
				}
			}));
		}
	}

	private static void QueueWindowCorrection(Window window)
	{
		TraversalState state = WindowStates.GetValue(window, static _ => new TraversalState());
		QueueTraversal(window, state);
	}

	private static void QueueTraversal(FrameworkElement root, TraversalState state)
	{
		if (Interlocked.Exchange(ref state.Pending, 1) != 0)
		{
			return;
		}

		root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
		{
			Volatile.Write(ref state.Pending, 0);
			QueueDescendants(root, root as Window);
		}));
	}

	private static void QueueDescendants(DependencyObject root, Window? owner)
	{
		Stack<DependencyObject> pending = new();
		pending.Push(root);
		while (pending.Count > 0)
		{
			DependencyObject current = pending.Pop();
			if (current is TextBlock block)
			{
				if (owner != null)
				{
					FlowState flow = FlowStates.GetValue(block, static _ => new FlowState());
					flow.Owner = new WeakReference<Window>(owner);
				}

				QueueCorrection(block);
			}

			int children = VisualTreeHelper.GetChildrenCount(current);
			for (int index = children - 1; index >= 0; index--)
			{
				pending.Push(VisualTreeHelper.GetChild(current, index));
			}
		}
	}

	private static Window? FindPageOwner(Wpf.Ui.Controls.UiPage page)
	{
		if (PageOwners.TryGetValue(page, out OwnerState? state)
			&& state?.Owner != null
			&& state.Owner.TryGetTarget(out Window? stored))
		{
			return stored;
		}

		Window? direct = Window.GetWindow(page);
		if (direct != null)
		{
			return direct;
		}

		if (Application.Current == null)
		{
			return null;
		}

		foreach (Window window in Application.Current.Windows)
		{
			if (window.FindName("RootFrame") is Frame frame && ReferenceEquals(frame.Content, page))
			{
				return window;
			}
		}

		return null;
	}

	private static void CorrectScrollableAlignment(DependencyObject root)
	{
		foreach (ScrollViewer viewer in FindDescendants<ScrollViewer>(root))
		{
			double available = viewer.ViewportHeight > 0.0 ? viewer.ViewportHeight : viewer.ActualHeight;
			if (available <= 0.0)
			{
				continue;
			}

			foreach (FrameworkElement element in FindDescendants<FrameworkElement>(viewer))
			{
				if (ReferenceEquals(element, viewer))
				{
					continue;
				}

				AlignmentState state = AlignmentStates.GetValue(element, static _ => new AlignmentState());
				long now = Environment.TickCount64;
				if (now - state.FlippedAtTicks > SettleMilliseconds)
				{
					state.Flips = 0;
					state.Latched = false;
				}

				if (state.Latched)
				{
					continue;
				}

				bool centered = state.OwnsAlignment
					? state.OriginalAlignment == VerticalAlignment.Center
					: element.VerticalAlignment == VerticalAlignment.Center;
				if (!centered)
				{
					continue;
				}

				double occupied = element.DesiredSize.Height + element.Margin.Top + element.Margin.Bottom;
				if (!state.OwnsAlignment && occupied > available + Tolerance)
				{
					state.OriginalAlignment = element.VerticalAlignment;
					state.OwnsAlignment = true;
					state.Flips++;
					state.FlippedAtTicks = now;
					element.SetCurrentValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
				}
				else if (state.OwnsAlignment && occupied < available - AlignmentHysteresis)
				{
					state.OwnsAlignment = false;
					state.Flips++;
					state.FlippedAtTicks = now;
					element.SetCurrentValue(FrameworkElement.VerticalAlignmentProperty, state.OriginalAlignment);
				}

				if (state.Flips >= MaxAlignmentFlips)
				{
					state.Latched = true;

					if (!state.OwnsAlignment)
					{
						state.OriginalAlignment = element.VerticalAlignment;
						state.OwnsAlignment = true;
						element.SetCurrentValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
					}
				}
			}
		}
	}

	private static List<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
	{
		List<T> found = new();
		Stack<DependencyObject> pending = new();
		pending.Push(root);
		while (pending.Count > 0)
		{
			DependencyObject current = pending.Pop();
			if (current is T match)
			{
				found.Add(match);
			}

			int children = VisualTreeHelper.GetChildrenCount(current);
			for (int index = children - 1; index >= 0; index--)
			{
				pending.Push(VisualTreeHelper.GetChild(current, index));
			}
		}

		return found;
	}

	private static void Correct(TextBlock block, FlowState state, bool ownerAttached = false)
	{
		RemoveUndrawableCharacters(block);
		if (!ownerAttached && !CanCorrect(block, state))
		{
			return;
		}

		try
		{
			if (!state.Sanitized)
			{
				state.Sanitized = true;
				LinuxInlineText.Sanitize(block);
			}

			if (IsCompactText(block))
			{
				RestoreOriginalFlow(block, state);
				return;
			}

			ApplyBodyFlow(block, state);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxTextGuard::Correct", "A text element could not be constrained: " + ex.Message);
		}
	}

	private static bool CanCorrect(TextBlock block, FlowState state)
	{
		bool attachedWithGeometry = state.Owner != null
			&& state.Owner.TryGetTarget(out _)
			&& VisualTreeHelper.GetParent(block) != null
			&& (block.ActualWidth > 0.0 || block.DesiredSize.Width > 0.0);
		return block.IsLoaded || attachedWithGeometry;
	}

	internal static bool IsCompactText(TextBlock block)
	{
		if (block == null || GetPreserveCompactLayout(block) || block.TextTrimming != TextTrimming.None)
		{
			return true;
		}
		if (block.FontSize >= 20.0)
		{
			return true;
		}

		if (HasSingleLineHeight(block))
		{
			return true;
		}

		DependencyObject? current = block;
		while (current is not null)
		{
			if (IsCompactOwner(current))
			{
				return true;
			}

			if (current is FrameworkElement element && element.TemplatedParent is DependencyObject templatedParent && IsCompactOwner(templatedParent))
			{
				return true;
			}

			current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
		}

		return false;
	}

	internal static bool IsAutomaticallyWrapped(TextBlock block)
	{
		return block is not null
			&& FlowStates.TryGetValue(block, out FlowState? state)
			&& state is not null
			&& state.OwnsWrapping;
	}

	private static bool HasSingleLineHeight(TextBlock block)
	{
		double height = block.Height;
		if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0.0)
		{
			return false;
		}

		double lineHeight = double.IsNaN(block.LineHeight) || block.LineHeight <= 0.0
			? block.FontSize * 1.45
			: block.LineHeight;
		return height <= lineHeight + Tolerance;
	}

	private static bool IsCompactOwner(DependencyObject element)
	{
		if (element is Wpf.Ui.Controls.CardControl or Wpf.Ui.Controls.CardAction or TabControl)
			return false;

		return element is ButtonBase
			or System.Windows.Controls.Primitives.Selector
			or ComboBoxItem
			or ListBoxItem
			or ListViewItem
			or System.Windows.Controls.TreeViewItem
			or System.Windows.Controls.MenuItem
			or TabItem
			or TextBoxBase
			or System.Windows.Controls.PasswordBox
			or DataGridCell
			or DataGridColumnHeader
			or Wpf.Ui.Controls.NavigationItem
			or TitleBar;
	}

	private static void RestoreOriginalFlow(TextBlock block, FlowState state)
	{
		if (state.Source.Length > 0 && state.Rendered.Length > 0 && string.Equals(block.Text, state.Rendered, StringComparison.Ordinal))
		{
			block.SetCurrentValue(TextBlock.TextProperty, state.Source);
			state.Rendered = string.Empty;
		}

		if (state.OwnsWrapping)
		{
			block.SetCurrentValue(TextBlock.TextWrappingProperty, state.OriginalWrapping);
			state.OwnsWrapping = false;
		}

		if (state.OwnsMaxWidth)
		{
			block.SetCurrentValue(FrameworkElement.MaxWidthProperty, state.OriginalMaxWidth);
			state.OwnsMaxWidth = false;
			state.AppliedMaxWidth = double.NaN;
		}

		if (state.OwnsLineHeight)
		{
			block.SetCurrentValue(TextBlock.LineHeightProperty, state.OriginalLineHeight);
			state.OwnsLineHeight = false;
			state.AppliedLineHeight = double.NaN;
		}

		if (state.OwnsMinHeight)
		{
			block.SetCurrentValue(FrameworkElement.MinHeightProperty, state.OriginalMinHeight);
			state.OwnsMinHeight = false;
			state.AppliedMinHeight = double.NaN;
		}

		if (state.OwnsFontSize)
		{
			state.OwnsFontSize = false;
			state.AppliedFontSize = double.NaN;
			block.SetCurrentValue(TextElement.FontSizeProperty, state.OriginalFontSize);
		}
	}

	private static void ApplyBodyFlow(TextBlock block, FlowState state)
	{
		bool rich = LinuxInlineText.HasRichInlines(block);
		string current = ReadText(block);
		if (current.Length == 0)
		{
			RestoreOriginalFlow(block, state);
			return;
		}
		if (!rich && !string.Equals(current, state.Rendered, StringComparison.Ordinal))
		{
			state.Source = RemoveBreakOpportunities(current);
			state.Rendered = string.Empty;
		}
		else if (!rich && state.Source.Length == 0)
		{
			state.Source = RemoveBreakOpportunities(current);
		}

		string text = rich ? current : state.Source;

		double limit = ResolveStableLimit(block, state);
		if (limit < MinimumLimit)
		{
			return;
		}

		double target = Math.Max(MinimumLimit, limit - Math.Max(WrapSafety, limit * 0.02));
		if (HasFiniteWidth(state.OriginalMaxWidth))
		{
			target = Math.Min(target, state.OriginalMaxWidth);
		}

		if (IsSelfDrivenShrink(block, state, target))
		{
			return;
		}

		bool explicitWrap = state.OriginalWrapping != TextWrapping.NoWrap;
		bool overflow = MeasureLongestLine(block, text) > target + Tolerance;
		if (!explicitWrap && !overflow)
		{
			RestoreOriginalFlow(block, state);
			return;
		}

		bool changed = false;
		string rendered = text;
		if (!rich)
		{
			rendered = AddBreakOpportunities(block, text, target);
			int preliminaryLines = NormalizeNewlines(rendered).Split('\n').Length;
			bool useCompactBodySize = state.OriginalFontSize >= 14.0
				&& (preliminaryLines >= 3 || state.OwnsFontSize && preliminaryLines >= 2);
			if (useCompactBodySize)
			{
				double fontSize = Math.Max(11.0, Math.Round(state.OriginalFontSize * 0.93 * 2.0) / 2.0);
				if (!state.OwnsFontSize || Math.Abs(state.AppliedFontSize - fontSize) > Tolerance)
				{
					state.OwnsFontSize = true;
					state.AppliedFontSize = fontSize;
					block.SetCurrentValue(TextElement.FontSizeProperty, fontSize);
					changed = true;
				}
				rendered = AddBreakOpportunities(block, text, target);
			}
			else if (state.OwnsFontSize)
			{
				state.OwnsFontSize = false;
				state.AppliedFontSize = double.NaN;
				block.SetCurrentValue(TextElement.FontSizeProperty, state.OriginalFontSize);
				changed = true;
				rendered = AddBreakOpportunities(block, text, target);
			}

			state.Rendered = rendered;
			if (!string.Equals(current, rendered, StringComparison.Ordinal))
			{
				block.SetCurrentValue(TextBlock.TextProperty, rendered);
				changed = true;
			}
		}

		if (block.TextWrapping == TextWrapping.NoWrap)
		{
			block.SetCurrentValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
			state.OwnsWrapping = true;
			changed = true;
		}

		if (!state.OwnsMaxWidth || Math.Abs(state.AppliedMaxWidth - target) > Tolerance)
		{
			block.SetCurrentValue(FrameworkElement.MaxWidthProperty, target);
			state.OwnsMaxWidth = true;
			state.AppliedMaxWidth = target;
			changed = true;
		}

		if (!state.OwnsFontSize && state.OwnsLineHeight && HasFiniteWidth(state.OriginalLineHeight))
		{
			block.SetCurrentValue(TextBlock.LineHeightProperty, state.OriginalLineHeight);
			state.OwnsLineHeight = false;
			state.AppliedLineHeight = double.NaN;
			changed = true;
		}

		if (state.OwnsFontSize || double.IsNaN(state.OriginalLineHeight) || state.OriginalLineHeight <= 0.0)
		{
			double lineHeight = state.OwnsFontSize && HasFiniteWidth(state.OriginalLineHeight)
				? Math.Max(block.FontSize * 1.3, state.OriginalLineHeight * block.FontSize / state.OriginalFontSize)
				: Math.Ceiling(block.FontSize * 1.45 * 2.0) / 2.0;
			if (!state.OwnsLineHeight || Math.Abs(state.AppliedLineHeight - lineHeight) > Tolerance)
			{
				block.SetCurrentValue(TextBlock.LineHeightProperty, lineHeight);
				state.OwnsLineHeight = true;
				state.AppliedLineHeight = lineHeight;
				changed = true;
			}
		}

		double effectiveLineHeight = state.OwnsLineHeight ? state.AppliedLineHeight : block.LineHeight;
		if (double.IsNaN(effectiveLineHeight) || effectiveLineHeight <= 0.0)
		{
			effectiveLineHeight = block.FontSize * 1.45;
		}

		string layoutText = rich ? text : state.Rendered;
		int renderedLines = EstimateLineCount(block, layoutText, target);
		double minimumHeight = Math.Max(state.OriginalMinHeight, renderedLines * effectiveLineHeight + block.Padding.Top + block.Padding.Bottom);
		if (renderedLines > 1 && (!state.OwnsMinHeight || Math.Abs(state.AppliedMinHeight - minimumHeight) > Tolerance))
		{
			block.SetCurrentValue(FrameworkElement.MinHeightProperty, minimumHeight);
			state.OwnsMinHeight = true;
			state.AppliedMinHeight = minimumHeight;
			changed = true;
		}

		if (!changed)
		{
			return;
		}

		state.Corrections++;
		state.CorrectedAtTicks = Environment.TickCount64;
		Interlocked.Increment(ref _corrected);
		if (Interlocked.Exchange(ref _reported, 1) == 0)
		{
			App.Logger?.WriteLine("LinuxTextGuard", "Applied stable body constraints while preserving source values");
		}
	}

	private static double ResolveOwnerWidth(TextBlock block, FlowState state)
	{
		Window? owner = null;
		if (state.Owner != null)
		{
			state.Owner.TryGetTarget(out owner);
		}

		owner ??= Window.GetWindow(block);
		return owner?.ActualWidth ?? double.NaN;
	}

	private static bool HasOwnerWidthChanged(TextBlock block, FlowState state)
	{
		double ownerWidth = ResolveOwnerWidth(block, state);
		return double.IsNaN(state.OwnerWidth) || double.IsNaN(ownerWidth) || Math.Abs(state.OwnerWidth - ownerWidth) > Tolerance;
	}

	private static bool IsSelfDrivenShrink(TextBlock block, FlowState state, double target)
	{
		double ownerWidth = ResolveOwnerWidth(block, state);

		if (double.IsNaN(state.OwnerWidth) || double.IsNaN(ownerWidth) || Math.Abs(state.OwnerWidth - ownerWidth) > Tolerance)
		{
			state.OwnerWidth = ownerWidth;
			state.Shrinks = 0;
			return false;
		}

		if (!state.OwnsMaxWidth || double.IsNaN(state.AppliedMaxWidth) || target >= state.AppliedMaxWidth - Tolerance)
		{
			state.Shrinks = 0;
			return false;
		}

		state.Shrinks++;
		return state.Shrinks > MaxSelfDrivenShrinks;
	}

	private static double ResolveStableLimit(TextBlock block, FlowState state)
	{
		double limit = double.PositiveInfinity;
		TakeLimit(ref limit, block.Width);
		TakeLimit(ref limit, state.OriginalMaxWidth);
		Window? owner = null;
		if (state.Owner != null)
		{
			state.Owner.TryGetTarget(out owner);
		}

		owner ??= Window.GetWindow(block);
		if (owner?.FindName("RootFrame") is FrameworkElement rootFrame && rootFrame.ActualWidth > 0.0)
		{
			TakeLimit(ref limit, rootFrame.ActualWidth - block.Margin.Left - block.Margin.Right - WrapSafety);
		}

		double inset = block.Margin.Left + block.Margin.Right;
		UIElement child = block;
		DependencyObject? current = VisualTreeHelper.GetParent(block) ?? LogicalTreeHelper.GetParent(block);
		while (current is FrameworkElement element)
		{
			if (element is Border border)
			{
				inset += border.Padding.Left + border.Padding.Right + border.BorderThickness.Left + border.BorderThickness.Right;
			}

			if (element is ScrollViewer viewer && viewer.ViewportWidth > 0.0)
			{
				TakeLimit(ref limit, viewer.ViewportWidth - inset);
				break;
			}

			if (element is Wpf.Ui.Controls.UiPage or Frame && element.ActualWidth > 0.0)
			{
				TakeLimit(ref limit, element.ActualWidth - inset);
			}

			if (element is Grid grid && TryResolveGridConstraint(grid, child, out double gridWidth))
			{
				TakeLimit(ref limit, gridWidth - inset);
			}

			if (HasFiniteWidth(element.Width))
			{
				TakeLimit(ref limit, element.Width - inset);
			}

			if (HasFiniteWidth(element.MaxWidth))
			{
				TakeLimit(ref limit, element.MaxWidth - inset);
			}

			if (element is Window window && window.ActualWidth > 0.0)
			{
				TakeLimit(ref limit, window.ActualWidth - inset);
				break;
			}

			inset += element.Margin.Left + element.Margin.Right;
			child = element;
			current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
		}

		return double.IsInfinity(limit) ? 0.0 : limit;
	}

	private static bool TryResolveGridConstraint(Grid grid, UIElement child, out double width)
	{
		width = 0.0;
		if (grid.ColumnDefinitions.Count == 0)
		{
			if (grid.HorizontalAlignment == HorizontalAlignment.Stretch && grid.ActualWidth > 0.0)
			{
				width = grid.ActualWidth;
				return true;
			}

			return false;
		}

		int column = Grid.GetColumn(child);
		int span = Math.Max(1, Grid.GetColumnSpan(child));
		if (column < 0 || column >= grid.ColumnDefinitions.Count)
		{
			return false;
		}

		bool stable = false;
		for (int index = column; index < grid.ColumnDefinitions.Count && index < column + span; index++)
		{
			ColumnDefinition definition = grid.ColumnDefinitions[index];
			width += definition.ActualWidth;
			stable |= definition.Width.GridUnitType != GridUnitType.Auto;
		}

		return stable && width > 0.0;
	}

	private static bool HasFiniteWidth(double value)
	{
		return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
	}

	private static void TakeLimit(ref double limit, double candidate)
	{
		if (HasFiniteWidth(candidate))
		{
			limit = Math.Min(limit, candidate);
		}
	}

	private static double MeasureLongestLine(TextBlock block, string text)
	{
		double longest = 0.0;
		foreach (string line in NormalizeNewlines(text).Split('\n'))
		{
			longest = Math.Max(longest, LinuxInlineText.MeasureCached(block, line));
		}

		return longest;
	}

	private static int EstimateLineCount(TextBlock block, string text, double limit)
	{
		int total = 0;
		double space = LinuxInlineText.MeasureCached(block, " ");
		foreach (string paragraph in NormalizeNewlines(text).Split('\n'))
		{
			if (paragraph.Length == 0)
			{
				total++;
				continue;
			}

			int lines = 1;
			double width = 0.0;
			foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				double wordWidth = LinuxInlineText.MeasureCached(block, word);
				double addition = width > 0.0 ? wordWidth + space : wordWidth;
				if (width > 0.0 && width + addition > limit)
				{
					lines++;
					width = wordWidth;
				}
				else
				{
					width += addition;
				}
			}

			total += lines;
		}

		return Math.Max(1, total);
	}

	internal static string RemoveBreakOpportunities(string text)
	{
		return string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\u200B", string.Empty, StringComparison.Ordinal);
	}

	internal static string GetSourceText(TextBlock block)
	{
		if (block != null && FlowStates.TryGetValue(block, out FlowState? state) && state is not null && state.Source.Length > 0)
		{
			return state.Source;
		}

		return block == null ? string.Empty : RemoveBreakOpportunities(block.Text ?? string.Empty);
	}

	internal static string GetFlowState(TextBlock block)
	{
		if (block == null || !FlowStates.TryGetValue(block, out FlowState? state) || state is null)
		{
			return "missing";
		}

		Window? owner = null;
		state.Owner?.TryGetTarget(out owner);
		FrameworkElement? frame = owner?.FindName("RootFrame") as FrameworkElement;
		return "max=" + state.AppliedMaxWidth.ToString(CultureInfo.InvariantCulture)
			+ ",min=" + state.AppliedMinHeight.ToString(CultureInfo.InvariantCulture)
			+ ",line=" + state.AppliedLineHeight.ToString(CultureInfo.InvariantCulture)
			+ ",owner=" + (owner == null ? "none" : owner.GetType().FullName)
			+ ",frame=" + (frame?.ActualWidth.ToString(CultureInfo.InvariantCulture) ?? "none")
			+ ",loaded=" + block.IsLoaded.ToString(CultureInfo.InvariantCulture)
			+ ",limit=" + ResolveStableLimit(block, state).ToString(CultureInfo.InvariantCulture);
	}

	private static string AddBreakOpportunities(TextBlock block, string text, double limit)
	{
		string[] paragraphs = NormalizeNewlines(text).Split('\n');
		List<string> lines = new();
		foreach (string paragraph in paragraphs)
		{
			if (paragraph.Length == 0)
			{
				lines.Add(string.Empty);
				continue;
			}

			System.Text.StringBuilder line = new();
			double lineWidth = 0.0;
			double spaceWidth = LinuxInlineText.MeasureCached(block, " ");
			foreach (string word in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
			{
				List<string> pieces = SplitToken(block, word, limit);
				for (int pieceIndex = 0; pieceIndex < pieces.Count; pieceIndex++)
				{
					string piece = pieces[pieceIndex];
					double pieceWidth = LinuxInlineText.MeasureCached(block, piece);
					double addition = line.Length == 0 ? pieceWidth : spaceWidth + pieceWidth;
					if (line.Length > 0 && lineWidth + addition > limit)
					{
						lines.Add(line.ToString());
						line.Clear();
						lineWidth = 0.0;
						addition = pieceWidth;
					}

					if (line.Length > 0)
					{
						line.Append(' ');
					}

					line.Append(piece);
					lineWidth += addition;
					if (pieceIndex < pieces.Count - 1)
					{
						lines.Add(line.ToString());
						line.Clear();
						lineWidth = 0.0;
					}
				}
			}

			if (line.Length > 0)
			{
				lines.Add(line.ToString());
			}
		}

		return string.Join("\n", lines);
	}

	private static List<string> SplitToken(TextBlock block, string token, double limit)
	{
		if (LinuxInlineText.MeasureCached(block, token) <= limit)
		{
			return [token];
		}

		List<string> pieces = new();
		System.Text.StringBuilder piece = new();
		TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(token);
		while (elements.MoveNext())
		{
			string element = elements.GetTextElement();
			string candidate = piece.ToString() + element;
			if (piece.Length > 0 && LinuxInlineText.MeasureCached(block, candidate) > limit)
			{
				pieces.Add(piece.ToString());
				piece.Clear();
			}

			piece.Append(element);
		}

		if (piece.Length > 0)
		{
			pieces.Add(piece.ToString());
		}

		return pieces;
	}

	private static string NormalizeNewlines(string text)
	{
		return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
	}

	private static string ReadText(TextBlock block)
	{
		string direct = block.Text ?? string.Empty;
		return direct.Length > 0 ? direct : LinuxInlineText.ReadInlineText(block);
	}
}
