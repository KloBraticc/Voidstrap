using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static partial class WindowsColorEmoji
{
    private const string LOG_IDENT = "WindowsColorEmoji";
    private const string FontFileName = "seguiemj.ttf";
    private const double OutlineEmSize = 100.0;
    private const ushort ForegroundPaletteIndex = 0xFFFF;
    private const int VariationSelectorText = 0xFE0E;
    private const int VariationSelectorEmoji = 0xFE0F;
    private const int ZeroWidthJoiner = 0x200D;
    private const int KeycapCombiner = 0x20E3;

    private static readonly DependencyPropertyDescriptor? TextDescriptor = DependencyPropertyDescriptor.FromProperty(
        TextBlock.TextProperty,
        typeof(TextBlock)
    );

    private static readonly ConditionalWeakTable<TextBlock, Tracker> Trackers = new();
    private static readonly ConditionalWeakTable<TextBlock, object> Hooked = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<ushort, Layer[]?> LayerCache = new();
    private static readonly ConditionalWeakTable<ushort[], Layer[]?> SequenceCache = new();

    private static bool _installed;
    private static bool _loadAttempted;
    private static GlyphTypeface? _typeface;
    private static Dictionary<ushort, (int First, int Count)>? _baseGlyphs;
    private static (ushort Glyph, ushort Palette)[]? _layers;
    private static Brush?[]? _palette;

    private readonly record struct Layer(Geometry Geometry, Brush? Fill);

    private readonly record struct Emoji(int Offset, int Length, ushort[] Glyphs);

    private sealed class Tracker
    {
        public EmojiAdorner? Adorner;
        public readonly List<(Run Run, object Original)> Hidden = new();
        public bool Pending;
        public bool Hiding;
        public string? Snapshot;
    }

    public static void Install()
    {
        if (_installed || !Voidstrap.Utility.Platform.IsWindows)
            return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnSizeChanged));
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not TextBlock block)
            return;
        if (Trackers.TryGetValue(block, out Tracker? tracker))
        {
            if (!string.Equals(block.Text, tracker.Snapshot, StringComparison.Ordinal))
                QueueApply(block);
            else
                tracker.Adorner?.InvalidateVisual();
            return;
        }
        if (!Hooked.TryGetValue(block, out _))
        {
            Hooked.Add(block, Hooked);
            block.Loaded += OnLoaded;
            block.Unloaded += OnUnloaded;
        }
        Track(block);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock block && !Trackers.TryGetValue(block, out _))
            Track(block);
    }

    private static void Track(TextBlock block)
    {
        Trackers.Add(block, new Tracker());
        TextDescriptor?.AddValueChanged(block, OnTextChanged);
        Apply(block);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock block || !Trackers.TryGetValue(block, out Tracker? tracker))
            return;
        TextDescriptor?.RemoveValueChanged(block, OnTextChanged);
        Clear(block, tracker);
        Trackers.Remove(block);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock block && !(Trackers.TryGetValue(block, out Tracker? tracker) && tracker.Hiding))
            QueueApply(block);
    }

    private static void QueueApply(TextBlock block)
    {
        if (!Trackers.TryGetValue(block, out Tracker? tracker) || tracker.Pending)
            return;
        tracker.Pending = true;
        block.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ApplyPending, block);
    }

    private static readonly DispatcherOperationCallback ApplyPending = ApplyPendingCore;

    private static object? ApplyPendingCore(object state)
    {
        TextBlock block = (TextBlock)state;
        if (Trackers.TryGetValue(block, out Tracker? tracker))
        {
            tracker.Pending = false;
            Apply(block);
        }
        return null;
    }

    private static void Apply(TextBlock block)
    {
        if (!Trackers.TryGetValue(block, out Tracker? tracker))
            return;
        string text = block.Text;
        tracker.Snapshot = text;
        if (!MayContainEmoji(text) || !EnsureFont())
        {
            Clear(block, tracker);
            return;
        }
        List<(TextPointer Start, TextPointer End, ushort[] Glyphs)> found = FindEmoji(block);
        if (found.Count == 0)
        {
            Clear(block, tracker);
            return;
        }
        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(block);
        if (layer == null)
        {
            Clear(block, tracker);
            return;
        }
        found = Hide(block, tracker);
        if (found.Count == 0)
        {
            Clear(block, tracker);
            return;
        }
        if (tracker.Adorner == null)
        {
            tracker.Adorner = new EmojiAdorner(block);
            layer.Add(tracker.Adorner);
        }
        tracker.Adorner.SetItems(found, text);
    }

    private static void Clear(TextBlock block, Tracker tracker)
    {
        Restore(block, tracker);
        if (tracker.Adorner != null)
        {
            tracker.Adorner.Detach();
            AdornerLayer.GetAdornerLayer(block)?.Remove(tracker.Adorner);
            tracker.Adorner = null;
        }
    }

    private static List<(TextPointer Start, TextPointer End, ushort[] Glyphs)> Hide(TextBlock block, Tracker tracker)
    {
        Restore(block, tracker);
        List<(TextPointer Start, TextPointer End, ushort[] Glyphs)> found = FindEmoji(block);
        List<(TextPointer Start, TextPointer End, ushort[] Glyphs)> hidden = new(found.Count);
        Dictionary<Run, TextEffectCollection> effects = new();
        TextPointer content = block.ContentStart;
        foreach ((TextPointer start, TextPointer end, ushort[] glyphs) item in found)
        {
            if (item.start.Parent is not Run run || !ReferenceEquals(item.end.Parent, run))
                continue;
            int position = CharacterIndex(content, item.start) + 1;
            int count = item.start.GetOffsetToPosition(item.end);
            if (position < content.GetOffsetToPosition(run.ContentStart) || position + count > content.GetOffsetToPosition(run.ContentEnd))
                continue;
            if (!effects.TryGetValue(run, out TextEffectCollection? collection))
            {
                collection = new TextEffectCollection();
                effects.Add(run, collection);
            }
            collection.Add(new TextEffect { PositionStart = position, PositionCount = count, Foreground = Brushes.Transparent });
            hidden.Add(item);
        }
        tracker.Hiding = true;
        try
        {
            foreach ((Run run, TextEffectCollection collection) in effects)
            {
                tracker.Hidden.Add((run, run.ReadLocalValue(TextElement.TextEffectsProperty)));
                run.TextEffects = collection;
            }
        }
        finally
        {
            tracker.Hiding = false;
        }
        return hidden;
    }

    private static int CharacterIndex(TextPointer from, TextPointer to)
    {
        int index = 0;
        TextPointer? pointer = from;
        while (pointer != null && pointer.CompareTo(to) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                TextPointer next = pointer.GetNextContextPosition(LogicalDirection.Forward) ?? to;
                index += next.CompareTo(to) > 0 ? pointer.GetOffsetToPosition(to) : pointer.GetOffsetToPosition(next);
                pointer = next;
                continue;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return index;
    }

    private static void Restore(TextBlock block, Tracker tracker)
    {
        if (tracker.Hidden.Count == 0)
            return;
        tracker.Hiding = true;
        try
        {
            foreach ((Run run, object original) in tracker.Hidden)
            {
                if (!run.ContentStart.IsInSameDocument(block.ContentStart))
                    continue;
                if (original == DependencyProperty.UnsetValue)
                    run.ClearValue(TextElement.TextEffectsProperty);
                else
                    run.SetValue(TextElement.TextEffectsProperty, original);
            }
        }
        finally
        {
            tracker.Hiding = false;
            tracker.Hidden.Clear();
        }
    }

    private static bool IsHiddenDetached(TextBlock block)
    {
        if (!Trackers.TryGetValue(block, out Tracker? tracker))
            return false;
        foreach ((Run run, _) in tracker.Hidden)
        {
            if (!run.ContentStart.IsInSameDocument(block.ContentStart))
                return true;
        }
        return false;
    }

    private static bool MayContainEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (char c in text)
        {
            if (char.IsHighSurrogate(c) || (c >= 0x2190 && c <= 0x2BFF) || c == VariationSelectorEmoji)
                return true;
        }
        return false;
    }

    private static List<(TextPointer Start, TextPointer End, ushort[] Glyphs)> FindEmoji(TextBlock block)
    {
        List<(TextPointer, TextPointer, ushort[])> result = new();
        TextPointer? pointer = block.ContentStart;
        while (pointer != null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                string run = pointer.GetTextInRun(LogicalDirection.Forward);
                foreach (Emoji emoji in Scan(run))
                {
                    TextPointer? start = pointer.GetPositionAtOffset(emoji.Offset);
                    TextPointer? end = pointer.GetPositionAtOffset(emoji.Offset + emoji.Length);
                    if (start != null && end != null)
                        result.Add((start, end, emoji.Glyphs));
                }
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return result;
    }

    private static IEnumerable<Emoji> Scan(string text)
    {
        List<int> codePoints = new();
        int index = 0;
        while (index < text.Length)
        {
            int start = index;
            int first = PeekRune(text, index, out int firstLength);
            if (first < 0)
            {
                index++;
                continue;
            }
            index += firstLength;
            codePoints.Clear();
            codePoints.Add(first);
            bool presentation = false;
            bool sequence = false;
            if (IsRegionalIndicator(first))
            {
                int pair = PeekRune(text, index, out int pairLength);
                if (IsRegionalIndicator(pair))
                {
                    codePoints.Add(pair);
                    index += pairLength;
                }
                sequence = true;
            }
            while (index < text.Length)
            {
                int value = PeekRune(text, index, out int valueLength);
                if (value == VariationSelectorEmoji || value == VariationSelectorText)
                {
                    presentation |= value == VariationSelectorEmoji;
                    index += valueLength;
                    continue;
                }
                if (IsSkinTone(value) || value == KeycapCombiner || IsTag(value))
                {
                    codePoints.Add(value);
                    index += valueLength;
                    sequence = true;
                    continue;
                }
                if (value == ZeroWidthJoiner)
                {
                    int joined = PeekRune(text, index + valueLength, out int joinedLength);
                    if (joined < 0)
                        break;
                    codePoints.Add(value);
                    codePoints.Add(joined);
                    index += valueLength + joinedLength;
                    sequence = true;
                    continue;
                }
                break;
            }
            if (!sequence && !IsEmojiCodePoint(first, presentation))
                continue;
            ushort[]? glyphs = Shape(codePoints);
            if (glyphs != null)
                yield return new Emoji(start, index - start, glyphs);
        }
    }

    private static int PeekRune(string text, int index, out int length)
    {
        length = 0;
        if (index >= text.Length || Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out length) != System.Buffers.OperationStatus.Done)
            return -1;
        return rune.Value;
    }

    private static bool IsSkinTone(int value) => value is >= 0x1F3FB and <= 0x1F3FF;

    private static bool IsRegionalIndicator(int value) => value is >= 0x1F1E6 and <= 0x1F1FF;

    private static bool IsTag(int value) => value is >= 0xE0020 and <= 0xE007F;

    private static bool IsEmojiCodePoint(int value, bool presentation)
    {
        if (value >= 0x1F000)
            return true;
        if (value is >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF)
            return true;
        return presentation && value >= 0xA9;
    }

    private static bool EnsureFont()
    {
        lock (Gate)
        {
            if (_loadAttempted)
                return _typeface != null;
            _loadAttempted = true;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), FontFileName);
                if (!File.Exists(path))
                    return false;
                using FileStream stream = File.OpenRead(path);
                byte[]? colr = ReadTable(stream, "COLR");
                byte[]? cpal = ReadTable(stream, "CPAL");
                if (colr == null || cpal == null || !ParseColr(colr) || !ParseCpal(cpal))
                    return false;
                if (ReadTable(stream, "GSUB") is { } gsub)
                    ParseGsub(gsub);
                _typeface = new GlyphTypeface(new Uri(path));
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Color emoji font could not be loaded: " + ex.Message);
                _baseGlyphs = null;
                _layers = null;
                _palette = null;
                return false;
            }
        }
    }

    private static byte[]? ReadTable(FileStream stream, string tag)
    {
        byte[] header = new byte[12];
        stream.Position = 0;
        stream.ReadExactly(header);
        int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        byte[] directory = new byte[count * 16];
        stream.ReadExactly(directory);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = directory.AsSpan(i * 16, 16);
            if (Encoding.ASCII.GetString(record[..4]) != tag)
                continue;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            byte[] table = new byte[length];
            stream.Position = offset;
            stream.ReadExactly(table);
            return table;
        }
        return null;
    }

    private static bool ParseColr(byte[] table)
    {
        ReadOnlySpan<byte> data = table;
        int baseCount = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int baseOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int layerOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        int layerCount = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        if (baseCount == 0 || baseOffset + baseCount * 6 > data.Length || layerOffset + layerCount * 4 > data.Length)
            return false;
        Dictionary<ushort, (int, int)> bases = new(baseCount);
        for (int i = 0; i < baseCount; i++)
        {
            ReadOnlySpan<byte> record = data.Slice(baseOffset + i * 6, 6);
            bases[BinaryPrimitives.ReadUInt16BigEndian(record)] = (
                BinaryPrimitives.ReadUInt16BigEndian(record[2..]),
                BinaryPrimitives.ReadUInt16BigEndian(record[4..])
            );
        }
        var layers = new (ushort, ushort)[layerCount];
        for (int i = 0; i < layerCount; i++)
        {
            ReadOnlySpan<byte> record = data.Slice(layerOffset + i * 4, 4);
            layers[i] = (BinaryPrimitives.ReadUInt16BigEndian(record), BinaryPrimitives.ReadUInt16BigEndian(record[2..]));
        }
        _baseGlyphs = bases;
        _layers = layers;
        return true;
    }

    private static bool ParseCpal(byte[] table)
    {
        ReadOnlySpan<byte> data = table;
        int entries = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        int colorOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        int firstIndex = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        if (colorOffset + (firstIndex + entries) * 4 > data.Length)
            return false;
        var palette = new Brush?[entries];
        for (int i = 0; i < entries; i++)
        {
            ReadOnlySpan<byte> color = data.Slice(colorOffset + (firstIndex + i) * 4, 4);
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(color[3], color[2], color[1], color[0]));
            brush.Freeze();
            palette[i] = brush;
        }
        _palette = palette;
        return true;
    }

    private static Layer[]? GetLayers(ushort glyph)
    {
        lock (Gate)
        {
            if (LayerCache.TryGetValue(glyph, out Layer[]? cached))
                return cached;
            Layer[]? built = null;
            try
            {
                if (_typeface != null && _baseGlyphs != null && _layers != null && _palette != null && _baseGlyphs.TryGetValue(glyph, out var range))
                {
                    built = new Layer[range.Count];
                    for (int i = 0; i < range.Count; i++)
                    {
                        (ushort layerGlyph, ushort paletteIndex) = _layers[range.First + i];
                        Geometry geometry = _typeface.GetGlyphOutline(layerGlyph, OutlineEmSize, OutlineEmSize);
                        geometry.Freeze();
                        Brush? fill = paletteIndex == ForegroundPaletteIndex || paletteIndex >= _palette.Length ? null : _palette[paletteIndex];
                        built[i] = new Layer(geometry, fill);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Color emoji glyph could not be built: " + ex.Message);
                built = null;
            }
            LayerCache[glyph] = built;
            return built;
        }
    }

    private static Layer[]? GetLayers(ushort[] glyphs)
    {
        if (glyphs.Length == 1)
            return GetLayers(glyphs[0]);
        lock (Gate)
        {
            if (SequenceCache.TryGetValue(glyphs, out Layer[]? cached))
                return cached;
        }
        List<Layer> combined = new();
        double advance = 0;
        foreach (ushort glyph in glyphs)
        {
            Layer[]? layers = GetLayers(glyph);
            if (layers == null || _typeface == null)
            {
                combined.Clear();
                break;
            }
            TranslateTransform shift = new TranslateTransform(advance, 0);
            shift.Freeze();
            foreach (Layer layer in layers)
            {
                Geometry moved = layer.Geometry.Clone();
                moved.Transform = shift;
                moved.Freeze();
                combined.Add(new Layer(moved, layer.Fill));
            }
            advance += _typeface.AdvanceWidths.TryGetValue(glyph, out double width) ? width * OutlineEmSize : OutlineEmSize;
        }
        Layer[]? built = combined.Count == 0 ? null : combined.ToArray();
        lock (Gate)
            SequenceCache.AddOrUpdate(glyphs, built);
        return built;
    }

    private sealed class EmojiAdorner : Adorner
    {
        private readonly TextBlock _block;
        private const int MaxRenderRetries = 3;
        private List<(TextPointer Start, TextPointer End, ushort[] Glyphs)> _items = new();
        private int _retries;
        private string? _snapshot;

        public EmojiAdorner(TextBlock block)
            : base(block)
        {
            _block = block;
            IsHitTestVisible = false;
            _block.IsVisibleChanged += OnBlockVisibilityChanged;
        }

        public void SetItems(List<(TextPointer Start, TextPointer End, ushort[] Glyphs)> items, string snapshot)
        {
            _items = items;
            _snapshot = snapshot;
            _retries = 0;
            InvalidateVisual();
        }

        private void RetryRender()
        {
            if (_retries >= MaxRenderRetries)
                return;
            _retries++;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(InvalidateVisual));
        }

        public void Detach()
        {
            _block.IsVisibleChanged -= OnBlockVisibilityChanged;
            _items = new();
        }

        private void OnBlockVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            if (!_block.IsVisible || _items.Count == 0)
                return;
            if (!string.Equals(_block.Text, _snapshot, StringComparison.Ordinal) || IsHiddenDetached(_block))
            {
                QueueApply(_block);
                return;
            }
            Brush foreground = _block.Foreground ?? Brushes.White;
            double scale = _block.FontSize / OutlineEmSize;
            dc.PushClip(new RectangleGeometry(new Rect(_block.RenderSize)));
            dc.PushOpacity(_block.Opacity);
            foreach ((TextPointer start, TextPointer end, ushort[] glyphs) in _items)
            {
                if (!start.IsInSameDocument(_block.ContentStart))
                    continue;
                Layer[]? layers = GetLayers(glyphs);
                if (layers == null || layers.Length == 0)
                    continue;
                Rect from = start.GetCharacterRect(LogicalDirection.Forward);
                Rect to = end.GetCharacterRect(LogicalDirection.Backward);
                if (from.IsEmpty || to.IsEmpty)
                {
                    RetryRender();
                    continue;
                }
                double width = Math.Abs(to.Y - from.Y) < 0.5 && to.X > from.X ? to.X - from.X : _block.FontSize;
                Rect bounds = Rect.Empty;
                foreach (Layer layer in layers)
                    bounds.Union(layer.Geometry.Bounds);
                if (bounds.IsEmpty)
                    continue;
                double fit = Math.Min(scale, width / Math.Max(bounds.Width, 1.0));
                double centerX = from.X + width / 2.0;
                double centerY = from.Y + from.Height / 2.0;
                dc.PushTransform(new MatrixTransform(
                    fit,
                    0,
                    0,
                    fit,
                    centerX - (bounds.X + bounds.Width / 2.0) * fit,
                    centerY - (bounds.Y + bounds.Height / 2.0) * fit
                ));
                foreach (Layer layer in layers)
                    dc.DrawGeometry(layer.Fill ?? foreground, null, layer.Geometry);
                dc.Pop();
            }
            dc.Pop();
            dc.Pop();
        }
    }
}
