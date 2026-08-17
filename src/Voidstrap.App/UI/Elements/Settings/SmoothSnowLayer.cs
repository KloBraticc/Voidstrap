using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Voidstrap.UI.Elements.Settings;

public sealed class SmoothSnowLayer : FrameworkElement, IDisposable
{
    private sealed class Particle
    {
        public double X;
        public double Y;
        public double Radius;
        public double Speed;
        public double Drift;
        public double Phase;
        public double Flutter;
        public int BrushIndex;
    }

    private static readonly Brush[] Brushes = CreateBrushes();
    private readonly List<Particle> _particles = new(96);
    private readonly Random _random = new();
    private TimeSpan _lastRenderTime;
    private bool _requestedActive;
    private bool _rendering;
    private bool _disposed;

    public SmoothSnowLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public void SetActive(bool active)
    {
        if (_disposed)
        {
            return;
        }
        _requestedActive = active;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UpdateRenderingState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureParticleCount();
        UpdateRenderingState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateRenderingState();
    }

    private void UpdateRenderingState()
    {
        if (_requestedActive && IsLoaded && IsVisible)
        {
            StartRendering();
        }
        else
        {
            StopRendering();
        }
    }

    private void StartRendering()
    {
        if (_rendering)
        {
            return;
        }
        _rendering = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering)
        {
            return;
        }
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
        _lastRenderTime = TimeSpan.Zero;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_rendering || e is not RenderingEventArgs rendering)
        {
            return;
        }
        if (_lastRenderTime == TimeSpan.Zero)
        {
            _lastRenderTime = rendering.RenderingTime;
            InvalidateVisual();
            return;
        }
        double elapsed = (rendering.RenderingTime - _lastRenderTime).TotalSeconds;
        _lastRenderTime = rendering.RenderingTime;
        if (elapsed <= 0.0)
        {
            return;
        }
        Advance(Math.Min(elapsed, 1.0 / 30.0));
        InvalidateVisual();
    }

    private void Advance(double elapsed)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0.0 || height <= 0.0)
        {
            return;
        }
        for (int i = 0; i < _particles.Count; i++)
        {
            Particle particle = _particles[i];
            particle.Phase += particle.Flutter * elapsed;
            particle.Y += particle.Speed * elapsed;
            particle.X += (particle.Drift + Math.Sin(particle.Phase) * 7.0) * elapsed;
            if (particle.Y > height + particle.Radius)
            {
                ResetParticle(particle, width, height, false);
            }
            else if (particle.X < -particle.Radius)
            {
                particle.X = width + particle.Radius;
            }
            else if (particle.X > width + particle.Radius)
            {
                particle.X = -particle.Radius;
            }
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        EnsureParticleCount();
        double width = ActualWidth;
        double height = ActualHeight;
        for (int i = 0; i < _particles.Count; i++)
        {
            Particle particle = _particles[i];
            particle.X = Math.Clamp(particle.X, -particle.Radius, width + particle.Radius);
            particle.Y = Math.Clamp(particle.Y, -particle.Radius, height + particle.Radius);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        for (int i = 0; i < _particles.Count; i++)
        {
            Particle particle = _particles[i];
            drawingContext.DrawEllipse(Brushes[particle.BrushIndex], null, new Point(particle.X, particle.Y), particle.Radius, particle.Radius);
        }
    }

    private void EnsureParticleCount()
    {
        double area = Math.Max(1.0, ActualWidth * ActualHeight);
        int desired = Math.Clamp((int)Math.Round(area / 13500.0), 32, 84);
        while (_particles.Count > desired)
        {
            _particles.RemoveAt(_particles.Count - 1);
        }
        while (_particles.Count < desired)
        {
            Particle particle = new Particle();
            ResetParticle(particle, Math.Max(1.0, ActualWidth), Math.Max(1.0, ActualHeight), true);
            _particles.Add(particle);
        }
    }

    private void ResetParticle(Particle particle, double width, double height, bool initial)
    {
        double depth = _random.NextDouble();
        particle.Radius = 1.0 + depth * 2.4;
        particle.X = _random.NextDouble() * width;
        particle.Y = initial ? _random.NextDouble() * height : -particle.Radius - _random.NextDouble() * 24.0;
        particle.Speed = 28.0 + depth * 62.0;
        particle.Drift = -9.0 + _random.NextDouble() * 18.0;
        particle.Phase = _random.NextDouble() * Math.PI * 2.0;
        particle.Flutter = 0.8 + _random.NextDouble() * 1.8;
        particle.BrushIndex = Math.Clamp((int)Math.Round(depth * (Brushes.Length - 1)), 0, Brushes.Length - 1);
    }

    private static Brush[] CreateBrushes()
    {
        Brush[] brushes = new Brush[8];
        for (int i = 0; i < brushes.Length; i++)
        {
            SolidColorBrush brush = new(Color.FromArgb((byte)(72 + i * 23), 255, 255, 255));
            brush.Freeze();
            brushes[i] = brush;
        }
        return brushes;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopRendering();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        IsVisibleChanged -= OnIsVisibleChanged;
        _particles.Clear();
        GC.SuppressFinalize(this);
    }
}
