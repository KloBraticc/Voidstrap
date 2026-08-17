using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Voidstrap.UI.Elements.ContextMenu;

public class TextMarkerService : DocumentColorizingTransformer, IBackgroundRenderer, ITextMarkerService
{
	private class TextMarker : ITextMarker
	{
		public int StartOffset { get; set; }

		public int Length { get; set; }

		public Color? BackgroundColor { get; set; }

		public Color? ForegroundColor { get; set; }

		public string ToolTip { get; set; }
	}

	private readonly TextDocument _document;

	private readonly List<TextMarker> _markers = new List<TextMarker>();

	public IEnumerable<ITextMarker> TextMarkers => _markers;

	public KnownLayer Layer => KnownLayer.Selection;

	public TextMarkerService(TextDocument document)
	{
		_document = document ?? throw new ArgumentNullException("document");
	}

	public ITextMarker Create(int startOffset, int length)
	{
		TextMarker textMarker = new TextMarker
		{
			StartOffset = startOffset,
			Length = length
		};
		_markers.Add(textMarker);
		return textMarker;
	}

	public void RemoveAll(Predicate<ITextMarker> predicate)
	{
		_markers.RemoveAll(predicate.Invoke);
	}

	public void Draw(TextView textView, DrawingContext drawingContext)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		if (_markers.Count == 0)
		{
			return;
		}
		foreach (TextMarker marker in _markers)
		{
			TextSegment segment = new TextSegment
			{
				StartOffset = marker.StartOffset,
				Length = marker.Length
			};
			foreach (Rect item in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
			{
				drawingContext.DrawRectangle(new SolidColorBrush(marker.BackgroundColor ?? Colors.Transparent), null, item);
			}
		}
	}

	protected override void ColorizeLine(DocumentLine line)
	{
		foreach (TextMarker marker in _markers)
		{
			if (line.EndOffset >= marker.StartOffset && line.Offset <= marker.StartOffset + marker.Length && marker.ForegroundColor.HasValue)
			{
				ChangeLinePart(Math.Max(line.Offset, marker.StartOffset), Math.Min(line.EndOffset, marker.StartOffset + marker.Length), delegate(VisualLineElement c)
				{
					c.TextRunProperties.SetForegroundBrush(new SolidColorBrush(marker.ForegroundColor.Value));
				});
			}
		}
	}
}
