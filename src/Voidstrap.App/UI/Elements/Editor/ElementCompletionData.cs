using System;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace Voidstrap.UI.Elements.Editor;

public class ElementCompletionData : ICompletionData
{
	public ImageSource? Image => null;

	public string Text { get; private set; }

	public object Content => Text;

	public object? Description => null;

	public double Priority { get; }

	public ElementCompletionData(string text)
	{
		Text = text;
	}

	public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
	{
		textArea.Document.Replace(completionSegment, Text);
	}
}
