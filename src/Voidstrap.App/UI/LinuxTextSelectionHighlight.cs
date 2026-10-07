using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Voidstrap.UI;

internal static class LinuxTextSelectionHighlight
{
	private const int MaximumRichPositions = 20000;

	private static readonly ConditionalWeakTable<TextBoxBase, SelectionAdorner> Adorners = new();

	private static bool _installed;

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.UsesPortableUi)
			return;

		_installed = true;
		EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFocusChanged), true);
		EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFocusChanged), true);
		EventManager.RegisterClassHandler(typeof(TextBoxBase), TextBoxBase.SelectionChangedEvent, new RoutedEventHandler(OnChanged), true);
		EventManager.RegisterClassHandler(typeof(TextBoxBase), TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged), true);
		EventManager.RegisterClassHandler(typeof(TextBoxBase), ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged), true);
		EventManager.RegisterClassHandler(typeof(TextBoxBase), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnSizeChanged), true);
	}

	private static void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => Refresh(sender);

	private static void OnChanged(object sender, RoutedEventArgs e) => Refresh(sender);

	private static void OnTextChanged(object sender, TextChangedEventArgs e) => Refresh(sender);

	private static void OnScrollChanged(object sender, ScrollChangedEventArgs e) => Refresh(sender);

	private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh(sender);

	private static void Refresh(object sender)
	{
		if (sender is not TextBoxBase box)
			return;

		try
		{
			bool visible = HasSelection(box) && (box.IsKeyboardFocusWithin || box.IsInactiveSelectionHighlightEnabled);
			if (!Adorners.TryGetValue(box, out SelectionAdorner? adorner))
			{
				if (!visible)
					return;
				adorner = new SelectionAdorner(box);
				Adorners.Add(box, adorner);
			}

			AdornerLayer? layer = AdornerLayer.GetAdornerLayer(box);
			if (layer is null)
				return;
			if (!ReferenceEquals(adorner.Layer, layer))
			{
				adorner.Layer?.Remove(adorner);
				layer.Add(adorner);
				adorner.Layer = layer;
			}

			adorner.Showing = visible;
			adorner.InvalidateVisual();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxTextSelectionHighlight", "The selection highlight could not be updated: " + ex.Message);
		}
	}

	private static bool HasSelection(TextBoxBase box)
	{
		return box switch
		{
			TextBox text => text.SelectionLength > 0,
			RichTextBox rich => !rich.Selection.IsEmpty,
			_ => false
		};
	}

	private sealed class SelectionAdorner : Adorner
	{
		public SelectionAdorner(TextBoxBase box) : base(box)
		{
			IsHitTestVisible = false;
		}

		public AdornerLayer? Layer { get; set; }

		public bool Showing { get; set; }

		protected override void OnRender(DrawingContext drawingContext)
		{
			if (!Showing || AdornedElement is not TextBoxBase box || !box.IsVisible)
				return;

			List<Rect> rects = [];
			try
			{
				if (box is TextBox text)
					CollectTextBox(text, rects);
				else if (box is RichTextBox rich)
					CollectRichTextBox(rich, rects);
			}
			catch (Exception)
			{
				return;
			}

			if (rects.Count == 0)
				return;

			drawingContext.PushClip(new RectangleGeometry(ContentBounds(box)));
			drawingContext.PushOpacity(Math.Clamp(box.SelectionOpacity, 0.0, 1.0));
			Brush brush = box.SelectionBrush ?? SystemColors.HighlightBrush;
			foreach (Rect rect in rects)
				drawingContext.DrawRectangle(brush, null, rect);
			drawingContext.Pop();
			drawingContext.Pop();
		}

		private static void CollectTextBox(TextBox box, List<Rect> rects)
		{
			int start = box.SelectionStart;
			int end = start + box.SelectionLength;
			string text = box.Text ?? string.Empty;
			if (end <= start || start >= text.Length + 1)
				return;

			int firstLine = box.GetLineIndexFromCharacterIndex(start);
			int lastLine = box.GetLineIndexFromCharacterIndex(Math.Max(start, end - 1));
			if (firstLine < 0 || lastLine < 0)
				return;

			int firstVisible = box.GetFirstVisibleLineIndex();
			int lastVisible = box.GetLastVisibleLineIndex();
			if (firstVisible >= 0)
				firstLine = Math.Max(firstLine, firstVisible);
			if (lastVisible >= 0)
				lastLine = Math.Min(lastLine, lastVisible);

			for (int line = firstLine; line <= lastLine; line++)
			{
				int lineStart = box.GetCharacterIndexFromLineIndex(line);
				int lineLength = box.GetLineLength(line);
				if (lineStart < 0 || lineLength < 0)
					continue;

				int from = Math.Max(start, lineStart);
				int to = Math.Min(end, lineStart + lineLength);
				bool selectsBreak = false;
				while (to > from && to <= text.Length && (text[to - 1] == '\n' || text[to - 1] == '\r'))
				{
					to--;
					selectsBreak = true;
				}
				if (to < from)
					continue;

				Rect first = box.GetRectFromCharacterIndex(Math.Min(from, text.Length));
				if (first.IsEmpty)
					continue;
				double left = first.Left;
				double right = to > from ? box.GetRectFromCharacterIndex(to - 1, true).Right : left;
				if (selectsBreak || to == from)
					right += Math.Max(4.0, first.Height / 4.0);
				if (right < left)
					(left, right) = (right, left);
				rects.Add(new Rect(left, first.Top, Math.Max(1.0, right - left), first.Height));
			}
		}

		private static void CollectRichTextBox(RichTextBox box, List<Rect> rects)
		{
			TextPointer? position = box.Selection.Start.GetInsertionPosition(LogicalDirection.Forward);
			TextPointer end = box.Selection.End;
			Rect current = Rect.Empty;
			int steps = 0;
			while (position is not null && position.CompareTo(end) < 0 && steps++ < MaximumRichPositions)
			{
				TextPointer? next = position.GetNextInsertionPosition(LogicalDirection.Forward);
				Rect leading = position.GetCharacterRect(LogicalDirection.Forward);
				Rect trailing = next is not null && next.CompareTo(end) <= 0 ? next.GetCharacterRect(LogicalDirection.Backward) : leading;
				if (!leading.IsEmpty)
				{
					double right = !trailing.IsEmpty && Math.Abs(trailing.Top - leading.Top) < 0.5 ? trailing.Left : leading.Left + Math.Max(4.0, leading.Height / 4.0);
					Rect piece = new(Math.Min(leading.Left, right), leading.Top, Math.Max(1.0, Math.Abs(right - leading.Left)), leading.Height);
					if (!current.IsEmpty && Math.Abs(current.Top - piece.Top) < 0.5)
					{
						current.Union(piece);
					}
					else
					{
						if (!current.IsEmpty)
							rects.Add(current);
						current = piece;
					}
				}
				position = next;
			}
			if (!current.IsEmpty)
				rects.Add(current);
		}

		private static Rect ContentBounds(TextBoxBase box)
		{
			Rect bounds = new(box.RenderSize);
			if (box.Template?.FindName("PART_ContentHost", box) is FrameworkElement host && host.IsVisible && host.ActualWidth > 0 && host.ActualHeight > 0)
			{
				try
				{
					bounds = host.TransformToAncestor(box).TransformBounds(new Rect(host.RenderSize));
				}
				catch (InvalidOperationException)
				{
				}
			}
			return bounds;
		}
	}
}
