using System;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public interface IMusicPlayback : IDisposable
{
    bool IsPlaying { get; }

    bool HasTrack { get; }

    TimeSpan Duration { get; }

    TimeSpan Position { get; set; }

    double Volume { get; set; }

    bool EqualizerEnabled { get; set; }

    Exception? LastError { get; }

    event EventHandler? PlaybackEnded;

    event EventHandler? PlayStateChanged;

    void SetBandGain(int band, float gainDb);

    bool Load(string path);

    void Play();

    void Pause();

    void Stop();
}

public static class MusicPlayback
{
    public static IMusicPlayback Create() => OperatingSystem.IsWindows() ? new PlaybackService() : new GStreamerPlayback();

    public static TimeSpan ProbeDuration(string path) => OperatingSystem.IsWindows() ? PlaybackService.ProbeDuration(path) : GStreamerPlayback.ProbeDuration(path);
}
