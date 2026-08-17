using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.Integrations.GameChat
{
    internal static class GameChatDrawing
    {
        public static readonly SolidColorBrush ButtonFill = CreateBrush(Color.FromArgb(179, 18, 18, 20));
        public static readonly SolidColorBrush Placeholder = CreateBrush(Color.FromArgb(180, 200, 200, 200));
        public static readonly SolidColorBrush ArrowActive = CreateBrush(Color.FromRgb(120, 170, 255));
        public static readonly SolidColorBrush ArrowIdle = CreateBrush(Color.FromArgb(150, 150, 150, 150));
        public static readonly Pen ProgressPen = CreatePen(Brushes.DeepSkyBlue, 4);
        public static readonly Pen GripPen = CreatePen(CreateBrush(Color.FromArgb(150, 255, 255, 255)), 2);
        public static readonly Pen CaretPen = CreatePen(CreateBrush(Color.FromRgb(230, 230, 230)), 1.4);

        private static SolidColorBrush CreateBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen CreatePen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness);
            pen.Freeze();
            return pen;
        }
    }

    public partial class GameChatRoundButton : FrameworkElement
    {
        public event EventHandler? Clicked;
        public event EventHandler? TripleTapped;
        public event EventHandler<Vector>? Dragged;
        public event EventHandler? DragEnded;

        private readonly DispatcherTimer _holdTimer;
        private readonly DispatcherTimer _visualTimer;
        private bool _isDragging;
		private bool _isPressed;
		private bool _shutdown;
        private Point _lastScreenPos;
		private Vector _pendingDrag;
		private int _dragDispatchPending;
        private double _holdProgress;
		private long _holdStartTicks;
        private const int HoldThreshold = 300;

		private long _lastTapTicks;
        private int _tapCount;
        private const int TripleTapWindowMs = 500;

        private readonly ImageSource? _imgOn;
        private readonly ImageSource? _imgOff;

        public bool IsActive { get; set; } = true;

        public GameChatRoundButton(ImageSource? imgOn, ImageSource? imgOff)
        {
            _imgOn = imgOn;
            _imgOff = imgOff;
            Width = 45;
            Height = 45;
            Cursor = Cursors.Hand;

            _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldThreshold) };
            _holdTimer.Tick += OnHoldTimerTick;

			_visualTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _visualTimer.Tick += OnVisualTimerTick;

			var clip = new EllipseGeometry(new Point(22.5, 22.5), 22.5, 22.5);
			clip.Freeze();
			Clip = clip;
        }

        private void OnHoldTimerTick(object? sender, EventArgs e)
        {
            _holdTimer.Stop();
            _visualTimer.Stop();
            _holdProgress = 0;
            _isDragging = true;
            Cursor = Cursors.SizeAll;
            InvalidateVisual();
        }

        private void OnVisualTimerTick(object? sender, EventArgs e)
        {
			double elapsed = Environment.TickCount64 - _holdStartTicks;
            _holdProgress = Math.Min(elapsed / HoldThreshold, 1.0);
            InvalidateVisual();
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
				_isPressed = true;
				_lastScreenPos = GetCursorPosition();
				_holdStartTicks = Environment.TickCount64;
                _holdProgress = 0;
                _holdTimer.Start();
                _visualTimer.Start();
                CaptureMouse();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isDragging)
            {
				Point current = GetCursorPosition();
                Vector delta = current - _lastScreenPos;
                if (delta.X != 0 || delta.Y != 0)
                {
                    _lastScreenPos = current;
					_pendingDrag += delta;
					ScheduleDrag();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
			if (e.ChangedButton != MouseButton.Left || !_isPressed)
			{
				base.OnMouseUp(e);
				return;
			}
			bool wasDragging = _isDragging;
            _holdTimer.Stop();
            _visualTimer.Stop();

			if (!_isDragging && Environment.TickCount64 - _holdStartTicks < HoldThreshold)
            {
				long now = Environment.TickCount64;
				_tapCount = now - _lastTapTicks <= TripleTapWindowMs ? _tapCount + 1 : 1;
				_lastTapTicks = now;
                if (_tapCount >= 3)
                {
                    _tapCount = 0;
                    TripleTapped?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Clicked?.Invoke(this, EventArgs.Empty);
                }
            }

            _holdProgress = 0;
            _isDragging = false;
			_isPressed = false;
			FlushDrag();
			if (IsMouseCaptured)
				ReleaseMouseCapture();
            Cursor = Cursors.Hand;
			if (wasDragging)
				DragEnded?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            base.OnMouseUp(e);
        }

		protected override void OnLostMouseCapture(MouseEventArgs e)
		{
			if (_isPressed)
			{
				bool wasDragging = _isDragging;
				_holdTimer.Stop();
				_visualTimer.Stop();
				_isPressed = false;
				_isDragging = false;
				_holdProgress = 0;
				FlushDrag();
				Cursor = Cursors.Hand;
				if (wasDragging)
					DragEnded?.Invoke(this, EventArgs.Empty);
				InvalidateVisual();
			}
			base.OnLostMouseCapture(e);
		}

		private void ScheduleDrag()
		{
			if (_shutdown || Interlocked.Exchange(ref _dragDispatchPending, 1) != 0)
				return;
			try
			{
				Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(FlushDrag));
			}
			catch (InvalidOperationException)
			{
				Interlocked.Exchange(ref _dragDispatchPending, 0);
			}
		}

		private void FlushDrag()
		{
			Interlocked.Exchange(ref _dragDispatchPending, 0);
			Vector delta = _pendingDrag;
			_pendingDrag = default;
			if (!_shutdown && (delta.X != 0 || delta.Y != 0))
				Dragged?.Invoke(this, delta);
		}

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawEllipse(GameChatDrawing.ButtonFill, null, new Point(22.5, 22.5), 22.5, 22.5);

            ImageSource? currentImg = IsActive ? _imgOn : _imgOff;
            if (currentImg != null)
            {
                double w = currentImg.Width;
                double h = currentImg.Height;
                if (w > 45) { h = h * 45 / w; w = 45; }
                if (h > 45) { w = w * 45 / h; h = 45; }
                dc.DrawImage(currentImg, new Rect((45 - w) / 2, (45 - h) / 2, w, h));
            }

            if (_holdProgress > 0 && _holdProgress < 1.0)
            {
                double penWidth = 4;
                double radius = (45 - penWidth) / 2;
                Point center = new(22.5, 22.5);
                double sweep = _holdProgress * 360;
                double startAngle = -90;
                double endAngle = startAngle + sweep;
                Point start = ArcPoint(center, radius, startAngle);
                Point end = ArcPoint(center, radius, endAngle);
				var geometry = new StreamGeometry();
				using (StreamGeometryContext context = geometry.Open())
				{
					context.BeginFigure(start, false, false);
					context.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
				}
				geometry.Freeze();
                dc.DrawGeometry(null, GameChatDrawing.ProgressPen, geometry);
            }
        }

        private static Point ArcPoint(Point center, double radius, double angleDegrees)
        {
            double rad = angleDegrees * Math.PI / 180.0;
            return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
        }

        public void Shutdown()
        {
			if (_shutdown)
				return;
			_shutdown = true;
            _holdTimer.Stop();
            _holdTimer.Tick -= OnHoldTimerTick;
            _visualTimer.Stop();
            _visualTimer.Tick -= OnVisualTimerTick;
			_pendingDrag = default;
			Interlocked.Exchange(ref _dragDispatchPending, 0);
			_isPressed = false;
			_isDragging = false;
			if (IsMouseCaptured)
				ReleaseMouseCapture();
			Clicked = null;
			TripleTapped = null;
			Dragged = null;
			DragEnded = null;
        }

		private static Point GetCursorPosition()
		{
			return GetCursorPos(out NativePoint point) ? new Point(point.X, point.Y) : default;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct NativePoint
		{
			public int X;
			public int Y;
		}

		[LibraryImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool GetCursorPos(out NativePoint point);
    }

    public class GameChatInputBox : FrameworkElement
    {
        private const double TextLeft = 10;
        private const double SendZoneWidth = 38;
        private const double FontSize = 13;

        private bool _isChatting;

        public bool IsChatting
        {
            get => _isChatting;
            set
            {
                if (_isChatting == value)
                    return;
                _isChatting = value;
                _caretVisible = true;
                if (value)
                    _caretTimer.Start();
                else
                    _caretTimer.Stop();
            }
        }

        public string RawText { get; set; } = "";
        public int CaretIndex { get; set; }

        public event EventHandler? Clicked;
        public event EventHandler? SendRequested;

        private bool _caretVisible = true;
        private readonly DispatcherTimer _caretTimer;
        private readonly Typeface _typeface = new(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private const double SendIconSize = 17;
        private static readonly Geometry? SendIconGeometry = BuildSendIcon();

        private static Geometry? BuildSendIcon()
        {
            try
            {
                var gt = new GlyphTypeface(new Uri("pack://application:,,,/Wpf.Ui;component/Fonts/FluentSystemIcons-Filled.ttf"));
                if (!gt.CharacterToGlyphMap.TryGetValue(0xF6A3, out ushort idx))
                    return null;
                var geo = gt.GetGlyphOutline(idx, SendIconSize, SendIconSize);
                geo.Freeze();
                return geo;
            }
            catch
            {
                return null;
            }
        }

        public GameChatInputBox()
        {
            _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _caretTimer.Tick += OnCaretTimerTick;
            Cursor = Cursors.IBeam;
        }

        private bool InSendZone(Point p) => p.X >= ActualWidth - SendZoneWidth;

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (InSendZone(p) && (_isChatting || !string.IsNullOrEmpty(RawText)))
                SendRequested?.Invoke(this, EventArgs.Empty);
            else
                Clicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            base.OnMouseLeftButtonDown(e);
        }

        private void OnCaretTimerTick(object? sender, EventArgs e)
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        }

        private FormattedText MakeText(string s, Brush brush, double maxWidth)
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double safeWidth = (double.IsNaN(maxWidth) || double.IsInfinity(maxWidth) || maxWidth < 1) ? 100000 : maxWidth;
            return new FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, _typeface, FontSize, brush, dpi)
            {
                MaxTextWidth = safeWidth,
                MaxLineCount = 1,
                Trimming = TextTrimming.None,
            };
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

            double textMaxWidth = ActualWidth - TextLeft - SendZoneWidth;

            if (!IsChatting && string.IsNullOrEmpty(RawText))
            {
                var placeholder = MakeText(GameChatStrings.ChatInputBoxText, GameChatDrawing.Placeholder, textMaxWidth);
                dc.DrawText(placeholder, new Point(TextLeft, (ActualHeight - placeholder.Height) / 2));
            }
            else
            {
                int caret = Math.Max(0, Math.Min(CaretIndex, RawText.Length));
                var text = MakeText(RawText, Brushes.White, textMaxWidth);
                double textY = (ActualHeight - text.Height) / 2;

                double caretX = TextLeft;
                if (caret > 0)
                {
                    var upto = MakeText(RawText[..caret], Brushes.White, double.PositiveInfinity);
                    caretX = TextLeft + Math.Min(upto.WidthIncludingTrailingWhitespace, textMaxWidth);
                }

                dc.DrawText(text, new Point(TextLeft, textY));

                if (IsChatting && _caretVisible)
                    dc.DrawLine(GameChatDrawing.CaretPen, new Point(caretX, textY + 1), new Point(caretX, textY + text.Height - 1));
            }

            bool canSend = _isChatting || !string.IsNullOrEmpty(RawText);
            Brush sendBrush = canSend ? GameChatDrawing.ArrowActive : GameChatDrawing.ArrowIdle;
            if (SendIconGeometry != null)
            {
                Rect b = SendIconGeometry.Bounds;
                double ox = ActualWidth - SendZoneWidth + (SendZoneWidth - b.Width) / 2 - b.X;
                double oy = (ActualHeight - b.Height) / 2 - b.Y;
                dc.PushTransform(new TranslateTransform(ox, oy));
                dc.DrawGeometry(sendBrush, null, SendIconGeometry);
                dc.Pop();
            }
            else
            {
                var arrow = MakeText("➤", sendBrush, SendZoneWidth);
                dc.DrawText(arrow, new Point(ActualWidth - SendZoneWidth + (SendZoneWidth - arrow.Width) / 2, (ActualHeight - arrow.Height) / 2));
            }
        }

        public void Shutdown()
        {
            _caretTimer.Stop();
            _caretTimer.Tick -= OnCaretTimerTick;
        }
    }

    public partial class GameChatResizeGrip : FrameworkElement
    {
        private Point _lastScreenPos;
        private bool _isResizing;
		private bool _shutdown;
		private Vector _pendingResize;
		private int _resizeDispatchPending;

        public event EventHandler<Vector>? ResizeDragged;

        public GameChatResizeGrip()
        {
            Width = 20;
            Height = 20;
            Cursor = Cursors.SizeNWSE;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isResizing = true;
				_lastScreenPos = GetCursorPosition();
                CaptureMouse();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isResizing)
            {
				Point current = GetCursorPosition();
                Vector delta = current - _lastScreenPos;
                if (delta.X != 0 || delta.Y != 0)
                {
                    _lastScreenPos = current;
					_pendingResize += delta;
					ScheduleResize();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
			if (e.ChangedButton != MouseButton.Left || !_isResizing)
			{
				base.OnMouseUp(e);
				return;
			}
            _isResizing = false;
			FlushResize();
			if (IsMouseCaptured)
				ReleaseMouseCapture();
            base.OnMouseUp(e);
        }

		protected override void OnLostMouseCapture(MouseEventArgs e)
		{
			if (_isResizing)
			{
				_isResizing = false;
				FlushResize();
			}
			base.OnLostMouseCapture(e);
		}

		private void ScheduleResize()
		{
			if (_shutdown || Interlocked.Exchange(ref _resizeDispatchPending, 1) != 0)
				return;
			try
			{
				Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(FlushResize));
			}
			catch (InvalidOperationException)
			{
				Interlocked.Exchange(ref _resizeDispatchPending, 0);
			}
		}

		private void FlushResize()
		{
			Interlocked.Exchange(ref _resizeDispatchPending, 0);
			Vector delta = _pendingResize;
			_pendingResize = default;
			if (!_shutdown && (delta.X != 0 || delta.Y != 0))
				ResizeDragged?.Invoke(this, delta);
		}

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Width, Height));
            dc.DrawLine(GameChatDrawing.GripPen, new Point(Width - 5, Height - 12), new Point(Width - 5, Height - 5));
            dc.DrawLine(GameChatDrawing.GripPen, new Point(Width - 12, Height - 5), new Point(Width - 5, Height - 5));
        }

		public void Shutdown()
		{
			if (_shutdown)
				return;
			_shutdown = true;
			_pendingResize = default;
			Interlocked.Exchange(ref _resizeDispatchPending, 0);
			_isResizing = false;
			if (IsMouseCaptured)
				ReleaseMouseCapture();
			ResizeDragged = null;
		}

		private static Point GetCursorPosition()
		{
			return GetCursorPos(out NativePoint point) ? new Point(point.X, point.Y) : default;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct NativePoint
		{
			public int X;
			public int Y;
		}

		[LibraryImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool GetCursorPos(out NativePoint point);
    }
}
