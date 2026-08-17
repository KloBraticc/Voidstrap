using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace Voidstrap.UI.Elements.Controls;

public sealed class CurrentLineHighlighter : IBackgroundRenderer, IDisposable
{
    private readonly TextArea _textArea;

    private readonly TextView _textView;

    private readonly DispatcherTimer _timer;

    private readonly Brush _fill;

    private readonly Pen _stroke;

    private double _current = double.NaN;

    private double _target;

    private double _height;

    private bool _disposed;

    public KnownLayer Layer => KnownLayer.Background;

    public CurrentLineHighlighter(TextArea textArea)
    {
        _textArea = textArea;
        _textView = textArea.TextView;

        _fill = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        _fill.Freeze();

        Brush strokeBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        strokeBrush.Freeze();
        _stroke = new Pen(strokeBrush, 1.0);
        _stroke.Freeze();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += Timer_Tick;

        _textView.BackgroundRenderers.Add(this);
        _textArea.Caret.PositionChanged += Caret_PositionChanged;
    }

    private void Caret_PositionChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
            _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        double delta = _target - _current;

        if (double.IsNaN(_current) || Math.Abs(delta) < 0.5)
        {
            _timer.Stop();
            return;
        }

        _current += delta * 0.35;
        _textView.InvalidateLayer(KnownLayer.Background);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_disposed || textView.Document == null)
            return;

        textView.EnsureVisualLines();
        VisualLine? line = textView.GetVisualLine(_textArea.Caret.Line);
        if (line == null)
            return;

        _target = line.VisualTop - textView.ScrollOffset.Y;
        _height = line.Height;

        if (double.IsNaN(_current) || Math.Abs(_target - _current) > _height * 4)
            _current = _target;

        double width = textView.ActualWidth - 1;
        if (width <= 0)
            return;

        drawingContext.DrawRoundedRectangle(_fill, _stroke, new Rect(0.5, _current + 0.5, width, _height - 1), 3, 3);

        if (Math.Abs(_target - _current) >= 0.5)
            _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _textArea.Caret.PositionChanged -= Caret_PositionChanged;
        _textView.BackgroundRenderers.Remove(this);
        GC.SuppressFinalize(this);
    }
}
