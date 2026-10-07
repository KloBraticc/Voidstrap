using System;
using System.Threading;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public sealed class MacPlayback : IMusicPlayback
{
    private const int EndPollMilliseconds = 250;

    private readonly MacOSAudioPlayer _player = new();
    private Timer? _endTimer;
    private bool _disposed;

    public bool IsPlaying => _player.IsPlaying;

    public bool HasTrack => _player.IsLoaded;

    public TimeSpan Duration => _player.Duration;

    public TimeSpan Position
    {
        get => _player.Position;
        set => _player.SeekTo(value);
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = (float)value;
    }

    public bool EqualizerEnabled
    {
        get => _player.EqualizerEnabled;
        set => _player.EqualizerEnabled = value;
    }

    public Exception? LastError { get; private set; }

    public event EventHandler? PlaybackEnded;

    public event EventHandler? PlayStateChanged;

    public void SetBandGain(int band, float gainDb) => _player.SetBandGain(band, gainDb);

    public bool Load(string path)
    {
        Stop();
        string? error = _player.Load(path);
        LastError = error == null ? null : new InvalidOperationException(error);
        return error == null;
    }

    public void Play()
    {
        if (!_player.IsLoaded)
            return;
        _player.Play();
        _endTimer ??= new Timer(OnEndPoll, null, EndPollMilliseconds, EndPollMilliseconds);
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (!_player.IsLoaded)
            return;
        _player.Pause();
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        bool hadTrack = _player.IsLoaded;
        StopTimer();
        _player.Unload();
        if (hadTrack)
            PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _player.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TimeSpan ProbeDuration(string path) => MacOSAudioPlayer.ProbeDuration(path);

    private void OnEndPoll(object? state)
    {
        try
        {
            if (_player.HasFinished())
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
    }

    private void StopTimer()
    {
        Timer? timer = Interlocked.Exchange(ref _endTimer, null);
        timer?.Dispose();
    }
}
