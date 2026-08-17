using RichPresence = DiscordRPC.RichPresence;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DiscordRPC;
using DiscordRPC.Message;
using Microsoft.Win32;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public class MusicPlayerViewModel : INotifyPropertyChanged, IDisposable
{
    private static class NativeMethods
    {
        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(nint hObject);
    }

    private static readonly string[] BandLabels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };

    private static readonly Dictionary<string, double[]> Presets = new()
    {
        ["Flat"] = new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        ["Bass Boost"] = new double[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 },
        ["Treble Boost"] = new double[] { 0, 0, 0, 0, 0, 1, 2, 4, 5, 6 },
        ["Vocal"] = new double[] { -2, -1, 0, 2, 4, 4, 3, 1, 0, -1 },
        ["Rock"] = new double[] { 4, 3, 1, -1, -1, 0, 2, 3, 4, 4 },
        ["Pop"] = new double[] { -1, 0, 2, 3, 3, 2, 0, -1, -1, -1 },
        ["Jazz"] = new double[] { 3, 2, 1, 2, -1, -1, 0, 1, 2, 3 },
        ["Electronic"] = new double[] { 5, 4, 1, 0, -2, 1, 0, 1, 4, 5 },
        ["Classical"] = new double[] { 4, 3, 2, 1, -1, -1, 0, 2, 3, 4 },
        ["Loudness"] = new double[] { 6, 4, 0, -2, -3, -1, 2, 4, 6, 6 }
    };

    private const string CustomPresetName = "Custom";

    private readonly PlaybackService _playback = new PlaybackService();
    private readonly DispatcherTimer _timer;
    private readonly string _savePath = Path.Combine(Paths.Config, "music.json");
    private readonly string _eqPath = Path.Combine(Paths.Config, "music_eq.json");

    private string _searchQuery = string.Empty;
    private CancellationTokenSource? _searchCts;
    private double _positionSeconds;
    private double _durationSeconds;
    private double _volume = 1.0;
    private double _preMuteVolume = 1.0;
    private bool _isMuted;
    private bool _isPlaying;
    private string _status = "Ready";
    private bool _isLooping;
    private bool _isSeeking;
    private bool _isShuffling = true;
    private bool _suppressAutoPlay;
    private bool _eqEnabled;
    private string _selectedEqPreset = "Flat";
    private bool _applyingPreset;
    private TrackItem? _selectedTrack;

    private DiscordRpcClient? _rpcClient;
    private bool _rpcConnected;
    private bool _showRpcConnectedMessage;
    private DateTime _lastRpcRefreshUtc = DateTime.MinValue;
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private static readonly Random _rng = new Random();
    private bool _disposed;

    public MusicPlayerViewModel()
    {
        Directory.CreateDirectory(Paths.Config);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        for (int i = 0; i < BandLabels.Length; i++)
            EqBands.Add(new EqualizerBand(i, BandLabels[i], OnBandChanged));

        _playback.PlaybackEnded += Playback_Ended;
        _playback.PlayStateChanged += Playback_StateChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Timer_Tick;

        OpenFilesCommand = new RelayCommand(OpenFiles);
        ConnectRpcCommand = new RelayCommand(() => ConnectRpc());
        PlayPauseCommand = new RelayCommand(PlayPause);
        NextCommand = new RelayCommand(Next);
        PreviousCommand = new RelayCommand(Previous);
        StopCommand = new RelayCommand(Stop);
        RemoveTrackCommand = new RelayCommand<TrackItem>(RemoveTrack);
        ToggleLoopCommand = new RelayCommand(() => { IsLooping = !IsLooping; Status = IsLooping ? "Loop on" : "Loop off"; UpdateRpcPresence(true); SaveLibraryThrottled(); });
        ToggleShuffleCommand = new RelayCommand(() => { IsShuffling = !IsShuffling; UpdateRpcPresence(true); SaveLibraryThrottled(); });
        ToggleMuteCommand = new RelayCommand(() => { IsMuted = !IsMuted; });
        ClearLibraryCommand = new RelayCommand(ClearLibrary);
        ResetEqualizerCommand = new RelayCommand(() => SelectedEqPreset = "Flat");

        LoadEqualizer();
        LoadLibrary();

        Tracks.CollectionChanged += Tracks_CollectionChanged;
        foreach (TrackItem track in Tracks)
            track.PropertyChanged += Track_PropertyChanged;

        _playback.Volume = _isMuted ? 0.0 : _volume;
        UpdateNowPlayingBindings();
        UpdateFilteredLibrary();
        _timer.Start();
    }

    public ObservableCollection<TrackItem> Tracks { get; } = new();

    public ObservableCollection<TrackItem> FilteredMusicLibrary { get; } = new();

    public ObservableCollection<EqualizerBand> EqBands { get; } = new();

    public IEnumerable<string> EqPresets => Presets.Keys;

    public TrackItem NowPlaying { get; private set; } = new TrackItem { Title = "-", FileType = "", FilePath = "" };

    public RelayCommand<TrackItem> RemoveTrackCommand { get; }
    public RelayCommand OpenFilesCommand { get; }
    public RelayCommand ConnectRpcCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleLoopCommand { get; }
    public RelayCommand ToggleShuffleCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand ClearLibraryCommand { get; }
    public RelayCommand ResetEqualizerCommand { get; }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value)
                return;
            _searchQuery = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSearchText));
            UpdateFilteredLibrary();
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(_searchQuery);

    public bool HasNoTracks => Tracks.Count == 0;

    public bool HasNoSearchMatches => Tracks.Count > 0 && FilteredMusicLibrary.Count == 0 && !string.IsNullOrWhiteSpace(SearchQuery);

    public bool IsShuffling
    {
        get => _isShuffling;
        set
        {
            if (_isShuffling == value)
                return;
            _isShuffling = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShuffleLabel));
            Status = IsShuffling ? "Shuffle on" : "Shuffle off";
        }
    }

    public string ShuffleLabel => IsShuffling ? "Shuffle On" : "Shuffle Off";

    public string RpcButtonLabel => _rpcConnected ? "Disconnect RPC" : "Connect RPC";

    public TrackItem? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            _selectedTrack = value;
            OnPropertyChanged();
            if (value != null && !_suppressAutoPlay)
                LoadAndPlay(value, true);
            UpdateNowPlayingBindings();
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            _playback.Volume = _isMuted ? 0.0 : _volume;
            if (_isMuted && _volume > 0.0)
            {
                _isMuted = false;
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(MuteLabel));
                OnPropertyChanged(nameof(MuteIcon));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumePercent));
            SaveLibraryThrottled();
        }
    }

    public string VolumePercent => $"{(int)Math.Round(_volume * 100.0)}%";

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value)
                return;
            if (value)
            {
                _preMuteVolume = _volume > 0.0 ? _volume : _preMuteVolume;
                _playback.Volume = 0.0;
            }
            else
            {
                double restore = _volume > 0.0 ? _volume : (_preMuteVolume > 0.0 ? _preMuteVolume : 0.5);
                _playback.Volume = restore;
                if (_volume == 0.0)
                {
                    _volume = restore;
                    OnPropertyChanged(nameof(Volume));
                    OnPropertyChanged(nameof(VolumePercent));
                }
            }
            _isMuted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MuteLabel));
            OnPropertyChanged(nameof(MuteIcon));
            SaveLibraryThrottled();
        }
    }

    public string MuteLabel => _isMuted ? "Unmute" : "Mute";

    public string MuteIcon => _isMuted ? "SpeakerMute24" : "Speaker224";

    public bool IsLooping
    {
        get => _isLooping;
        set
        {
            _isLooping = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoopLabel));
        }
    }

    public string LoopLabel => IsLooping ? "Loop On" : "Loop Off";

    public string PlayPauseLabel => _isPlaying ? "Pause" : "Play";

    public string PlayPauseIcon => _isPlaying ? "Pause24" : "Play24";

    public string PositionString => FormatTime(PositionSeconds);

    public string DurationString => FormatTime(NowPlayingDurationSeconds);

    public bool EqualizerEnabled
    {
        get => _eqEnabled;
        set
        {
            if (_eqEnabled == value)
                return;
            _eqEnabled = value;
            _playback.EqualizerEnabled = value;
            OnPropertyChanged();
            Status = value ? "Equalizer on" : "Equalizer off";
            SaveEqualizer();
        }
    }

    public string SelectedEqPreset
    {
        get => _selectedEqPreset;
        set
        {
            if (string.IsNullOrEmpty(value) || _selectedEqPreset == value)
                return;
            _selectedEqPreset = value;
            OnPropertyChanged();
            if (value != CustomPresetName && Presets.TryGetValue(value, out double[]? gains))
                ApplyPreset(gains);
            SaveEqualizer();
        }
    }

    public bool IsSeeking
    {
        get => _isSeeking;
        set
        {
            if (_isSeeking == value)
                return;
            _isSeeking = value;
            if (!_isSeeking)
            {
                _playback.Position = TimeSpan.FromSeconds(Math.Max(0.0, _positionSeconds));
                if (_isPlaying)
                    UpdateRpcPresence(true);
            }
            OnPropertyChanged();
        }
    }

    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (Math.Abs(_positionSeconds - value) < 0.01)
                return;
            _positionSeconds = value;
            if (!_isSeeking)
                _playback.Position = TimeSpan.FromSeconds(Math.Max(0.0, _positionSeconds));
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionString));
        }
    }

    public double NowPlayingDurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            _durationSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationString));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    private void OnBandChanged(EqualizerBand band)
    {
        _playback.SetBandGain(band.Index, (float)band.Gain);
        if (!_applyingPreset && _selectedEqPreset != CustomPresetName)
        {
            _selectedEqPreset = CustomPresetName;
            OnPropertyChanged(nameof(SelectedEqPreset));
        }
        if (!_applyingPreset)
            SaveEqualizer();
    }

    private void ApplyPreset(double[] gains)
    {
        _applyingPreset = true;
        try
        {
            for (int i = 0; i < EqBands.Count && i < gains.Length; i++)
            {
                EqBands[i].SetGainSilent(gains[i]);
                _playback.SetBandGain(i, (float)gains[i]);
            }
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!_playback.HasTrack)
            return;

        double duration = _playback.Duration.TotalSeconds;
        if (duration > 0 && Math.Abs(_durationSeconds - duration) > 0.2)
            NowPlayingDurationSeconds = duration;

        if (_isSeeking)
            return;

        double position = _playback.Position.TotalSeconds;
        if (_isPlaying && Math.Abs(_positionSeconds - position) > 0.2)
        {
            _positionSeconds = position;
            OnPropertyChanged(nameof(PositionSeconds));
            OnPropertyChanged(nameof(PositionString));
            SaveLibraryThrottled();
        }

        if (_isPlaying && _rpcConnected && _rpcClient?.IsInitialized == true && DateTime.UtcNow - _lastRpcRefreshUtc > TimeSpan.FromSeconds(8))
            UpdateRpcPresence();
    }

    private void Playback_StateChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _isPlaying = _playback.IsPlaying;
            RefreshUI();
        });
    }

    private void Playback_Ended(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (IsLooping && NowPlaying != null && !string.IsNullOrEmpty(NowPlaying.FilePath))
                {
                    LoadAndPlay(NowPlaying, true);
                    Status = "Looping: " + NowPlaying.Title;
                }
                else
                {
                    Next();
                }
            }
            catch (Exception ex)
            {
                Status = "Error advancing track: " + ex.Message;
            }
        });
    }

    public void UpdateFilteredLibrary()
    {
        CancellationTokenSource? previousSearch = _searchCts;
        _searchCts = new CancellationTokenSource();
        if (previousSearch != null)
        {
            try
            {
                previousSearch.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            previousSearch.Dispose();
        }
        CancellationToken token = _searchCts.Token;
        string query = SearchQuery?.Trim().ToLowerInvariant() ?? string.Empty;
        List<TrackItem> snapshot = Tracks.ToList();

        Task.Run(() =>
        {
            List<TrackItem> filtered = string.IsNullOrEmpty(query)
                ? snapshot
                : snapshot.Where(t =>
                    (!string.IsNullOrEmpty(t.Title) && t.Title.ToLowerInvariant().Contains(query)) ||
                    (!string.IsNullOrEmpty(t.Artist) && t.Artist.ToLowerInvariant().Contains(query)) ||
                    (!string.IsNullOrEmpty(t.FileType) && t.FileType.ToLowerInvariant().Contains(query))).ToList();

            if (token.IsCancellationRequested)
                return;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested)
                    return;
                FilteredMusicLibrary.Clear();
                foreach (TrackItem item in filtered)
                    FilteredMusicLibrary.Add(item);
                OnPropertyChanged(nameof(HasNoTracks));
                OnPropertyChanged(nameof(HasNoSearchMatches));
            });
        }, token);
    }

    private void Tracks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (TrackItem item in e.NewItems)
                item.PropertyChanged += Track_PropertyChanged;
        if (e.OldItems != null)
            foreach (TrackItem item in e.OldItems)
                item.PropertyChanged -= Track_PropertyChanged;
        UpdateFilteredLibrary();
        OnPropertyChanged(nameof(HasNoTracks));
        OnPropertyChanged(nameof(HasNoSearchMatches));
        SaveLibraryThrottled();
    }

    private void Track_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Title" || e.PropertyName == "Artist")
        {
            Status = "Updated: " + ((sender as TrackItem)?.Title ?? "Unknown");
            SaveLibraryThrottled();
        }
    }

    private void OpenFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import audio or video files (audio only playback)",
            Filter = "Audio and video files (*.mp3;*.wav;*.wma;*.aac;*.m4a;*.flac;*.mp4;*.mkv;*.mov;*.avi)|*.mp3;*.wav;*.wma;*.aac;*.m4a;*.flac;*.mp4;*.mkv;*.mov;*.avi|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true)
            return;

        var videoTypes = new[] { "MP4", "MKV", "MOV", "AVI", "WEBM" };
        foreach (string path in dialog.FileNames)
        {
            if (Tracks.Any(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            string ext = Path.GetExtension(path).Trim('.').ToUpperInvariant();
            var item = new TrackItem
            {
                FilePath = path,
                Title = Path.GetFileNameWithoutExtension(path),
                FileType = videoTypes.Contains(ext) ? "VIDEO (AUDIO ONLY)" : ext,
                Icon = GetFileIcon(path)
            };
            Tracks.Add(item);
            ProbeDurationAsync(item);
        }

        if (Tracks.Count > 0 && string.IsNullOrEmpty(NowPlaying.FilePath))
            SelectedTrack = Tracks.First();
        SaveLibraryThrottled();
        UpdateRpcPresence(true);
    }

    private void RemoveTrack(TrackItem? track)
    {
        if (track == null)
            return;
        bool wasCurrent = !string.IsNullOrEmpty(NowPlaying.FilePath) && string.Equals(NowPlaying.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase);
        Tracks.Remove(track);
        if (wasCurrent)
        {
            Stop();
            NowPlaying = new TrackItem { Title = "-", FileType = "", FilePath = "" };
            UpdateNowPlayingBindings();
        }
        Status = "Removed: " + track.Title;
        SaveLibraryThrottled();
        UpdateRpcPresence(true);
    }

    private void ClearLibrary()
    {
        try
        {
            Stop();
            foreach (TrackItem item in Tracks.ToList())
                item.PropertyChanged -= Track_PropertyChanged;
            Tracks.Clear();
            NowPlaying = new TrackItem { Title = "-", FileType = "", FilePath = "" };
            UpdateNowPlayingBindings();
            UpdateFilteredLibrary();
            Status = "Library cleared";
            SaveLibrary();
            UpdateRpcPresence(true);
        }
        catch (Exception ex)
        {
            Status = "Clear failed: " + ex.Message;
        }
    }

    private void PlayPause()
    {
        try
        {
            if (string.IsNullOrEmpty(NowPlaying.FilePath) || !File.Exists(NowPlaying.FilePath))
            {
                if (Tracks.Count > 0)
                    SelectedTrack = Tracks.First();
                else
                    Status = "No track selected.";
                return;
            }

            if (_isPlaying)
            {
                _playback.Pause();
                _isPlaying = false;
                Status = "Paused.";
            }
            else
            {
                if (!_playback.HasTrack)
                    LoadAndPlay(NowPlaying, true);
                else
                {
                    _playback.Play();
                    _isPlaying = true;
                }
                Status = "Playing: " + NowPlaying.Title;
            }
            RefreshUI();
            SaveLibraryThrottled();
            UpdateRpcPresence(true);
        }
        catch (Exception ex)
        {
            Status = "Play failed: " + ex.Message;
        }
    }

    private void LoadAndPlay(TrackItem item, bool autoPlay)
    {
        try
        {
            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
            {
                Status = "File not found.";
                return;
            }
            if (!_playback.Load(item.FilePath))
            {
                Status = "Failed to load: " + item.Title;
                return;
            }
            NowPlaying = item;
            if (_playback.Duration.TotalSeconds > 0)
            {
                item.Duration = _playback.Duration;
                NowPlayingDurationSeconds = _playback.Duration.TotalSeconds;
            }
            _positionSeconds = 0;
            OnPropertyChanged(nameof(PositionSeconds));
            UpdateNowPlayingBindings();

            if (autoPlay)
            {
                _playback.Play();
                _isPlaying = true;
                Status = "Playing: " + item.Title;
            }
            else
            {
                _isPlaying = false;
                Status = "Ready: " + item.Title;
            }
            RefreshUI();
            SaveLibraryThrottled();
            UpdateRpcPresence(true);
        }
        catch (Exception ex)
        {
            Status = $"Failed to load: {item.Title} ({ex.Message})";
        }
    }

    private void Next()
    {
        if (Tracks.Count == 0)
            return;
        int index;
        if (IsShuffling)
        {
            if (Tracks.Count == 1)
            {
                LoadAndPlay(Tracks[0], true);
                return;
            }
            do
            {
                index = _rng.Next(Tracks.Count);
            }
            while (Tracks[index] == SelectedTrack);
        }
        else
        {
            index = ((SelectedTrack != null ? Tracks.IndexOf(SelectedTrack) : -1) + 1) % Tracks.Count;
        }
        SelectedTrack = Tracks[index];
    }

    private void Previous()
    {
        if (Tracks.Count == 0)
            return;
        if (_playback.Position > TimeSpan.FromSeconds(3) && NowPlaying != null && !string.IsNullOrEmpty(NowPlaying.FilePath))
        {
            _playback.Position = TimeSpan.Zero;
            PositionSeconds = 0;
            return;
        }
        int index = SelectedTrack != null ? Tracks.IndexOf(SelectedTrack) : 0;
        index = (index - 1 + Tracks.Count) % Tracks.Count;
        SelectedTrack = Tracks[index];
    }

    private void Stop()
    {
        _playback.Stop();
        _isPlaying = false;
        PositionSeconds = 0.0;
        Status = "Stopped.";
        RefreshUI();
        SaveLibraryThrottled();
        UpdateRpcPresence(true);
    }

    private void RefreshUI()
    {
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(PositionString));
        OnPropertyChanged(nameof(DurationString));
    }

    private void UpdateNowPlayingBindings()
    {
        if (NowPlaying == null || string.IsNullOrEmpty(NowPlaying.FilePath))
            NowPlaying = new TrackItem { Title = "-", FileType = "", FilePath = "" };
        OnPropertyChanged(nameof(NowPlaying));
        OnPropertyChanged(nameof(NowPlayingDurationSeconds));
        OnPropertyChanged(nameof(DurationString));
        OnPropertyChanged(nameof(PositionString));
    }

    private void ProbeDurationAsync(TrackItem item)
    {
        string path = item.FilePath;
        Task.Run(() =>
        {
            try
            {
                using var reader = new NAudio.Wave.MediaFoundationReader(path);
                TimeSpan duration = reader.TotalTime;
                Application.Current?.Dispatcher.BeginInvoke(() => item.Duration = duration);
            }
            catch
            {
            }
        });
    }

    private void ConnectRpc(bool isAutoReconnect = false)
    {
        try
        {
            if (_rpcConnected && _rpcClient != null)
            {
                try { _rpcClient.ClearPresence(); } catch { }
                DisconnectRpcClient();
                Status = "RPC disconnected.";
                OnPropertyChanged(nameof(RpcButtonLabel));
                Frontend.ShowMessageBox("Discord RPC disconnected.");
                return;
            }
            Status = "Connecting to RPC...";
            DisconnectRpcClient();
            _showRpcConnectedMessage = !isAutoReconnect;
            _rpcClient = new DiscordRpcClient("1375529225230094507");
            _rpcClient.OnReady += RpcClient_OnReady;
            _rpcClient.OnError += RpcClient_OnError;
            _rpcClient.Initialize();
            UpdateRpcPresence(true);
        }
        catch (Exception ex)
        {
            DisconnectRpcClient();
            Status = "Failed to connect RPC: " + ex.Message;
            OnPropertyChanged(nameof(RpcButtonLabel));
            Frontend.ShowMessageBox("Failed to connect to RPC:\n" + ex.Message);
        }
    }

    private void RpcClient_OnReady(object? sender, ReadyMessage msg)
    {
        if (_disposed)
            return;

        _rpcConnected = true;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;

            Status = "RPC connected as " + msg.User.Username;
            OnPropertyChanged(nameof(RpcButtonLabel));
            if (_showRpcConnectedMessage)
                Frontend.ShowMessageBox("Discord RPC connected as " + msg.User.Username + ".\nThis makes Roblox RPC not display on your Profile!");
            UpdateRpcPresence(true);
        });
    }

    private void RpcClient_OnError(object? sender, ErrorMessage msg)
    {
        if (_disposed)
            return;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;

            _rpcConnected = false;
            Status = "RPC error: " + msg.Message;
            OnPropertyChanged(nameof(RpcButtonLabel));
        });
    }

    private void DisconnectRpcClient()
    {
        DiscordRpcClient? client = _rpcClient;
        _rpcClient = null;
        _rpcConnected = false;
        if (client == null)
            return;

        client.OnReady -= RpcClient_OnReady;
        client.OnError -= RpcClient_OnError;
        try
        {
            client.Dispose();
        }
        catch
        {
        }
    }

    private void UpdateRpcPresence(bool force = false)
    {
        if (!_rpcConnected || _rpcClient == null || !_rpcClient.IsInitialized)
            return;
        if (!force && DateTime.UtcNow - _lastRpcRefreshUtc < TimeSpan.FromSeconds(3))
            return;
        _lastRpcRefreshUtc = DateTime.UtcNow;

        RichPresence presence;
        if (NowPlaying == null || string.IsNullOrWhiteSpace(NowPlaying.Title))
        {
            presence = new RichPresence
            {
                Details = "Idle",
                State = "Voidstrap Music Player",
                Assets = new Assets
                {
                    LargeImageKey = App.WebsiteBaseUrl + "/Image/Voidstrap.png",
                    LargeImageText = "Voidstrap Music Player"
                }
            };
        }
        else
        {
            string loop = IsLooping ? " (Loop)" : string.Empty;
            double pos = Math.Max(0.0, PositionSeconds);
            double dur = Math.Max(1.0, NowPlaying.Duration.TotalSeconds);
            string state = $"{(NowPlaying.FileType ?? "FILE").ToUpperInvariant()} , {FormatTime(pos)} / {FormatTime(dur)} , {(_isPlaying ? "Playing" : "Paused")}{loop}";
            presence = new RichPresence
            {
                Details = NowPlaying.Title,
                State = state,
                Assets = new Assets
                {
                    LargeImageKey = App.WebsiteBaseUrl + "/Image/Voidstrap.png",
                    LargeImageText = "Voidstrap Music Player",
                    SmallImageKey = _isPlaying ? "play_icon" : "pause_icon",
                    SmallImageText = _isPlaying ? "Playing" : "Paused"
                }
            };
            if (_isPlaying && dur > 1.0)
            {
                DateTime now = DateTime.UtcNow;
                presence.Timestamps = new Timestamps
                {
                    Start = now - TimeSpan.FromSeconds(pos),
                    End = now + TimeSpan.FromSeconds(Math.Max(0.0, dur - pos))
                };
            }
        }
        try { _rpcClient.SetPresence(presence); } catch { }
    }

    private void SaveLibraryThrottled()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastSaveUtc < TimeSpan.FromSeconds(1.5))
            return;
        _lastSaveUtc = now;
        SaveLibrary();
    }

    private void SaveLibrary()
    {
        try
        {
            var data = new
            {
                Tracks = Tracks.Select(t => new
                {
                    t.Title,
                    t.Artist,
                    t.FilePath,
                    t.FileType,
                    Duration = Math.Max(0.0, t.Duration.TotalSeconds)
                }).ToList(),
                NowPlaying = NowPlaying?.FilePath ?? "",
                Volume = _volume,
                Position = Math.Max(0.0, _positionSeconds),
                Selected = SelectedTrack?.FilePath ?? "",
                Looping = IsLooping,
                Shuffling = IsShuffling,
                WasPlaying = _isPlaying,
                RpcConnected = _rpcConnected
            };
            string path = _savePath;
			Task.Run(() =>
            {
				try { Voidstrap.Utility.JsonFile.SerializeAtomic(path, data, Voidstrap.Utility.JsonOptions.Indented); }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Status = "Failed to save library: " + ex.Message;
        }
    }

    private void LoadLibrary()
    {
        try
        {
            if (!File.Exists(_savePath))
                return;
			JsonElement root = Voidstrap.Utility.JsonFile.Deserialize<JsonElement>(_savePath, Voidstrap.Utility.JsonOptions.Tolerant, 16777216);

            if (root.TryGetProperty("Tracks", out JsonElement tracks))
            {
                foreach (JsonElement el in tracks.EnumerateArray())
                {
                    string path = el.TryGetProperty("FilePath", out JsonElement fp) ? (fp.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        continue;
                    var item = new TrackItem
                    {
                        Title = el.TryGetProperty("Title", out JsonElement ti) ? (ti.GetString() ?? Path.GetFileNameWithoutExtension(path)) : Path.GetFileNameWithoutExtension(path),
                        Artist = el.TryGetProperty("Artist", out JsonElement ar) ? (ar.GetString() ?? "") : "",
                        FilePath = path,
                        FileType = el.TryGetProperty("FileType", out JsonElement ft) ? (ft.GetString() ?? "FILE") : "FILE",
                        Icon = GetFileIcon(path),
                        Duration = TimeSpan.FromSeconds(el.TryGetProperty("Duration", out JsonElement du) ? SafeGetDouble(du) : 0.0)
                    };
                    Tracks.Add(item);
                }
            }

            _volume = root.TryGetProperty("Volume", out JsonElement vol) ? Math.Clamp(SafeGetDouble(vol), 0.0, 1.0) : 1.0;
            _isLooping = root.TryGetProperty("Looping", out JsonElement lp) && lp.GetBoolean();
            if (root.TryGetProperty("Shuffling", out JsonElement sh))
                _isShuffling = sh.GetBoolean();
            _rpcConnected = root.TryGetProperty("RpcConnected", out JsonElement rc) && rc.GetBoolean();
            double pos = root.TryGetProperty("Position", out JsonElement ps) ? SafeGetDouble(ps) : 0.0;
            bool wasPlaying = root.TryGetProperty("WasPlaying", out JsonElement wp) && wp.GetBoolean();
            string selected = root.TryGetProperty("Selected", out JsonElement sel) ? sel.GetString() ?? "" : "";
            string last = root.TryGetProperty("NowPlaying", out JsonElement np) ? np.GetString() ?? "" : "";

            string restore = !string.IsNullOrEmpty(last) ? last : selected;
            if (!string.IsNullOrEmpty(restore))
            {
                TrackItem? found = Tracks.FirstOrDefault(t => string.Equals(t.FilePath, restore, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    _suppressAutoPlay = true;
                    SelectedTrack = found;
                    _suppressAutoPlay = false;
                    LoadAndPlay(found, false);
                    if (pos > 0 && pos < _playback.Duration.TotalSeconds)
                    {
                        _playback.Position = TimeSpan.FromSeconds(pos);
                        _positionSeconds = pos;
                        OnPropertyChanged(nameof(PositionSeconds));
                        OnPropertyChanged(nameof(PositionString));
                    }
                    if (wasPlaying)
                    {
                        _playback.Play();
                        _isPlaying = true;
                        RefreshUI();
                    }
                }
            }

            if (_rpcConnected)
                ConnectRpc(true);

            OnPropertyChanged(nameof(IsLooping));
            OnPropertyChanged(nameof(LoopLabel));
            OnPropertyChanged(nameof(IsShuffling));
            OnPropertyChanged(nameof(ShuffleLabel));
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(VolumePercent));
            Status = $"Loaded {Tracks.Count} tracks.";
        }
        catch (Exception ex)
        {
            Status = "Failed to load library: " + ex.Message;
        }
    }

    private void SaveEqualizer()
    {
        try
        {
            var data = new
            {
                Enabled = _eqEnabled,
                Preset = _selectedEqPreset,
                Bands = EqBands.Select(b => b.Gain).ToArray()
            };
            string path = _eqPath;
			Task.Run(() =>
            {
				try { Voidstrap.Utility.JsonFile.SerializeAtomic(path, data, Voidstrap.Utility.JsonOptions.Indented); }
                catch { }
            });
        }
        catch
        {
        }
    }

    private void LoadEqualizer()
    {
        try
        {
            if (!File.Exists(_eqPath))
                return;
			JsonElement root = Voidstrap.Utility.JsonFile.Deserialize<JsonElement>(_eqPath, Voidstrap.Utility.JsonOptions.Tolerant, 4194304);
            _eqEnabled = root.TryGetProperty("Enabled", out JsonElement en) && en.GetBoolean();
            _playback.EqualizerEnabled = _eqEnabled;
            if (root.TryGetProperty("Preset", out JsonElement pr))
                _selectedEqPreset = pr.GetString() ?? "Flat";
            if (root.TryGetProperty("Bands", out JsonElement bands))
            {
                int i = 0;
                _applyingPreset = true;
                foreach (JsonElement g in bands.EnumerateArray())
                {
                    if (i >= EqBands.Count)
                        break;
                    double gain = SafeGetDouble(g);
                    EqBands[i].SetGainSilent(gain);
                    _playback.SetBandGain(i, (float)gain);
                    i++;
                }
                _applyingPreset = false;
            }
        }
        catch
        {
            _applyingPreset = false;
        }
    }

    private static double SafeGetDouble(JsonElement el)
    {
        try { return el.GetDouble(); }
        catch { return 0.0; }
    }

    private static ImageSource? GetFileIcon(string path)
    {
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null)
                return null;
            using Bitmap bitmap = icon.ToBitmap();
            nint hbitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                try { NativeMethods.DeleteObject(hbitmap); } catch { }
            }
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0.5)
            return "0:00";
        TimeSpan ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1.0)
            return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
        return $"{ts.Minutes}:{ts.Seconds:00}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SaveLibrary();
        SaveEqualizer();
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _playback.PlaybackEnded -= Playback_Ended;
        _playback.PlayStateChanged -= Playback_StateChanged;
        try
        {
            _playback.Dispose();
        }
        catch
        {
        }
        Tracks.CollectionChanged -= Tracks_CollectionChanged;
        foreach (TrackItem track in Tracks)
        {
            try { track.PropertyChanged -= Track_PropertyChanged; } catch { }
        }
        CancellationTokenSource? searchCts = _searchCts;
        _searchCts = null;
        if (searchCts != null)
        {
            try
            {
                searchCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            searchCts.Dispose();
        }
        if (_rpcConnected && _rpcClient != null)
        {
            try
            {
                _rpcClient.ClearPresence();
            }
            catch
            {
            }
        }
        DisconnectRpcClient();
        GC.SuppressFinalize(this);
    }
}
