using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.UI
{
    public static class GifImageBehavior
    {
        private const long MaxEncodedBytes = 16L * 1024L * 1024L;

        private const long MaxDecodedBytes = 48L * 1024L * 1024L;

        private const int MaxAnimationFrames = 120;

        private static readonly DependencyProperty PortableAnimationStateProperty =
            DependencyProperty.RegisterAttached(
                "PortableAnimationState",
                typeof(PortableAnimationState),
                typeof(GifImageBehavior),
                new PropertyMetadata(null));

        public static readonly DependencyProperty SourcePathProperty =
            DependencyProperty.RegisterAttached(
                "SourcePath",
                typeof(string),
                typeof(GifImageBehavior),
                new PropertyMetadata(null, OnSourcePathChanged));

        public static string GetSourcePath(DependencyObject obj) => (string)obj.GetValue(SourcePathProperty);

        public static void SetSourcePath(DependencyObject obj, string value) => obj.SetValue(SourcePathProperty, value);

		public static void SetFrames(Image image, IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames)
		{
			if (image == null)
				return;
			StopPortableAnimation(image);
			image.BeginAnimation(Image.SourceProperty, null);
			image.Source = null;
			ApplyPortableFrames(image, frames);
		}

        private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image image)
                return;

            StopPortableAnimation(image);
            image.BeginAnimation(Image.SourceProperty, null);

            string path = e.NewValue as string ?? "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                image.Source = null;
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".gif")
            {
                try
                {
                    AnimateGif(image, path);
                    return;
                }
                catch
                {
                }
            }

            image.Source = LoadStatic(path);
        }

        private static BitmapSource? LoadStatic(string path)
        {
            try
            {
                return Voidstrap.Utility.SafeImaging.FromUri(new Uri(path, UriKind.Absolute));
            }
            catch
            {
                return null;
            }
        }

        private static void AnimateGif(Image image, string path)
        {
            if (new FileInfo(path).Length > MaxEncodedBytes)
            {
                image.Source = LoadStatic(path);
                return;
            }

            if (Voidstrap.Utility.Platform.IsLinux)
            {
                AnimatePortableGif(image, path);
                return;
            }

            var decoder = new GifBitmapDecoder(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                image.Source = null;
                return;
            }

            if (decoder.Frames.Count == 1)
            {
                var single = decoder.Frames[0];
                single.Freeze();
                image.Source = single;
                return;
            }

            long decodedBytes = 0;
            foreach (BitmapFrame frame in decoder.Frames)
            {
                long frameBytes = (long)frame.PixelWidth * frame.PixelHeight * 4;
                if (frame.PixelWidth < 1 || frame.PixelHeight < 1 || frameBytes > MaxDecodedBytes || decodedBytes > MaxDecodedBytes - frameBytes)
                {
                    var first = decoder.Frames[0];
                    first.Freeze();
                    image.Source = first;
                    return;
                }
                decodedBytes += frameBytes;
            }

            if (decoder.Frames.Count > MaxAnimationFrames)
            {
                var first = decoder.Frames[0];
                first.Freeze();
                image.Source = first;
                return;
            }

            var animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            var time = TimeSpan.Zero;

            foreach (var frame in decoder.Frames)
            {
                frame.Freeze();
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frame, KeyTime.FromTimeSpan(time)));

                int delayCentiseconds = 10;
                try
                {
                    if (frame.Metadata is BitmapMetadata md && md.ContainsQuery("/grctlext/Delay"))
                    {
                        var raw = md.GetQuery("/grctlext/Delay");
                        if (raw is ushort cs && cs > 0)
                            delayCentiseconds = cs;
                    }
                }
                catch
                {
                }

                time += TimeSpan.FromMilliseconds(delayCentiseconds * 10);
            }

            animation.Duration = new Duration(time);
            image.Source = decoder.Frames[0];
            image.BeginAnimation(Image.SourceProperty, animation);
        }

        private static void AnimatePortableGif(Image image, string path)
        {
            IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames = Voidstrap.Utility.SafeImaging.DecodeAnimationPortable(path);
			ApplyPortableFrames(image, frames);
		}

		private static void ApplyPortableFrames(Image image, IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames)
		{
            if (frames.Count == 0)
            {
                image.Source = null;
                return;
            }

            if (frames.Count == 1)
            {
                image.Source = frames[0].Frame;
                return;
            }

            image.Source = frames[0].Frame;
            PortableAnimationState state = new(image, frames);
            image.SetValue(PortableAnimationStateProperty, state);
            state.Start();
        }

        private static void StopPortableAnimation(Image image)
        {
            if (image.GetValue(PortableAnimationStateProperty) is PortableAnimationState state)
                state.Stop();
        }

        private sealed class PortableAnimationState
        {
            private readonly Image _image;
            private readonly IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> _frames;
            private System.Threading.Timer? _timer;
            private int _index;
            private int _queued;
            private bool _stopped;

            public PortableAnimationState(Image image, IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames)
            {
                _image = image;
                _frames = frames;
                _image.Loaded += OnLoaded;
                _image.Unloaded += OnUnloaded;
            }

            public void Start()
            {
                if (_stopped || _timer != null || !_image.IsLoaded)
                    return;

                int delay = Math.Clamp(_frames[_index].DelayMilliseconds, 20, 10000);
                _timer = new System.Threading.Timer(OnTimer, null, delay, System.Threading.Timeout.Infinite);
            }

            public void Stop()
            {
                if (_stopped)
                    return;

                _stopped = true;
                Pause();
                _image.Loaded -= OnLoaded;
                _image.Unloaded -= OnUnloaded;
                if (ReferenceEquals(_image.GetValue(PortableAnimationStateProperty), this))
                    _image.ClearValue(PortableAnimationStateProperty);
            }

            private void Pause()
            {
                _timer?.Dispose();
                _timer = null;
            }

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                Start();
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                Pause();
            }

            private void OnTimer(object? state)
            {
                if (_stopped || System.Threading.Interlocked.Exchange(ref _queued, 1) != 0)
                    return;

                try
                {
                    _image.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Advance));
                }
                catch (InvalidOperationException)
                {
                    System.Threading.Interlocked.Exchange(ref _queued, 0);
                }
            }

            private void Advance()
            {
                System.Threading.Interlocked.Exchange(ref _queued, 0);
                if (_stopped || !_image.IsLoaded)
                    return;

                _index = (_index + 1) % _frames.Count;
                _image.Source = _frames[_index].Frame;
                int delay = Math.Clamp(_frames[_index].DelayMilliseconds, 20, 10000);
                _timer?.Change(delay, System.Threading.Timeout.Infinite);
            }
        }
    }
}
