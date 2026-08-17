using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using System.Windows.Threading;
using Voidstrap.Utility;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.Extensions;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Editor;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Editor;

public partial class BootstrapperEditorWindow : WpfUiWindow{
	private static class CustomBootstrapperSchema
	{
		private class Schema
		{
			public Dictionary<string, Element> Elements { get; set; } = new Dictionary<string, Element>();

			public Dictionary<string, Type> Types { get; set; } = new Dictionary<string, Type>();
		}

		private class Element
		{
			public string? SuperClass { get; set; }

			public bool IsCreatable { get; set; }

			public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
		}

		public class Type
		{
			public bool CanHaveElement { get; set; }

			public List<string>? Values { get; set; }
		}

		private static Schema? _schema;

		public static SortedDictionary<string, SortedDictionary<string, string>> ElementInfo { get; set; } = new SortedDictionary<string, SortedDictionary<string, string>>();

		public static Dictionary<string, List<string>> PropertyElements { get; set; } = new Dictionary<string, List<string>>();

		public static SortedDictionary<string, Type> Types { get; set; } = new SortedDictionary<string, Type>();

		public static void ParseSchema()
		{
			if (_schema != null)
			{
				return;
			}
			try
			{
				_schema = JsonSerializer.Deserialize<Schema>(Resource.GetString("CustomBootstrapperSchema.json"));
				if (_schema == null)
				{
					throw new Exception("Deserialised CustomBootstrapperSchema is null");
				}
				foreach (KeyValuePair<string, Type> type in _schema.Types)
				{
					Types[type.Key] = type.Value;
				}
				PopulateElementInfo();
			}
			catch (Exception ex)
			{
				_schema = null;
				Types.Clear();
				ElementInfo.Clear();
				PropertyElements.Clear();
				App.Logger?.WriteLine("BootstrapperEditorWindow::ParseSchema", "Could not load the editor schema, autocomplete is off: " + ex.Message);
			}
		}

		private static (SortedDictionary<string, string>, List<string>) GetElementAttributes(string name, Element element)
		{
			if (ElementInfo.ContainsKey(name))
			{
				return (ElementInfo[name], PropertyElements[name]);
			}
			List<string> list = new List<string>();
			SortedDictionary<string, string> sortedDictionary = new SortedDictionary<string, string>();
			foreach (KeyValuePair<string, string> attribute in element.Attributes)
			{
				sortedDictionary[attribute.Key] = attribute.Value;
				if (!Types.TryGetValue(attribute.Value, out Type? attributeType))
				{
					continue;
				}
				if (attributeType.CanHaveElement)
				{
					list.Add(attribute.Key);
				}
			}
			if (element.SuperClass != null && _schema!.Elements.TryGetValue(element.SuperClass, out Element? superClass))
			{
				var (sortedDictionary2, list2) = GetElementAttributes(element.SuperClass, superClass);
				foreach (KeyValuePair<string, string> item in sortedDictionary2)
				{
					if (!sortedDictionary.ContainsKey(item.Key))
					{
						sortedDictionary[item.Key] = item.Value;
					}
				}
				foreach (string item2 in list2)
				{
					if (!list.Contains(item2))
					{
						list.Add(item2);
					}
				}
			}
			list.Sort();
			ElementInfo[name] = sortedDictionary;
			PropertyElements[name] = list;
			return (sortedDictionary, list);
		}

		private static void PopulateElementInfo()
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, Element> element in _schema.Elements)
			{
				GetElementAttributes(element.Key, element.Value);
				if (!element.Value.IsCreatable)
				{
					list.Add(element.Key);
				}
			}
			foreach (string item in list)
			{
				ElementInfo.Remove(item);
			}
		}
	}

	private BootstrapperEditorWindowViewModel _viewModel;

	private CompletionWindow? _completionWindow;

	private SearchPanel? _searchPanel;

	private const double EditorFontSize = 14;

	private const double MinimumEditorFontSize = 10;

	private const double MaximumEditorFontSize = 24;

	public BootstrapperEditorWindow(string name)
	{
		CustomBootstrapperSchema.ParseSchema();
		string text = Path.Combine(Paths.CustomThemes, name);
		string themePath = Path.Combine(text, "Theme.xml");
		string text2;

		try
		{
			text2 = File.Exists(themePath) ? File.ReadAllText(themePath) : Resource.GetString("CustomBootstrapperTemplate_Simple.xml");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow", "Could not read the theme, starting from the template: " + ex.Message);
			text2 = Resource.GetString("CustomBootstrapperTemplate_Simple.xml");
		}

		text2 = ToCRLF(text2);
		_viewModel = new BootstrapperEditorWindowViewModel();
		_viewModel.ThemeSavedCallback = ThemeSavedCallback;
		_viewModel.Directory = text;
		_viewModel.Name = name;
		_viewModel.Title = string.Format(Strings.CustomTheme_Editor_Title, name);
		_viewModel.Code = text2;
		base.DataContext = _viewModel;
		InitializeComponent();
		InitialiseCodeEditor();
		UIXML.Text = _viewModel.Code;
		UIXML.TextChanged += OnCodeChanged;
		UIXML.TextArea.TextEntered += OnTextAreaTextEntered;
		UIXML.TextArea.PreviewKeyDown += OnEditorPreviewKeyDown;
		UIXML.TextArea.Caret.PositionChanged += OnEditorPositionChanged;
		UIXML.TextArea.SelectionChanged += OnEditorSelectionChanged;
		InitialiseFiles();
		InitialisePreview();
		SizeChanged += OnEditorSizeChanged;
	}

	private void InitialiseCodeEditor()
	{
		UIXML.Options.IndentationSize = 2;
		UIXML.Options.ConvertTabsToSpaces = true;
		UIXML.Options.HighlightCurrentLine = true;
		UIXML.Options.AllowScrollBelowDocument = true;
		UIXML.Options.EnableTextDragDrop = true;
		UIXML.Options.CutCopyWholeLine = true;
		UIXML.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
		UIXML.TextArea.TextView.CurrentLineBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 96, 165, 250));
		UIXML.TextArea.TextView.CurrentLineBorder = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(72, 96, 165, 250)), 1);
		UIXML.TextArea.SelectionBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(110, 65, 135, 225));
		UIXML.TextArea.SelectionBorder = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 96, 165, 250)), 1);
		UIXML.TextArea.Caret.CaretBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(116, 185, 255));
		_searchPanel = SearchPanel.Install(UIXML.TextArea);
		UpdateEditorStatus();
	}

	private void OnEditorPositionChanged(object? sender, EventArgs e)
	{
		UpdateEditorStatus();
	}

	private void OnEditorSelectionChanged(object? sender, EventArgs e)
	{
		UpdateEditorStatus();
	}

	private void UpdateEditorStatus()
	{
		EditorPositionLabel.Text = "Ln " + UIXML.TextArea.Caret.Line + ", Col " + UIXML.TextArea.Caret.Column;
		EditorSelectionLabel.Text = UIXML.SelectionLength == 0 ? "No selection" : UIXML.SelectionLength + " selected";
		EditorZoomLabel.Text = Math.Round(UIXML.FontSize / EditorFontSize * 100) + "%";
	}

	private void UndoEditor_Click(object sender, RoutedEventArgs e)
	{
		if (UIXML.CanUndo)
			UIXML.Undo();
		UIXML.Focus();
	}

	private void RedoEditor_Click(object sender, RoutedEventArgs e)
	{
		if (UIXML.CanRedo)
			UIXML.Redo();
		UIXML.Focus();
	}

	private void FindEditor_Click(object sender, RoutedEventArgs e)
	{
		_searchPanel?.Open();
	}

	private void ZoomOutEditor_Click(object sender, RoutedEventArgs e)
	{
		SetEditorFontSize(UIXML.FontSize - 1);
	}

	private void ZoomInEditor_Click(object sender, RoutedEventArgs e)
	{
		SetEditorFontSize(UIXML.FontSize + 1);
	}

	private void SetEditorFontSize(double size)
	{
		UIXML.FontSize = Math.Clamp(size, MinimumEditorFontSize, MaximumEditorFontSize);
		UpdateEditorStatus();
		UIXML.Focus();
	}

	private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e)
	{
		ScalePreview();
	}


	private void ThemeSavedCallback(bool success, string message)
	{
		if (success)
		{
			Snackbar.Show(Strings.CustomTheme_Editor_Save_Success, message, SymbolRegular.CheckboxChecked24);
		}
		else
		{
			Snackbar.Show(Strings.CustomTheme_Editor_Save_Error, message, SymbolRegular.ErrorCircle24, ControlAppearance.Danger);
		}
	}

	private static string ToCRLF(string text)
	{
		return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
	}

	private void OnCodeChanged(object? sender, EventArgs e)
	{
		if (_activeFile == null || _activeFile.IsRoot)
			_viewModel.Code = UIXML.Text;

		_viewModel.CodeChanged = true;
		EditorDirtyLabel.Text = "unsaved";
		QueuePreview();
	}

	private void OnClosing(object sender, CancelEventArgs e)
	{
		if (_viewModel.CodeChanged)
		{
			switch (Frontend.ShowMessageBox(string.Format(Strings.CustomTheme_Editor_ConfirmSave, _viewModel.Name), MessageBoxImage.Asterisk, MessageBoxButton.YesNoCancel))
			{
			case MessageBoxResult.Cancel:
				e.Cancel = true;
				break;
			case MessageBoxResult.Yes:
				SaveActiveFile();
				break;
			}
		}

		if (!e.Cancel)
		{
			StopExternalWatch();
			StopThemeWatch();
		}
	}

	private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
		{
			if (e.Key == Key.Add || e.Key == Key.OemPlus)
			{
				e.Handled = true;
				SetEditorFontSize(UIXML.FontSize + 1);
				return;
			}
			if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
			{
				e.Handled = true;
				SetEditorFontSize(UIXML.FontSize - 1);
				return;
			}
			if (e.Key == Key.D0 || e.Key == Key.NumPad0)
			{
				e.Handled = true;
				SetEditorFontSize(EditorFontSize);
				return;
			}
		}
		if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
		{
			e.Handled = true;
			TryAutoComplete(force: true);
		}
	}

	private void OnTextAreaTextEntered(object sender, TextCompositionEventArgs e)
	{
		try
		{
			switch (e.Text)
			{
			case "<":
				OpenElementAutoComplete();
				break;
			case " ":
				OpenAttributeAutoComplete();
				break;
			case ".":
				OpenPropertyElementAutoComplete();
				break;
			case "/":
				AddEndTag();
				break;
			case ">":
				CloseCompletionWindow();
				break;
			case "!":
				CloseCompletionWindow();
				break;
			case "\"":
				TryAutoComplete(force: true);
				break;
			case "=":
				TryAutoComplete(force: true);
				break;
			default:
				if (e.Text.Length == 1 && IsWordChar(e.Text[0]))
					TryAutoComplete(force: false);
				break;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow::OnTextAreaTextEntered", "Autocomplete failed: " + ex.Message);
			CloseCompletionWindow();
		}
	}

	private (string, int) GetLineAndPosAtCaretPosition()
	{
		string text = UIXML.Text;
		if (string.IsNullOrEmpty(text))
		{
			return ("", -1);
		}

		int caret = Math.Clamp(UIXML.CaretOffset - 1, -1, text.Length - 1);
		if (caret < 0)
		{
			return ("", -1);
		}

		int lineStart = text.LastIndexOf('\n', caret) + 1;
		int lineEnd = text.IndexOf('\n', caret);
		if (lineEnd == -1)
		{
			lineEnd = text.Length;
		}
		if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
		{
			lineEnd--;
		}

		string line = text.Substring(lineStart, Math.Max(0, lineEnd - lineStart));
		int position = Math.Clamp(caret - lineStart, -1, line.Length - 1);
		return (line, position);
	}

	public static string? GetElementAtCursor(string xml, int offset, bool onlyAllowInside = false)
	{
		if (string.IsNullOrEmpty(xml))
		{
			return null;
		}
		if (offset >= xml.Length)
		{
			offset = xml.Length - 1;
		}
		if (offset < 0)
		{
			return null;
		}
		int num = xml.LastIndexOf('<', offset);
		if (num < 0)
		{
			return null;
		}
		if (num + 1 < xml.Length && xml[num + 1] == '/')
		{
			num++;
		}
		int num2 = xml.IndexOf(' ', num);
		if (num2 == -1)
		{
			num2 = int.MaxValue;
		}
		int num3 = xml.IndexOf('>', num);
		if (num3 == -1)
		{
			num3 = int.MaxValue;
		}
		else
		{
			if (onlyAllowInside && num3 < offset)
			{
				return null;
			}
			if (num3 > 0 && num3 < xml.Length && xml[num3 - 1] == '/')
			{
				num3--;
			}
		}
		int num4 = Math.Min(num2, num3);
		if (num3 > 0 && num3 < int.MaxValue && num4 > num)
		{
			string text = xml.Substring(num + 1, num4 - num - 1);
			if (!(text == "!--"))
			{
				return text;
			}
			return null;
		}
		return null;
	}

	private string? GetElementAtCursorNoSpaces(string xml, int offset)
	{
		(string, int) lineAndPosAtCaretPosition = GetLineAndPosAtCaretPosition();
		string item = lineAndPosAtCaretPosition.Item1;
		int num = Math.Min(lineAndPosAtCaretPosition.Item2, item.Length - 1);
		string text = "";
		while (num >= 0)
		{
			char c = item[num];
			switch (c)
			{
			case '\t':
			case ' ':
				return null;
			case '<':
				return text;
			}
			text = c + text;
			num--;
		}
		return null;
	}

	private string? ShowAttributesForElementName()
	{
		var (text, num) = GetLineAndPosAtCaretPosition();
		if (text.Count((char x) => x == '"') % 2 == 0)
		{
			int num2 = -1;
			int num3 = num;
			int num4 = text.Length - 1;
			while (num3 != -1)
			{
				num2++;
				num3 = ((num4 <= num3 + 1) ? (-1) : text.IndexOf('"', num3 + 1));
			}
			if (num2 % 2 != 0)
			{
				return null;
			}
		}
		return GetElementAtCursor(UIXML.Text, UIXML.CaretOffset, onlyAllowInside: true);
	}

	private void AddEndTag()
	{
		CloseCompletionWindow();
		if (UIXML.CaretOffset >= 2 && UIXML.Text.Length > 2 && UIXML.CaretOffset - 2 < UIXML.Text.Length && UIXML.Text[UIXML.CaretOffset - 2] == '<')
		{
			string elementAtCursor = GetElementAtCursor(UIXML.Text, UIXML.CaretOffset - 3);
			if (elementAtCursor != null)
			{
				UIXML.TextArea.Document.Insert(UIXML.CaretOffset, elementAtCursor + ">");
			}
		}
		else if ((UIXML.Text.Length <= UIXML.CaretOffset || UIXML.Text[UIXML.CaretOffset] != '>') && ShowAttributesForElementName() != null)
		{
			UIXML.TextArea.Document.Insert(UIXML.CaretOffset, ">");
		}
	}

	private enum CompletionSpot
	{
		None,
		ElementName,
		AttributeName,
		AttributeValue
	}

	private static bool IsWordChar(char value)
	{
		return char.IsLetterOrDigit(value) || value == '_' || value == '-' || value == '.' || value == ':';
	}

	private CompletionSpot DetectSpot(out string elementName, out string attributeName, out int wordStart)
	{
		elementName = "";
		attributeName = "";

		string text = UIXML.Text;
		int caret = Math.Min(UIXML.CaretOffset, text.Length);
		wordStart = caret;

		int open = -1;

		for (int i = caret - 1; i >= 0; i--)
		{
			if (text[i] == '>')
				return CompletionSpot.None;

			if (text[i] == '<')
			{
				open = i;
				break;
			}
		}

		if (open < 0)
			return CompletionSpot.None;

		int nameStart = open + 1;

		if (nameStart < text.Length && (text[nameStart] == '/' || text[nameStart] == '!' || text[nameStart] == '?'))
			return CompletionSpot.None;

		int nameEnd = nameStart;

		while (nameEnd < caret && nameEnd < text.Length && IsWordChar(text[nameEnd]))
			nameEnd++;

		elementName = text.Substring(nameStart, nameEnd - nameStart);

		if (caret <= nameEnd)
		{
			wordStart = nameStart;
			return CompletionSpot.ElementName;
		}

		int quotes = 0;

		for (int i = nameEnd; i < caret; i++)
		{
			if (text[i] == '"')
				quotes++;
		}

		if (quotes % 2 == 1)
		{
			int quote = text.LastIndexOf('"', caret - 1);
			wordStart = quote + 1;

			int cursor = quote - 1;

			while (cursor >= 0 && char.IsWhiteSpace(text[cursor]))
				cursor--;

			if (cursor >= 0 && text[cursor] == '=')
			{
				cursor--;

				while (cursor >= 0 && char.IsWhiteSpace(text[cursor]))
					cursor--;

				int end = cursor;

				while (cursor >= 0 && IsWordChar(text[cursor]))
					cursor--;

				if (end > cursor)
					attributeName = text.Substring(cursor + 1, end - cursor);
			}

			return CompletionSpot.AttributeValue;
		}

		int start = caret;

		while (start > 0 && IsWordChar(text[start - 1]))
			start--;

		wordStart = start;
		return CompletionSpot.AttributeName;
	}

	private void TryAutoComplete(bool force)
	{
		if (_completionWindow != null && !force)
			return;

		CompletionSpot spot = DetectSpot(out string element, out string attribute, out int wordStart);

		switch (spot)
		{
			case CompletionSpot.ElementName:
				OpenElementAutoComplete(wordStart);
				break;

			case CompletionSpot.AttributeName:
				OpenAttributesFor(element, wordStart);
				break;

			case CompletionSpot.AttributeValue:
				OpenValuesFor(element, attribute, wordStart);
				break;

			default:
				if (force)
					CloseCompletionWindow();
				break;
		}
	}

	private void OpenAttributesFor(string elementName, int wordStart)
	{
		if (string.IsNullOrEmpty(elementName) || !CustomBootstrapperSchema.ElementInfo.ContainsKey(elementName))
			return;

		var list = new List<ICompletionData>();

		foreach (KeyValuePair<string, string> attribute in CustomBootstrapperSchema.ElementInfo[elementName])
		{
			string type = attribute.Value;
			list.Add(new AttributeCompletionData(attribute.Key, type, delegate
			{
				OpenTypeValueAutoComplete(type);
			}));
		}

		ShowCompletionWindow(list, wordStart);
	}

	private void OpenValuesFor(string elementName, string attributeName, int wordStart)
	{
		if (string.IsNullOrEmpty(elementName) || string.IsNullOrEmpty(attributeName))
			return;

		if (!CustomBootstrapperSchema.ElementInfo.TryGetValue(elementName, out SortedDictionary<string, string>? attributes))
			return;

		if (!attributes.TryGetValue(attributeName, out string? typeName))
			return;

		if (!CustomBootstrapperSchema.Types.TryGetValue(typeName, out CustomBootstrapperSchema.Type? type) || type.Values == null)
			return;

		var list = new List<ICompletionData>();

		foreach (string value in type.Values)
			list.Add(new TypeValueCompletionData(value));

		ShowCompletionWindow(list, wordStart);
	}

	private void OpenElementAutoComplete(int wordStart)
	{
		var list = new List<ICompletionData>();

		foreach (string key in CustomBootstrapperSchema.ElementInfo.Keys)
			list.Add(new ElementCompletionData(key));

		ShowCompletionWindow(list, wordStart);
	}

	private void OpenElementAutoComplete()
	{
		List<ICompletionData> list = new List<ICompletionData>();
		foreach (string key in CustomBootstrapperSchema.ElementInfo.Keys)
		{
			list.Add(new ElementCompletionData(key));
		}
		ShowCompletionWindow(list);
	}

	private void OpenAttributeAutoComplete()
	{
		string text = ShowAttributesForElementName();
		if (text == null)
		{
			CloseCompletionWindow();
			return;
		}
		if (!CustomBootstrapperSchema.ElementInfo.ContainsKey(text))
		{
			CloseCompletionWindow();
			return;
		}
		SortedDictionary<string, string> sortedDictionary = CustomBootstrapperSchema.ElementInfo[text];
		List<ICompletionData> list = new List<ICompletionData>();
		foreach (KeyValuePair<string, string> attribute in sortedDictionary)
		{
			list.Add(new AttributeCompletionData(attribute.Key, attribute.Value, delegate
			{
				OpenTypeValueAutoComplete(attribute.Value);
			}));
		}
		ShowCompletionWindow(list);
	}

	private void OpenTypeValueAutoComplete(string typeName)
	{
		if (!CustomBootstrapperSchema.Types.TryGetValue(typeName, out CustomBootstrapperSchema.Type? type))
		{
			return;
		}
		List<string>? values = type.Values;
		if (values == null)
		{
			return;
		}
		List<ICompletionData> list = new List<ICompletionData>();
		foreach (string item in values)
		{
			list.Add(new TypeValueCompletionData(item));
		}
		ShowCompletionWindow(list);
	}

	private void OpenPropertyElementAutoComplete()
	{
		string elementAtCursorNoSpaces = GetElementAtCursorNoSpaces(UIXML.Text, UIXML.CaretOffset);
		if (elementAtCursorNoSpaces == null)
		{
			CloseCompletionWindow();
			return;
		}
		if (!CustomBootstrapperSchema.PropertyElements.ContainsKey(elementAtCursorNoSpaces))
		{
			CloseCompletionWindow();
			return;
		}
		List<string> list = CustomBootstrapperSchema.PropertyElements[elementAtCursorNoSpaces];
		List<ICompletionData> list2 = new List<ICompletionData>();
		foreach (string item in list)
		{
			list2.Add(new TypeValueCompletionData(item));
		}
		ShowCompletionWindow(list2);
	}

	private void CloseCompletionWindow()
	{
		if (_completionWindow != null)
		{
			_completionWindow.Closed -= CompletionWindow_Closed;
			_completionWindow.Close();
			_completionWindow = null;
		}
	}

	private void ShowCompletionWindow(List<ICompletionData> completionData)
	{
		ShowCompletionWindow(completionData, -1);
	}

	private void ShowCompletionWindow(List<ICompletionData> completionData, int startOffset)
	{
		CloseCompletionWindow();
		if (!completionData.Any())
		{
			return;
		}
		_completionWindow = new CompletionWindow(UIXML.TextArea);

		string prefix = "";

		if (startOffset >= 0 && startOffset <= UIXML.CaretOffset)
		{
			_completionWindow.StartOffset = startOffset;
			prefix = UIXML.Document.GetText(startOffset, UIXML.CaretOffset - startOffset);
		}

		IList<ICompletionData> completionData2 = _completionWindow.CompletionList.CompletionData;
		foreach (ICompletionData completionDatum in completionData)
		{
			completionData2.Add(completionDatum);
		}
		StyleCompletionWindow(_completionWindow, completionData);

		if (prefix.Length > 0)
		{
			_completionWindow.CompletionList.SelectItem(prefix);

			if (_completionWindow.CompletionList.SelectedItem == null)
			{
				CloseCompletionWindow();
				return;
			}
		}

		_completionWindow.Closed += CompletionWindow_Closed;
		_completionWindow.Show();
	}

	private const string CompletionItemStyleXaml = """
		<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
		       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
		       TargetType="ListBoxItem">
		  <Setter Property="Padding" Value="9,5" />
		  <Setter Property="Margin" Value="3,1" />
		  <Setter Property="SnapsToDevicePixels" Value="True" />
		  <Setter Property="HorizontalContentAlignment" Value="Stretch" />
		  <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
		  <Setter Property="Template">
		    <Setter.Value>
		      <ControlTemplate TargetType="ListBoxItem">
		        <Border x:Name="Bd" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
		          <ContentPresenter VerticalAlignment="Center" />
		        </Border>
		        <ControlTemplate.Triggers>
		          <Trigger Property="IsMouseOver" Value="True">
		            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
		          </Trigger>
		          <Trigger Property="IsSelected" Value="True">
		            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource AccentFillColorDefaultBrush}" />
		            <Setter Property="Foreground" Value="{DynamicResource TextOnAccentFillColorPrimaryBrush}" />
		          </Trigger>
		        </ControlTemplate.Triggers>
		      </ControlTemplate>
		    </Setter.Value>
		  </Setter>
		</Style>
		""";

	private Style? _completionItemStyle;

	private System.Windows.Media.Brush ThemeBrush(string key, string fallback)
	{
		if (TryFindResource(key) is System.Windows.Media.Brush found)
			return found;

		var converted = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback);
		var brush = new System.Windows.Media.SolidColorBrush(converted);
		brush.Freeze();
		return brush;
	}

	private void StyleCompletionWindow(CompletionWindow window, List<ICompletionData> items)
	{
		try
		{
			window.Background = ThemeBrush("ApplicationBackgroundBrush", "#FF1F1F23");
			window.Foreground = ThemeBrush("TextFillColorPrimaryBrush", "#FFFFFFFF");
			window.BorderBrush = ThemeBrush("ControlStrokeColorDefaultBrush", "#26FFFFFF");
			window.BorderThickness = new Thickness(1);
			window.WindowStyle = WindowStyle.None;
			window.AllowsTransparency = false;
			window.SizeToContent = SizeToContent.Manual;
			window.MaxHeight = 320;

			double longest = 0;
			foreach (ICompletionData item in items)
				longest = Math.Max(longest, (item.Text ?? "").Length);

			window.Width = Math.Min(460, Math.Max(200, longest * 8.0 + 96));

			var list = window.CompletionList;
			list.Background = System.Windows.Media.Brushes.Transparent;
			list.Foreground = window.Foreground;
			list.ApplyTemplate();

			var box = list.ListBox;
			if (box == null)
				return;

			_completionItemStyle ??= XamlReader.Parse(CompletionItemStyleXaml) as Style;

			box.Background = System.Windows.Media.Brushes.Transparent;
			box.Foreground = window.Foreground;
			box.BorderThickness = new Thickness(0);
			box.Padding = new Thickness(2);
			box.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
			System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(box, System.Windows.Controls.ScrollBarVisibility.Disabled);

			if (_completionItemStyle != null)
				box.ItemContainerStyle = _completionItemStyle;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow::StyleCompletionWindow", ex.Message);
		}
	}

	private void CompletionWindow_Closed(object? sender, EventArgs e)
	{
		if (sender is CompletionWindow completionWindow)
		{
			completionWindow.Closed -= CompletionWindow_Closed;
			if (ReferenceEquals(_completionWindow, completionWindow))
			{
				_completionWindow = null;
			}
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		UIXML.TextChanged -= OnCodeChanged;
		UIXML.TextArea.TextEntered -= OnTextAreaTextEntered;
		UIXML.TextArea.PreviewKeyDown -= OnEditorPreviewKeyDown;
		UIXML.TextArea.Caret.PositionChanged -= OnEditorPositionChanged;
		UIXML.TextArea.SelectionChanged -= OnEditorSelectionChanged;
		SizeChanged -= OnEditorSizeChanged;
		_searchPanel?.Uninstall();
		_searchPanel = null;

		if (_previewTimer != null)
		{
			_previewTimer.Stop();
			_previewTimer.Tick -= OnPreviewTimerTick;
			_previewTimer = null;
		}

		PreviewHost.Child = null;
		ReleasePreviewDialog();
		CloseCompletionWindow();
		_viewModel.Dispose();
		base.OnClosed(e);
		RestoreMainWindow();
	}

	private static void RestoreMainWindow()
	{
		try
		{
			var main = System.Windows.Application.Current?.Windows
				.OfType<Voidstrap.UI.Elements.Settings.MainWindow>()
				.FirstOrDefault();

			if (main == null)
				return;

			if (main.WindowState == System.Windows.WindowState.Minimized)
				main.WindowState = System.Windows.WindowState.Normal;

			main.Show();
			main.Activate();
			main.Topmost = true;
			main.Topmost = false;
			main.Focus();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow::RestoreMainWindow", ex.Message);
		}
	}

	#region Live preview

	private DispatcherTimer? _previewTimer;

	private Voidstrap.UI.Elements.Bootstrapper.CustomDialog? _previewDialog;
	private string? _renderedPreviewXml;

	private int _previewErrorLine;

	private void InitialisePreview()
	{
		_previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
		_previewTimer.Tick += OnPreviewTimerTick;
		RenderPreview();
	}

	private void QueuePreview()
	{
		if (_previewTimer == null || LiveToggle.IsChecked != true)
			return;

		_previewTimer.Stop();
		_previewTimer.Start();
	}

	private void OnPreviewTimerTick(object? sender, EventArgs e)
	{
		_previewTimer?.Stop();
		RenderPreview();
	}

	private void RefreshPreview_Click(object sender, RoutedEventArgs e)
	{
		SaveActiveFile(silent: true);
		RenderPreview(true);
	}

	private string CurrentThemeXml()
	{
		if (_activeFile != null && _activeFile.IsRoot && BootstrapperEditorWindowViewModel.LooksLikeThemeXml(UIXML.Text))
			return UIXML.Text;

		try
		{
			string path = Path.Combine(_viewModel.Directory, "Theme.xml");
			return File.Exists(path) ? File.ReadAllText(path) : _viewModel.Code;
		}
		catch
		{
			return _viewModel.Code;
		}
	}

	private void RenderPreview(bool force = false)
	{
		string xml = CurrentThemeXml();
		if (!force && _previewDialog != null && string.Equals(_renderedPreviewXml, xml, StringComparison.Ordinal))
			return;

		PreviewHost.Child = null;
		ReleasePreviewDialog();

		Voidstrap.UI.Elements.Bootstrapper.CustomDialog? dialog = null;

		try
		{
			dialog = new Voidstrap.UI.Elements.Bootstrapper.CustomDialog(true);
			dialog.ApplyCustomTheme(_viewModel.Name, xml);
			dialog.Message = Strings.Bootstrapper_StylePreview_TextCancel;
			dialog.CancelEnabled = true;
		}
		catch (Exception ex)
		{
			try { dialog?.Close(); } catch { }
			ShowPreviewProblem(ex);
			return;
		}

		if (dialog == null)
			return;

		try
		{
			object? content = dialog.Content;
			dialog.Content = null;

			if (content is not FrameworkElement root)
			{
				dialog.Close();
				ShowPreviewProblem(new InvalidOperationException("This theme has no visible elements."));
				return;
			}

			root.DataContext = dialog.DataContext;

			double width = double.IsNaN(dialog.Width) || dialog.Width <= 0 ? 800 : dialog.Width;
			double height = double.IsNaN(dialog.Height) || dialog.Height <= 0 ? 450 : dialog.Height;

			root.Width = width;
			root.Height = height;

			Voidstrap.Models.BackdropType backdrop = Voidstrap.UI.WindowBackdrop.ResolveFor(dialog);
			bool glass = backdrop != Voidstrap.Models.BackdropType.None;

			var layers = new System.Windows.Controls.Grid();

			if (glass)
			{
				layers.Children.Add(BuildBackdropLayer(backdrop));

				var tint = new System.Windows.Controls.Border
				{
					Background = new System.Windows.Media.SolidColorBrush(Voidstrap.UI.WindowBackdrop.CreateSurfaceColor(backdrop))
				};

				layers.Children.Add(tint);
			}

			layers.Children.Add(root);

			var shell = new System.Windows.Controls.Border
			{
				Width = width,
				Height = height,
				Background = glass ? System.Windows.Media.Brushes.Transparent : dialog.Background,
				CornerRadius = CornerFor(dialog),
				ClipToBounds = true,
				Child = layers
			};

			shell.LayoutTransform = new System.Windows.Media.ScaleTransform(1, 1);

			_previewDialog = dialog;
			_renderedPreviewXml = xml;
			PreviewHost.Child = shell;
			PreviewPlaceholder.Visibility = Visibility.Collapsed;

			ScalePreview();
			ShowPreviewOk(width, height);
		}
		catch (Exception ex)
		{
			try { dialog.Close(); } catch { }
			ShowPreviewProblem(ex);
		}
	}

	private static System.Windows.Media.ImageSource? _wallpaper;

	private static bool _wallpaperChecked;

	private static System.Windows.Media.ImageSource? DesktopWallpaper()
	{
		if (_wallpaperChecked)
			return _wallpaper;

		_wallpaperChecked = true;

		try
		{
			string? path = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;

			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return null;

			var image = new System.Windows.Media.Imaging.BitmapImage();
			image.BeginInit();
			image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
			image.DecodePixelWidth = 1280;
			image.UriSource = new Uri(path);
			image.EndInit();
			image.Freeze();

			_wallpaper = image;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow::DesktopWallpaper", ex.Message);
		}

		return _wallpaper;
	}

	private static double BlurFor(Voidstrap.Models.BackdropType backdrop)
	{
		return backdrop switch
		{
			Voidstrap.Models.BackdropType.Mica => 100,
			Voidstrap.Models.BackdropType.MicaAlt => 110,
			Voidstrap.Models.BackdropType.Acrylic => 42,
			Voidstrap.Models.BackdropType.Aero => 42,
			_ => 60
		};
	}

	private static UIElement BuildBackdropLayer(Voidstrap.Models.BackdropType backdrop)
	{
		var blur = new System.Windows.Media.Effects.BlurEffect
		{
			Radius = BlurFor(backdrop),
			KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
			RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
		};

		System.Windows.Media.ImageSource? wallpaper = DesktopWallpaper();

		if (wallpaper != null)
		{
			return new System.Windows.Controls.Image
			{
				Source = wallpaper,
				Stretch = System.Windows.Media.Stretch.UniformToFill,
				Effect = blur
			};
		}

		var fallback = new System.Windows.Media.LinearGradientBrush
		{
			StartPoint = new Point(0, 0),
			EndPoint = new Point(1, 1)
		};

		fallback.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x2A, 0x3B, 0x55), 0));
		fallback.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x18, 0x22), 1));

		return new System.Windows.Controls.Border { Background = fallback, Effect = blur };
	}

	private static CornerRadius CornerFor(Voidstrap.UI.Elements.Bootstrapper.CustomDialog dialog)
	{
		return dialog.WindowCornerPreference switch
		{
			Wpf.Ui.Appearance.WindowCornerPreference.DoNotRound => new CornerRadius(0),
			Wpf.Ui.Appearance.WindowCornerPreference.RoundSmall => new CornerRadius(4),
			_ => new CornerRadius(8)
		};
	}

	private void ScalePreview()
	{
		if (PreviewHost.Child is not System.Windows.Controls.Border shell)
			return;

		double available = PreviewColumn.ActualWidth - 60;
		if (available <= 0 || shell.Width <= 0)
			return;

		double scale = Math.Min(1.0, available / shell.Width);
		shell.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
	}

	private void ReleasePreviewDialog()
	{
		if (_previewDialog == null)
			return;

		try { _previewDialog.Close(); } catch { }

		_previewDialog = null;
	}

	private void ShowPreviewOk(double width, double height)
	{
		_previewErrorLine = 0;
		PreviewStatus.Background = (System.Windows.Media.Brush)FindResource("ControlFillColorDefaultBrush");
		PreviewStatusIcon.Symbol = SymbolRegular.CheckmarkCircle24;
		PreviewStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorSuccessBrush");
		PreviewStatusTitle.Text = "Builds cleanly";
		PreviewStatusDetail.Text = Math.Round(width) + " x " + Math.Round(height);
		PreviewStatusDetail.Visibility = Visibility.Visible;
		PreviewStatusHint.Visibility = Visibility.Collapsed;
		PreviewGoTo.Visibility = Visibility.Collapsed;
	}

	private void ShowPreviewProblem(Exception ex)
	{
		PreviewHost.Child = null;
		PreviewPlaceholder.Visibility = Visibility.Visible;
		PreviewPlaceholder.Text = "Could not build";

		string message = ex.Message;
		Exception? inner = ex.InnerException;

		while (inner != null)
		{
			if (!string.IsNullOrWhiteSpace(inner.Message) && !message.Contains(inner.Message, StringComparison.Ordinal))
				message += "\n" + inner.Message;
			inner = inner.InnerException;
		}

		_previewErrorLine = ExtractLine(ex);

		PreviewStatus.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xE8, 0x11, 0x23));
		PreviewStatusIcon.Symbol = SymbolRegular.ErrorCircle24;
		PreviewStatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorCriticalBrush");
		PreviewStatusTitle.Text = _previewErrorLine > 0 ? "Line " + _previewErrorLine : "Build failed";
		PreviewStatusDetail.Text = message;
		PreviewStatusDetail.Visibility = Visibility.Visible;

		string hint = HintFor(message);
		PreviewStatusHint.Text = hint;
		PreviewStatusHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
		PreviewGoTo.Content = "Go to line " + _previewErrorLine;
		PreviewGoTo.Visibility = _previewErrorLine > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private static int ExtractLine(Exception ex)
	{
		Exception? current = ex;

		while (current != null)
		{
			if (current is System.Xml.XmlException xml && xml.LineNumber > 0)
				return xml.LineNumber;

			current = current.InnerException;
		}

		var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"line (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		return match.Success && int.TryParse(match.Groups[1].Value, out int line) ? line : 0;
	}

	private static string HintFor(string message)
	{
		string lower = message.ToLowerInvariant();

		if (lower.Contains("doctype") || lower.Contains("unexpectedtoken"))
			return "This does not look like theme XML. Check that you are editing Theme.xml and not an HTML file.";

		if (lower.Contains("root"))
			return "The outer tag has to be VoidstrapCustomBootstrapper.";

		if (lower.Contains("version"))
			return "Add Version=\"2\" to the outer tag.";

		if (lower.Contains("not allowed") || lower.Contains("unknown"))
			return "That tag or attribute is not part of the theme format. Autocomplete lists everything you can use.";

		if (lower.Contains("uri") || lower.Contains("source") || lower.Contains("theme://"))
			return "Check the theme:// path. It has to match a file in this theme, and it is case sensitive.";

		if (lower.Contains("parse") || lower.Contains("xml"))
			return "Something is malformed. A missing quote or an unclosed tag is the usual cause.";

		return "";
	}

	private void PreviewGoTo_Click(object sender, RoutedEventArgs e)
	{
		if (_previewErrorLine <= 0)
			return;

		if (_activeFile != null && !_activeFile.IsRoot)
			SelectFile("Theme.xml");

		try
		{
			int line = Math.Min(_previewErrorLine, UIXML.Document.LineCount);
			var docLine = UIXML.Document.GetLineByNumber(line);
			UIXML.Select(docLine.Offset, docLine.Length);
			UIXML.ScrollToLine(line);
			UIXML.Focus();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow::PreviewGoTo", ex.Message);
		}
	}

	#endregion

	#region Theme files

	public class ThemeFile
	{
		public string RelativePath { get; set; } = "";

		public string FullPath { get; set; } = "";

		public string Display { get; set; } = "";

		public string SizeText { get; set; } = "";

		public SymbolRegular Icon { get; set; } = SymbolRegular.Document24;

		public bool IsText { get; set; }

		public bool IsImage { get; set; }

		public bool IsRoot { get; set; }
	}

	private static readonly string[] TextFileExtensions = { ".xml", ".html", ".htm", ".css", ".js", ".json", ".txt", ".md", ".svg" };

	private static readonly string[] ImageFileExtensions = { ".png", ".apng", ".jpg", ".jpeg", ".jfif", ".jpe", ".jff", ".webp", ".gif", ".bmp", ".dib", ".ico", ".cur" };

	private static readonly string[] MediaFileExtensions = { ".mp4", ".webm", ".mov", ".mp3", ".wav", ".ogg", ".m4a", ".flac" };

	private readonly System.Collections.ObjectModel.ObservableCollection<ThemeFile> _files = new();

	private FileSystemWatcher? _themeWatcher;

	private int _themeRefreshPending;

	private ThemeFile? _activeFile;

	private bool _suppressFileSelection;

	private static bool HasExtension(string path, string[] set)
	{
		return set.Contains(Path.GetExtension(path).ToLowerInvariant());
	}

	private static string HumanSize(long bytes)
	{
		if (bytes < 1024)
			return bytes + " B";
		if (bytes < 1024 * 1024)
			return Math.Round(bytes / 1024.0) + " KB";
		return Math.Round(bytes / (1024.0 * 1024.0), 1) + " MB";
	}

	private void InitialiseFiles()
	{
		FileList.ItemsSource = _files;
		RefreshFiles();
		SelectFile("Theme.xml");
		StartThemeWatch();
	}

	private void StartThemeWatch()
	{
		StopThemeWatch();

		try
		{
			Directory.CreateDirectory(_viewModel.Directory);
			_themeWatcher = new FileSystemWatcher(_viewModel.Directory)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
			};
			_themeWatcher.Changed += OnThemeFileChanged;
			_themeWatcher.Created += OnThemeFileChanged;
			_themeWatcher.Deleted += OnThemeFileChanged;
			_themeWatcher.Renamed += OnThemeFileRenamed;
			_themeWatcher.EnableRaisingEvents = true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow::StartThemeWatch", ex.Message);
			StopThemeWatch();
		}
	}

	private void StopThemeWatch()
	{
		FileSystemWatcher? watcher = _themeWatcher;
		_themeWatcher = null;

		if (watcher == null)
			return;

		watcher.EnableRaisingEvents = false;
		watcher.Changed -= OnThemeFileChanged;
		watcher.Created -= OnThemeFileChanged;
		watcher.Deleted -= OnThemeFileChanged;
		watcher.Renamed -= OnThemeFileRenamed;
		watcher.Dispose();
	}

	private void OnThemeFileChanged(object sender, FileSystemEventArgs e)
	{
		QueueThemeRefresh();
	}

	private void OnThemeFileRenamed(object sender, RenamedEventArgs e)
	{
		QueueThemeRefresh();
	}

	private void QueueThemeRefresh()
	{
		if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _themeRefreshPending, 1) != 0)
			return;

		Dispatcher.BeginInvoke(new Action(ApplyThemeFileChange));
	}

	private void ApplyThemeFileChange()
	{
		Interlocked.Exchange(ref _themeRefreshPending, 0);
		RefreshFiles();
		_renderedPreviewXml = null;
		QueuePreview();
	}

	private void RefreshFiles()
	{
		string previous = _activeFile?.RelativePath ?? "Theme.xml";
		bool wasSuppressed = _suppressFileSelection;
		_suppressFileSelection = true;
		_files.Clear();

		try
		{
			if (!Directory.Exists(_viewModel.Directory))
				Directory.CreateDirectory(_viewModel.Directory);

			var entries = Directory
				.EnumerateFiles(_viewModel.Directory, "*.*", SearchOption.AllDirectories)
				.Select(path => Path.GetRelativePath(_viewModel.Directory, path).Replace(Path.DirectorySeparatorChar, '/'))
				.Where(rel => !rel.StartsWith(".", StringComparison.Ordinal))
				.OrderBy(rel => rel.Equals("Theme.xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
				.ThenBy(rel => rel.Contains('/') ? 1 : 0)
				.ThenBy(rel => rel, StringComparer.OrdinalIgnoreCase);

			foreach (string relative in entries)
			{
				string full = Path.Combine(_viewModel.Directory, relative.Replace('/', Path.DirectorySeparatorChar));
				long size = 0;

				try { size = new FileInfo(full).Length; } catch { }

			bool isText = HasExtension(relative, TextFileExtensions);
			bool isImage = HasExtension(relative, ImageFileExtensions);
			bool isMedia = HasExtension(relative, MediaFileExtensions);

				_files.Add(new ThemeFile
				{
					RelativePath = relative,
					FullPath = full,
					Display = relative,
					SizeText = HumanSize(size),
					IsText = isText,
					IsImage = isImage,
					IsRoot = relative.Equals("Theme.xml", StringComparison.OrdinalIgnoreCase),
				Icon = relative.Equals("Theme.xml", StringComparison.OrdinalIgnoreCase)
					? SymbolRegular.Code24
					: isImage
						? SymbolRegular.Image24
						: isMedia
							? SymbolRegular.Video24
							: isText
								? SymbolRegular.DocumentText24
								: SymbolRegular.Document24
				});
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow::RefreshFiles", ex.Message);
		}
		finally
		{
			_suppressFileSelection = wasSuppressed;
		}

		ThemeFile? same = _activeFile == null
			? null
			: _files.FirstOrDefault(f => f.RelativePath.Equals(_activeFile.RelativePath, StringComparison.OrdinalIgnoreCase));

		if (same != null)
		{
			_activeFile = same;
			SyncSelectionToActive();
			return;
		}

		SelectFile(previous);
	}

	private void SelectFile(string relativePath)
	{
		ThemeFile? match = _files.FirstOrDefault(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
			?? _files.FirstOrDefault();

		_suppressFileSelection = true;
		FileList.SelectedItem = match;
		_suppressFileSelection = false;

		OpenFile(match);
	}

	private void FileList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
	{
		if (_suppressFileSelection)
			return;

		OpenFile(FileList.SelectedItem as ThemeFile);
	}

	private bool _switchingFile;

	private void SyncSelectionToActive()
	{
		_suppressFileSelection = true;

		try
		{
			FileList.SelectedItem = _activeFile != null
				? _files.FirstOrDefault(f => f.RelativePath.Equals(_activeFile.RelativePath, StringComparison.OrdinalIgnoreCase))
				: null;
		}
		finally
		{
			_suppressFileSelection = false;
		}
	}

	private void OpenFile(ThemeFile? file)
	{
		if (file == null || _switchingFile)
			return;

		if (_activeFile != null && file.RelativePath.Equals(_activeFile.RelativePath, StringComparison.OrdinalIgnoreCase))
		{
			_activeFile = file;
			return;
		}

		_switchingFile = true;

		try
		{
			if (!SaveActiveFile(silent: true))
			{
				SyncSelectionToActive();
				return;
			}

			if (file.IsImage)
			{
				_activeFile = file;
				ShowMedia(file);
				return;
			}

			if (!file.IsText)
			{
				_activeFile = file;
				ShowUnsupported(file);
				return;
			}

			string text;

			try
			{
				text = ToCRLF(File.ReadAllText(file.FullPath));
			}
			catch (Exception ex)
			{
				ThemeSavedCallback(false, "Could not open " + file.RelativePath + ": " + ex.Message);
				SyncSelectionToActive();
				return;
			}

			_activeFile = file;

			MediaPanel.Visibility = Visibility.Collapsed;
			UIXML.Visibility = Visibility.Visible;
			EditorFileLabel.Text = file.RelativePath;
			EditorDirtyLabel.Text = "";

			CodeEditorBehavior.SetLanguage(UIXML, Path.GetExtension(file.RelativePath).ToLowerInvariant());

			UIXML.TextChanged -= OnCodeChanged;
			UIXML.Text = text;
			UIXML.TextChanged += OnCodeChanged;
			UIXML.CaretOffset = 0;
			UIXML.ScrollToHome();
		}
		finally
		{
			_switchingFile = false;
		}
	}

	private void ShowMedia(ThemeFile file)
	{
		UIXML.Visibility = Visibility.Collapsed;
		EditorFileLabel.Text = file.RelativePath;
		EditorDirtyLabel.Text = "";
		MediaPanel.Visibility = Visibility.Visible;

		try
		{
			var image = new System.Windows.Media.Imaging.BitmapImage();
			image.BeginInit();
			image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
			image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
			image.UriSource = new Uri(file.FullPath);
			image.EndInit();
			image.Freeze();

			MediaImage.Source = image;
			MediaInfo.Text = file.RelativePath + "\n" + image.PixelWidth + " by " + image.PixelHeight + ", " + file.SizeText
				+ "\n\nUse theme://" + file.RelativePath + " to reference it.";
		}
		catch (Exception ex)
		{
			MediaImage.Source = null;
			MediaInfo.Text = file.RelativePath + "\n\nThis image could not be shown: " + ex.Message;
		}

	}

	private void ShowUnsupported(ThemeFile file)
	{
		UIXML.Visibility = Visibility.Collapsed;
		EditorFileLabel.Text = file.RelativePath;
		EditorDirtyLabel.Text = "";
		MediaPanel.Visibility = Visibility.Visible;
		MediaImage.Source = null;
		MediaInfo.Text = file.RelativePath + "\n" + file.SizeText
			+ "\n\nThis file cannot be edited here. Use Replace to swap it for another file."
			+ "\n\nUse theme://" + file.RelativePath + " to reference it.";
	}

	private bool SaveActiveFile(bool silent = false)
	{
		if (_activeFile == null || !_activeFile.IsText || !_viewModel.CodeChanged)
			return true;

		if (_activeFile.IsRoot && !BootstrapperEditorWindowViewModel.LooksLikeThemeXml(UIXML.Text))
		{
			ThemeSavedCallback(false, "That content is not a theme file, so Theme.xml was left alone.");
			return false;
		}

		try
		{
			File.WriteAllText(_activeFile.FullPath, UIXML.Text);
			_viewModel.CodeChanged = false;

			if (_activeFile.IsRoot)
				_viewModel.Code = UIXML.Text;

			EditorDirtyLabel.Text = "";

			if (!silent)
				ThemeSavedCallback(true, "Saved " + _activeFile.RelativePath);

			return true;
		}
		catch (Exception ex)
		{
			ThemeSavedCallback(false, "Could not save " + _activeFile.RelativePath + ": " + ex.Message);
			return false;
		}
	}

	private void SaveActive_Click(object sender, RoutedEventArgs e)
	{
		if (_activeFile == null || !_activeFile.IsText)
		{
			ThemeSavedCallback(true, "There is nothing to save for this file.");
			return;
		}

		_viewModel.CodeChanged = true;
		SaveActiveFile();
		RefreshFiles();
	}

	private void RefreshFiles_Click(object sender, RoutedEventArgs e) => RefreshFiles();

	private string? AskForName(string title, string initial)
	{
		var dialog = new Voidstrap.UI.Elements.Dialogs.TextInputDialog(title, initial);
		dialog.Owner = this;
		dialog.ShowDialog();
		return dialog.Confirmed ? dialog.Value : null;
	}

	private static bool IsSafeRelativePath(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;
		if (value.Contains("..", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(value))
			return false;
		return value.IndexOfAny(Path.GetInvalidPathChars()) == -1;
	}

	private void NewFile_Click(object sender, RoutedEventArgs e)
	{
		string? name = AskForName("Name the new file, for example panel.html", "panel.html");
		if (name == null)
			return;

		name = name.Trim().Replace('\\', '/');

		if (!IsSafeRelativePath(name))
		{
			ThemeSavedCallback(false, "That file name cannot be used.");
			return;
		}

		string target = Path.Combine(_viewModel.Directory, name.Replace('/', Path.DirectorySeparatorChar));

		if (File.Exists(target))
		{
			ThemeSavedCallback(false, name + " already exists.");
			return;
		}

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.WriteAllText(target, StarterContentFor(name));
			RefreshFiles();
			SelectFile(name);
			ThemeSavedCallback(true, name + " has been created.");
		}
		catch (Exception ex)
		{
			ThemeSavedCallback(false, "Could not create " + name + ": " + ex.Message);
		}
	}

	private static string StarterContentFor(string name)
	{
		string extension = Path.GetExtension(name).ToLowerInvariant();

		if (extension == ".html" || extension == ".htm")
			return Resource.GetString("CustomBootstrapperTemplate_Panel.html");

		if (extension == ".css")
			return "html, body {\r\n    margin: 0;\r\n    height: 100%;\r\n    background: transparent;\r\n}\r\n";

		if (extension == ".js")
			return "window.voidstrap.onUpdate(function (state) {\r\n});\r\n";

		return string.Empty;
	}

	private void NewFolder_Click(object sender, RoutedEventArgs e)
	{
		string? name = AskForName("Name the new folder", "Assets");
		if (name == null)
			return;

		name = name.Trim().Replace('\\', '/');

		if (!IsSafeRelativePath(name))
		{
			ThemeSavedCallback(false, "That folder name cannot be used.");
			return;
		}

		try
		{
			Directory.CreateDirectory(Path.Combine(_viewModel.Directory, name.Replace('/', Path.DirectorySeparatorChar)));
			ThemeSavedCallback(true, name + " has been created. Add a file to it to see it here.");
		}
		catch (Exception ex)
		{
			ThemeSavedCallback(false, "Could not create " + name + ": " + ex.Message);
		}
	}

	private void ImportFile_Click(object sender, RoutedEventArgs e)
	{
		var picker = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Add files to this theme",
			Multiselect = true,
			Filter = "Theme files|*.png;*.jpg;*.jpeg;*.jfif;*.webp;*.gif;*.bmp;*.ico;*.ttf;*.otf;*.ttc;*.html;*.htm;*.css;*.js;*.mp4;*.webm;*.mov;*.mp3;*.wav;*.ogg;*.m4a;*.flac|All files|*.*"
		};

		if (picker.ShowDialog(this) != true)
			return;

		int added = 0;

		foreach (string source in picker.FileNames)
		{
			try
			{
				string target = Path.Combine(_viewModel.Directory, Path.GetFileName(source));
				int suffix = 2;

				while (File.Exists(target))
				{
					target = Path.Combine(_viewModel.Directory,
						Path.GetFileNameWithoutExtension(source) + " " + suffix + Path.GetExtension(source));
					suffix++;
				}

				File.Copy(source, target);
				added++;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("BootstrapperEditorWindow::ImportFile", ex.Message);
			}
		}

		RefreshFiles();
		ThemeSavedCallback(added > 0, added > 0 ? added + " file(s) added." : "Nothing was added.");
	}

	private ThemeFile? SelectedFile()
	{
		return FileList.SelectedItem as ThemeFile ?? _activeFile;
	}

	private void RenameFile_Click(object sender, RoutedEventArgs e)
	{
		ThemeFile? file = SelectedFile();
		if (file == null)
			return;

		if (file.IsRoot)
		{
			ThemeSavedCallback(false, "Theme.xml cannot be renamed.");
			return;
		}

		string? name = AskForName("Rename " + file.RelativePath, file.RelativePath);
		if (name == null)
			return;

		name = name.Trim().Replace('\\', '/');

		if (!IsSafeRelativePath(name))
		{
			ThemeSavedCallback(false, "That file name cannot be used.");
			return;
		}

		try
		{
			string target = Path.Combine(_viewModel.Directory, name.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			File.Move(file.FullPath, target);
			_activeFile = null;
			RefreshFiles();
			SelectFile(name);
			ThemeSavedCallback(true, "Renamed to " + name + ". Remember to update theme:// references.");
		}
		catch (Exception ex)
		{
			ThemeSavedCallback(false, "Could not rename: " + ex.Message);
		}
	}

	private void ReplaceFile_Click(object sender, RoutedEventArgs e)
	{
		ThemeFile? file = SelectedFile();
		if (file == null || file.IsRoot)
			return;

		string extension = Path.GetExtension(file.RelativePath);

		var picker = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Replace " + file.RelativePath,
			Filter = file.IsImage
				? "Images|*.png;*.jpg;*.jpeg;*.jfif;*.webp;*.gif;*.bmp;*.ico|All files|*.*"
				: "Matching files|*" + extension + "|All files|*.*"
		};

		if (picker.ShowDialog(this) != true)
			return;

		try
		{
			File.Copy(picker.FileName, file.FullPath, overwrite: true);
			_activeFile = null;
			RefreshFiles();
			SelectFile(file.RelativePath);
			ThemeSavedCallback(true, file.RelativePath + " has been replaced.");
		}
		catch (Exception ex)
		{
			ThemeSavedCallback(false, "Could not replace: " + ex.Message);
		}
	}

	private void DuplicateFile_Click(object sender, RoutedEventArgs e)
	{
		ThemeFile? file = SelectedFile();
		if (file == null)
			return;

		try
		{
			string directory = Path.GetDirectoryName(file.FullPath)!;
			string stem = Path.GetFileNameWithoutExtension(file.FullPath);
			string extension = Path.GetExtension(file.FullPath);
			string target = Path.Combine(directory, stem + " copy" + extension);
			int suffix = 2;

			while (File.Exists(target))
			{
				target = Path.Combine(directory, stem + " copy " + suffix + extension);
				suffix++;
			}

			File.Copy(file.FullPath, target);
			RefreshFiles();
			ThemeSavedCallback(true, "Copied to " + Path.GetFileName(target) + ".");
		}
		catch (Exception ex)
		{
			ThemeSavedCallback(false, "Could not duplicate: " + ex.Message);
		}
	}

	private void DeleteFile_Click(object sender, RoutedEventArgs e)
	{
		ThemeFile? file = SelectedFile();
		if (file == null)
			return;

		if (file.IsRoot)
		{
			ThemeSavedCallback(false, "Theme.xml cannot be deleted.");
			return;
		}

		if (Frontend.ShowMessageBox("Delete " + file.RelativePath + " from this theme?", MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
			return;

		try
		{
			File.Delete(file.FullPath);
			_activeFile = null;
			RefreshFiles();
			SelectFile("Theme.xml");
			ThemeSavedCallback(true, file.RelativePath + " has been deleted.");
		}
		catch (Exception ex)
		{
			ThemeSavedCallback(false, "Could not delete: " + ex.Message);
		}
	}

	private void CopyThemePath_Click(object sender, RoutedEventArgs e)
	{
		ThemeFile? file = SelectedFile();
		if (file == null)
			return;

		try { System.Windows.Clipboard.SetText("theme://" + file.RelativePath); } catch { }

		ThemeSavedCallback(true, "Copied theme://" + file.RelativePath);
	}

	private void ShowInFolder_Click(object sender, RoutedEventArgs e)
	{
		ThemeFile? file = SelectedFile();
		if (file == null)
			return;

		try
		{
			using Process? process = Process.Start("explorer.exe", "/select,\"" + file.FullPath + "\"");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow::ShowInFolder", ex.Message);
		}
	}

	#endregion

	private ExternalEditorInfo? _external;

	private FileSystemWatcher? _externalWatcher;

	private DispatcherTimer? _externalReload;

	private void OpenExternal_Click(object sender, RoutedEventArgs e)
	{
		IReadOnlyList<ExternalEditorInfo> editors = ExternalEditor.Detect();

		if (editors.Count == 0)
		{
			ShowExternalStatus("No other editors were found on this PC.");
			return;
		}

		ExternalEditorPickerDialog dialog = new(editors) { Owner = this };

		if (dialog.ShowDialog() != true || dialog.SelectedEditor == null)
			return;

		_external = dialog.SelectedEditor;
		LaunchExternal(_external);
	}

	private string? _externalPath;

	private void LaunchExternal(ExternalEditorInfo editor)
	{
		ThemeFile? file = _activeFile;

		if (file == null || !file.IsText)
		{
			ShowExternalStatus("Open a text file first.");
			return;
		}

		if (!SaveActiveFile(silent: true))
		{
			ShowExternalStatus("Fix the problems in this file before opening it elsewhere.");
			return;
		}

		try
		{
			Directory.CreateDirectory(_viewModel.Directory);

			if (!File.Exists(file.FullPath))
				File.WriteAllText(file.FullPath, UIXML.Text);
		}
		catch (Exception ex)
		{
			ShowExternalStatus("Could not write " + file.RelativePath + ": " + ex.Message);
			return;
		}

		if (!ExternalEditor.Open(editor, file.FullPath))
		{
			ShowExternalStatus("Could not start " + editor.Name);
			return;
		}

		StartExternalWatch(file.FullPath);
		ShowExternalStatus("Editing " + file.RelativePath + " in " + editor.Name + ", changes appear here as you save");
	}

	private void StartExternalWatch(string themePath)
	{
		StopExternalWatch();

		string? folder = Path.GetDirectoryName(themePath);

		if (string.IsNullOrEmpty(folder))
			return;

		_externalPath = themePath;

		_externalWatcher = new FileSystemWatcher(folder, Path.GetFileName(themePath))
		{
			NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
			EnableRaisingEvents = true
		};

		_externalWatcher.Changed += OnExternalFileChanged;
		_externalWatcher.Created += OnExternalFileChanged;
	}

	private void StopExternalWatch()
	{
		if (_externalWatcher != null)
		{
			try
			{
				_externalWatcher.EnableRaisingEvents = false;
				_externalWatcher.Changed -= OnExternalFileChanged;
				_externalWatcher.Created -= OnExternalFileChanged;
				_externalWatcher.Dispose();
			}
			catch
			{
			}

			_externalWatcher = null;
		}

		_externalPath = null;

		if (_externalReload != null)
		{
			_externalReload.Stop();
			_externalReload.Tick -= OnExternalReloadTick;
			_externalReload = null;
		}
	}

	private void OnExternalFileChanged(object sender, FileSystemEventArgs e)
	{
		Dispatcher.BeginInvoke(new Action(delegate
		{
			if (_externalReload == null)
			{
				_externalReload = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
				_externalReload.Tick += OnExternalReloadTick;
			}

			_externalReload.Stop();
			_externalReload.Start();
		}));
	}

	private void OnExternalReloadTick(object? sender, EventArgs e)
	{
		_externalReload?.Stop();

		string? themePath = _externalPath;

		if (string.IsNullOrEmpty(themePath))
			return;

		if (_activeFile == null || !_activeFile.FullPath.Equals(themePath, StringComparison.OrdinalIgnoreCase))
			return;

		try
		{
			string text = ToCRLF(File.ReadAllText(themePath));

			if (text == UIXML.Text)
				return;

			int caret = UIXML.CaretOffset;
			UIXML.Text = text;
			UIXML.CaretOffset = Math.Min(caret, text.Length);

			ShowExternalStatus("Reloaded from " + (_external?.Name ?? "the other editor"));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow", "Could not reload the theme: " + ex.Message);
		}
	}

	private void ShowExternalStatus(string message)
	{
		try
		{
			Snackbar.Show("Open Editor", message);
		}
		catch
		{
			App.Logger?.WriteLine("BootstrapperEditorWindow", message);
		}
	}
}
