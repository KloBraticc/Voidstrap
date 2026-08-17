using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace Voidstrap.UI.Elements.Editor;

public class AttributeCompletionData : ICompletionData
{
	private Action _openValueAutoCompleteAction;

	private readonly string _hint;

	public ImageSource? Image => null;

	public string Text { get; private set; }

	public object Content => CompletionRow.Build(Text, _hint);

	public object? Description => string.IsNullOrEmpty(_hint) ? null : Text + " takes a " + _hint + " value.";

	public double Priority { get; }

	public AttributeCompletionData(string text, Action openValueAutoCompleteAction)
		: this(text, "", openValueAutoCompleteAction)
	{
	}

	public AttributeCompletionData(string text, string hint, Action openValueAutoCompleteAction)
	{
		_openValueAutoCompleteAction = openValueAutoCompleteAction;
		_hint = hint ?? "";
		Text = text;
	}

	public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
	{
		textArea.Document.Replace(completionSegment, Text + "=\"\"");
		textArea.Caret.Offset = textArea.Caret.Offset - 1;
		_openValueAutoCompleteAction();
	}
}

internal static class CompletionRow
{
	public static object Build(string text, string hint)
	{
		if (string.IsNullOrEmpty(hint))
			return text;

		var grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		var name = new TextBlock
		{
			Text = text,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis
		};

		var type = new TextBlock
		{
			Text = hint,
			Margin = new Thickness(14, 0, 0, 0),
			Opacity = 0.55,
			FontSize = 11,
			VerticalAlignment = VerticalAlignment.Center
		};

		Grid.SetColumn(name, 0);
		Grid.SetColumn(type, 1);
		grid.Children.Add(name);
		grid.Children.Add(type);

		return grid;
	}
}
