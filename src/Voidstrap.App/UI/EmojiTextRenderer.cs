using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Emoji.Wpf;

namespace Voidstrap.UI;

internal static class EmojiTextRenderer
{
    private static readonly DependencyProperty ProcessingProperty = DependencyProperty.RegisterAttached("EmojiTextRendererProcessing", typeof(bool), typeof(EmojiTextRenderer), new PropertyMetadata(false));

    private static readonly DependencyProperty QueuedProperty = DependencyProperty.RegisterAttached("EmojiTextRendererQueued", typeof(bool), typeof(EmojiTextRenderer), new PropertyMetadata(false));

    private static readonly DependencyProperty SourceTextProperty = DependencyProperty.RegisterAttached("EmojiTextRendererSourceText", typeof(string), typeof(EmojiTextRenderer), new PropertyMetadata("", OnSourceTextChanged));

    private static readonly HashSet<System.Windows.Controls.TextBlock> TrackedTextBlocks = new();

    private static readonly HashSet<System.Windows.Controls.RichTextBox> TrackedRichTextBoxes = new();

    private static readonly Dictionary<System.Windows.Controls.TextBlock, HashSet<Run>> TrackedRuns = new();

    private static readonly Dictionary<Run, System.Windows.Controls.TextBlock> RunOwners = new();

    private static readonly Dictionary<System.Windows.Controls.TextBlock, BindingBase> MovedTextBindings = new();

    private static readonly Dictionary<System.Windows.Controls.TextBlock, string> RenderedBindingText = new();

    private static readonly HashSet<FlowDocument> TrackedDocuments = new();

    private static readonly HashSet<Window> TrackedWindows = new();

    private static readonly HashSet<Window> QueuedWindows = new();

    private static DependencyPropertyDescriptor? _textDescriptor;

    private static DependencyPropertyDescriptor? _runTextDescriptor;

    private static bool _installed;

    private static bool _classHandlersRegistered;

    public static void Install()
    {
        if (_installed || !Voidstrap.Utility.Platform.IsWindows)
            return;
        EmojiData.EnableSubPixelRendering = true;
        _ = EmojiData.MatchStart;
        _textDescriptor = DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.TextBlock.TextProperty, typeof(System.Windows.Controls.TextBlock));
        _runTextDescriptor = DependencyPropertyDescriptor.FromProperty(Run.TextProperty, typeof(Run));
        _installed = true;
        if (!_classHandlersRegistered)
        {
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnFrameworkElementLoaded), true);
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnFrameworkElementUnloaded), true);
            _classHandlersRegistered = true;
        }
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(RefreshAll), DispatcherPriority.ContextIdle);
    }

    public static void Shutdown()
    {
        if (!_installed)
            return;
        foreach (System.Windows.Controls.RichTextBox richTextBox in TrackedRichTextBoxes.ToList())
            UntrackRichTextBox(richTextBox);
        foreach (System.Windows.Controls.TextBlock textBlock in TrackedTextBlocks.ToList())
            UntrackTextBlock(textBlock);
        foreach (Window window in TrackedWindows.ToList())
            UntrackWindow(window);
        TrackedDocuments.Clear();
        RunOwners.Clear();
        _installed = false;
    }

    public static void Refresh(System.Windows.Controls.TextBlock textBlock)
    {
        if (!_installed || textBlock is Emoji.Wpf.TextBlock)
            return;
        TrackTextBlock(textBlock);
        QueueTextBlock(textBlock);
    }

    private static void OnFrameworkElementLoaded(object sender, RoutedEventArgs e)
    {
        if (!_installed || sender is not DependencyObject node)
            return;
        if (sender is Window window)
        {
            if (TrackedWindows.Add(window))
            {
                window.ContentRendered += OnWindowContentRendered;
                window.Closed += OnWindowClosed;
            }
            QueueWindow(window);
        }
        RefreshNode(node);
    }

    private static void OnFrameworkElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RichTextBox richTextBox)
        {
            TrackedDocuments.Remove(richTextBox.Document);
            UntrackRichTextBox(richTextBox);
        }
        if (sender is System.Windows.Controls.TextBlock textBlock)
            UntrackTextBlock(textBlock);
        if (sender is FlowDocumentScrollViewer scrollViewer && scrollViewer.Document != null)
            TrackedDocuments.Remove(scrollViewer.Document);
        if (sender is FlowDocumentPageViewer pageViewer && pageViewer.Document is FlowDocument document)
            TrackedDocuments.Remove(document);
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        UntrackTree(window);
        UntrackWindow(window);
    }

    private static void OnWindowContentRendered(object? sender, EventArgs e)
    {
        if (sender is Window window)
            QueueWindow(window);
    }

    private static void QueueWindow(Window window)
    {
        if (!_installed || !QueuedWindows.Add(window) || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
            return;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            QueuedWindows.Remove(window);
            if (_installed && TrackedWindows.Contains(window))
                RefreshTree(window);
        }), DispatcherPriority.ContextIdle);
    }

    private static void UntrackWindow(Window window)
    {
        if (!TrackedWindows.Remove(window))
            return;
        QueuedWindows.Remove(window);
        window.ContentRendered -= OnWindowContentRendered;
        window.Closed -= OnWindowClosed;
    }

    private static void RefreshAll()
    {
        if (!_installed)
            return;
        HashSet<DependencyObject> roots = new();
        if (Application.Current != null)
        {
            foreach (Window window in Application.Current.Windows)
                roots.Add(window);
        }
        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            if (source.RootVisual is DependencyObject root)
                roots.Add(root);
        }
        foreach (DependencyObject root in roots)
            RefreshTree(root);
    }

    private static void RefreshTree(DependencyObject root)
    {
        Stack<DependencyObject> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (IsIconNode(current))
                continue;
            RefreshNode(current);
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                count = 0;
            }
            for (int i = count - 1; i >= 0; i--)
            {
                try
                {
                    pending.Push(VisualTreeHelper.GetChild(current, i));
                }
                catch
                {
                }
            }
        }
    }

    private static void UntrackTree(DependencyObject root)
    {
        Stack<DependencyObject> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (current is System.Windows.Controls.RichTextBox richTextBox)
            {
                TrackedDocuments.Remove(richTextBox.Document);
                UntrackRichTextBox(richTextBox);
            }
            if (current is System.Windows.Controls.TextBlock textBlock)
                UntrackTextBlock(textBlock);
            if (current is FlowDocumentScrollViewer scrollViewer && scrollViewer.Document != null)
                TrackedDocuments.Remove(scrollViewer.Document);
            if (current is FlowDocumentPageViewer pageViewer && pageViewer.Document is FlowDocument document)
                TrackedDocuments.Remove(document);
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(current);
            }
            catch
            {
                count = 0;
            }
            for (int index = count - 1; index >= 0; index--)
            {
                try
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
                catch
                {
                }
            }
        }
    }

    private static void RefreshNode(DependencyObject current)
    {
        if (IsIconNode(current))
            return;
        if (current is System.Windows.Controls.TextBlock textBlock && textBlock is not Emoji.Wpf.TextBlock)
        {
            TrackTextBlock(textBlock);
            QueueTextBlock(textBlock);
        }
        if (current is System.Windows.Controls.RichTextBox richTextBox && richTextBox is not Emoji.Wpf.RichTextBox)
        {
            TrackRichTextBox(richTextBox);
            TrackedDocuments.Add(richTextBox.Document);
            QueueFlowDocument(richTextBox.Document);
        }
        if (current is FlowDocumentScrollViewer scrollViewer && scrollViewer.Document != null)
        {
            TrackedDocuments.Add(scrollViewer.Document);
            QueueFlowDocument(scrollViewer.Document);
        }
        if (current is FlowDocumentPageViewer pageViewer && pageViewer.Document is FlowDocument document)
        {
            TrackedDocuments.Add(document);
            QueueFlowDocument(document);
        }
    }

    private static bool TrackTextBlock(System.Windows.Controls.TextBlock textBlock)
    {
        if (!TrackedTextBlocks.Add(textBlock))
            return false;
        _textDescriptor?.AddValueChanged(textBlock, OnTextBlockTextChanged);
        TrackedRuns[textBlock] = new HashSet<Run>();
        MoveTextBindingIfNeeded(textBlock);
        return true;
    }

    private static bool MoveTextBindingIfNeeded(System.Windows.Controls.TextBlock textBlock)
    {
        if (MovedTextBindings.ContainsKey(textBlock))
            return true;
        BindingBase? binding = BindingOperations.GetBindingBase(textBlock, System.Windows.Controls.TextBlock.TextProperty);
        if (binding == null || !ContainsEmoji(textBlock.Text))
            return false;
        BindingOperations.ClearBinding(textBlock, System.Windows.Controls.TextBlock.TextProperty);
        try
        {
            BindingOperations.SetBinding(textBlock, SourceTextProperty, binding);
            MovedTextBindings[textBlock] = binding;
            return true;
        }
        catch
        {
            BindingOperations.SetBinding(textBlock, System.Windows.Controls.TextBlock.TextProperty, binding);
            return false;
        }
    }

    private static void UntrackTextBlock(System.Windows.Controls.TextBlock textBlock)
    {
        if (!TrackedTextBlocks.Remove(textBlock))
            return;
        _textDescriptor?.RemoveValueChanged(textBlock, OnTextBlockTextChanged);
        if (TrackedRuns.Remove(textBlock, out HashSet<Run>? runs) && _runTextDescriptor != null)
        {
            foreach (Run run in runs)
            {
                _runTextDescriptor.RemoveValueChanged(run, OnRunTextChanged);
                RunOwners.Remove(run);
            }
        }
        RenderedBindingText.Remove(textBlock);
        if (MovedTextBindings.Remove(textBlock, out BindingBase? binding))
        {
            BindingOperations.ClearBinding(textBlock, SourceTextProperty);
            textBlock.Inlines.Clear();
            BindingOperations.SetBinding(textBlock, System.Windows.Controls.TextBlock.TextProperty, binding);
        }
    }

    private static bool TrackRichTextBox(System.Windows.Controls.RichTextBox richTextBox)
    {
        if (!TrackedRichTextBoxes.Add(richTextBox))
            return false;
        richTextBox.TextChanged += OnRichTextBoxTextChanged;
        return true;
    }

    private static void UntrackRichTextBox(System.Windows.Controls.RichTextBox richTextBox)
    {
        if (!TrackedRichTextBoxes.Remove(richTextBox))
            return;
        richTextBox.TextChanged -= OnRichTextBoxTextChanged;
    }

    private static void OnTextBlockTextChanged(object? sender, EventArgs e)
    {
        if (sender is System.Windows.Controls.TextBlock textBlock)
            QueueTextBlock(textBlock);
    }

    private static void OnSourceTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBlock textBlock)
            QueueTextBlock(textBlock);
    }

    private static void OnRunTextChanged(object? sender, EventArgs e)
    {
        if (sender is Run run && RunOwners.TryGetValue(run, out System.Windows.Controls.TextBlock? textBlock))
            QueueTextBlock(textBlock);
    }

    private static void OnRichTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.RichTextBox richTextBox)
            QueueFlowDocument(richTextBox.Document);
    }

    private static void QueueTextBlock(System.Windows.Controls.TextBlock textBlock)
    {
        if (!_installed || textBlock is Emoji.Wpf.TextBlock || (bool)textBlock.GetValue(QueuedProperty) || (bool)textBlock.GetValue(ProcessingProperty) || IsIconNode(textBlock))
            return;
        textBlock.SetValue(QueuedProperty, true);
        if (textBlock.Dispatcher.HasShutdownStarted || textBlock.Dispatcher.HasShutdownFinished)
        {
            textBlock.SetValue(QueuedProperty, false);
            return;
        }
        textBlock.Dispatcher.BeginInvoke(new Action(() =>
        {
            textBlock.SetValue(QueuedProperty, false);
            if (_installed && TrackedTextBlocks.Contains(textBlock))
                ProcessTextBlock(textBlock);
        }), DispatcherPriority.Loaded);
    }

    private static void QueueFlowDocument(FlowDocument document)
    {
        if (!_installed || (bool)document.GetValue(QueuedProperty) || (bool)document.GetValue(ProcessingProperty))
            return;
        document.SetValue(QueuedProperty, true);
        if (document.Dispatcher.HasShutdownStarted || document.Dispatcher.HasShutdownFinished)
        {
            document.SetValue(QueuedProperty, false);
            return;
        }
        document.Dispatcher.BeginInvoke(new Action(() =>
        {
            document.SetValue(QueuedProperty, false);
            if (_installed && TrackedDocuments.Contains(document))
                ProcessFlowDocument(document);
        }), DispatcherPriority.Loaded);
    }

    private static void ProcessTextBlock(System.Windows.Controls.TextBlock textBlock)
    {
        if ((bool)textBlock.GetValue(ProcessingProperty))
            return;
        textBlock.SetValue(ProcessingProperty, true);
        try
        {
            MoveTextBindingIfNeeded(textBlock);
            if (MovedTextBindings.ContainsKey(textBlock))
            {
                string sourceText = textBlock.GetValue(SourceTextProperty) as string ?? "";
                if (RenderedBindingText.TryGetValue(textBlock, out string? rendered) && string.Equals(rendered, sourceText, StringComparison.Ordinal))
                    return;
                ReconcileRuns(textBlock, []);
                textBlock.Inlines.Clear();
                Run sourceRun = new(sourceText);
                textBlock.Inlines.Add(sourceRun);
                if (ContainsEmoji(sourceText))
                    sourceRun.SubstituteGlyphs();
                RenderedBindingText[textBlock] = sourceText;
                ReconcileRuns(textBlock, CollectRuns(textBlock.Inlines).ToList());
                return;
            }
            List<Run> runs = CollectRuns(textBlock.Inlines).ToList();
            ReconcileRuns(textBlock, runs);
            foreach (Run run in runs.Where(run => ContainsEmoji(run.Text)).ToList())
                run.SubstituteGlyphs();
            ReconcileRuns(textBlock, CollectRuns(textBlock.Inlines).ToList());
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("EmojiTextRenderer::TextBlock", ex);
        }
        finally
        {
            textBlock.SetValue(ProcessingProperty, false);
        }
    }

    private static void ReconcileRuns(System.Windows.Controls.TextBlock textBlock, IReadOnlyCollection<Run> currentRuns)
    {
        if (_runTextDescriptor == null || !TrackedRuns.TryGetValue(textBlock, out HashSet<Run>? tracked))
            return;
        foreach (Run removed in tracked.Where(run => !currentRuns.Contains(run)).ToList())
        {
            _runTextDescriptor.RemoveValueChanged(removed, OnRunTextChanged);
            tracked.Remove(removed);
            RunOwners.Remove(removed);
        }
        foreach (Run run in currentRuns)
        {
            if (tracked.Add(run))
            {
                _runTextDescriptor.AddValueChanged(run, OnRunTextChanged);
                RunOwners[run] = textBlock;
            }
        }
    }

    private static void ProcessFlowDocument(FlowDocument document)
    {
        if ((bool)document.GetValue(ProcessingProperty))
            return;
        document.SetValue(ProcessingProperty, true);
        try
        {
            string text = new TextRange(document.ContentStart, document.ContentEnd).Text;
            if (ContainsEmoji(text))
                document.SubstituteGlyphs();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("EmojiTextRenderer::FlowDocument", ex);
        }
        finally
        {
            document.SetValue(ProcessingProperty, false);
        }
    }

    private static bool ContainsEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        for (int i = 0; i < text.Length; i++)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, i);
            if (category == UnicodeCategory.PrivateUse)
                continue;
            if (EmojiData.MatchStart.Contains(text[i]))
                return true;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
        }
        return false;
    }

    private static bool IsIconNode(DependencyObject node)
    {
        if (node is Wpf.Ui.Controls.SymbolIcon or Wpf.Ui.Controls.FontIcon)
            return true;
        if (node.GetValue(TextElement.FontFamilyProperty) is not System.Windows.Media.FontFamily fontFamily)
            return false;
        string source = fontFamily.Source;
        return source.Contains("Icon", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Symbol", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Wingdings", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Webdings", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Run> CollectRuns(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            if (inline is Run run)
            {
                yield return run;
            }
            else if (inline is Span span)
            {
                foreach (Run nested in CollectRuns(span.Inlines))
                    yield return nested;
            }
        }
    }
}
