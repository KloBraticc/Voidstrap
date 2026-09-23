using Voidstrap.Utility;
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
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DiscordRPC;
using DiscordRPC.Message;
using Microsoft.Win32;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public partial class MusicPlayerViewModel : INotifyPropertyChanged, IDisposable
{
    private static partial class NativeMethods
    {
        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DeleteObject(nint hObject);
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

    private readonly IMusicPlayback _playback = MusicPlayback.Create();
    private readonly DispatcherTimer _timer;
    private readonly string _savePath = Path.Combine(Paths.Config, "music.json");
    private readonly string _eqPath = Path.Combine(Paths.Config, "music_eq.json");

    private string _searchQuery = string.Empty;
    private string _searchTerm = string.Empty;
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
    private readonly DispatcherTimer _saveTimer;
    private readonly ListCollectionView _libraryView;
    private static readonly Dictionary<string, BitmapSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ProbeGate = new(2, 2);
    private readonly List<TrackItem> _shuffleOrder = new();
    private int _shufflePosition;
    private Task _writeChain = Task.CompletedTask;
    private bool _libraryDirty;
    private bool _equalizerDirty;
    private bool _loadingLibrary;
    private bool _rpcWanted;
    private int _positionSaveTicks;
    private int _loadGeneration;
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
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += SaveTimer_Tick;
        _libraryView = new ListCollectionView(Tracks) { Filter = MatchesSearch };

        OpenFilesCommand = new RelayCommand(OpenFiles);
        ConnectRpcCommand = new RelayCommand(() => ConnectRpc());
        PlayPauseCommand = new RelayCommand(PlayPause);
        NextCommand = new RelayCommand(Next);
        PreviousCommand = new RelayCommand(Previous);
        StopCommand = new RelayCommand(Stop);
        RemoveTrackCommand = new RelayCommand<TrackItem>(RemoveTrack);
        ToggleLoopCommand = new RelayCommand(() => { IsLooping = !IsLooping; Status = IsLooping ? "Loop on" : "Loop off"; UpdateRpcPresence(true); ScheduleSave(); });
        ToggleShuffleCommand = new RelayCommand(() => { IsShuffling = !IsShuffling; UpdateRpcPresence(true); ScheduleSave(); });
        ToggleMuteCommand = new RelayCommand(() => { IsMuted = !IsMuted; });
        ClearLibraryCommand = new RelayCommand(ClearLibrary);
        ResetEqualizerCommand = new RelayCommand(() => SelectedEqPreset = "Flat");

        LoadEqualizer();
        Tracks.CollectionChanged += Tracks_CollectionChanged;
        _playback.Volume = _isMuted ? 0.0 : _volume;
        UpdateNowPlayingBindings();
        _timer.Start();
        _ = LoadLibraryAsync();
    }

    public ObservableCollection<TrackItem> Tracks { get; } = new();

    public ICollectionView FilteredMusicLibrary => _libraryView;

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

    public bool HasNoSearchMatches => Tracks.Count > 0 && _libraryView.IsEmpty && !string.IsNullOrWhiteSpace(SearchQuery);

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

    public string RpcButtonLabel => _rpcClient != null ? "Disconnect RPC" : "Connect RPC";

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
            ScheduleSave();
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
            ScheduleSave();
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
            ScheduleSave(equalizer: true);
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
            ScheduleSave(equalizer: true);
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
            {
                _playback.Position = TimeSpan.FromSeconds(Math.Max(0.0, _positionSeconds));
                if (_isPlaying)
                    UpdateRpcPresence();
            }
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
            ScheduleSave(equalizer: true);
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
            if (++_positionSaveTicks >= 60)
            {
                _positionSaveTicks = 0;
                ScheduleSave();
            }
        }
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
        int generation = Volatile.Read(ref _loadGeneration);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_disposed || generation != _loadGeneration)
                    return;
                Exception? error = _playback.LastError;
                if (error != null)
                {
                    _isPlaying = false;
                    Status = "Playback stopped: " + error.Message;
                    RefreshUI();
                    UpdateRpcPresence(true);
                    return;
                }
                if (IsLooping && NowPlaying != null && !string.IsNullOrEmpty(NowPlaying.FilePath))
                {
                    if (LoadAndPlay(NowPlaying, true))
                        Status = "Looping: " + NowPlaying.Title;
                }
                else
                {
                    Advance(1, automatic: true);
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
        _searchTerm = _searchQuery.Trim();
        _libraryView.Refresh();
        OnPropertyChanged(nameof(HasNoTracks));
        OnPropertyChanged(nameof(HasNoSearchMatches));
    }

    private bool MatchesSearch(object item)
    {
        if (_searchTerm.Length == 0)
            return true;
        return item is TrackItem track
            && ((track.Title?.Contains(_searchTerm, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (track.Artist?.Contains(_searchTerm, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (track.FileType?.Contains(_searchTerm, StringComparison.CurrentCultureIgnoreCase) ?? false));
    }

    private void Tracks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (TrackItem item in e.NewItems)
                item.PropertyChanged += Track_PropertyChanged;
        if (e.OldItems != null)
            foreach (TrackItem item in e.OldItems)
                item.PropertyChanged -= Track_PropertyChanged;
        _shuffleOrder.Clear();
        OnPropertyChanged(nameof(HasNoTracks));
        OnPropertyChanged(nameof(HasNoSearchMatches));
        ScheduleSave();
    }

    private void Track_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Title" || e.PropertyName == "Artist")
        {
            Status = "Updated: " + ((sender as TrackItem)?.Title ?? "Unknown");
            ScheduleSave();
        }
        else if (e.PropertyName == "Duration")
        {
            ScheduleSave();
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
        HashSet<string> known = new(Tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (string path in dialog.FileNames)
        {
            if (!known.Add(path))
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
            added++;
        }
        Status = added == 1 ? "Imported 1 track" : "Imported " + added + " tracks";

        if (Tracks.Count > 0 && string.IsNullOrEmpty(NowPlaying.FilePath))
            SelectedTrack = Tracks.First();
        ScheduleSave();
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
        ScheduleSave();
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
                {
                    if (!LoadAndPlay(NowPlaying, true))
                        return;
                }
                else
                {
                    _playback.Play();
                    _isPlaying = true;
                }
                Status = "Playing: " + NowPlaying.Title;
            }
            RefreshUI();
            ScheduleSave();
            UpdateRpcPresence(true);
        }
        catch (Exception ex)
        {
            Status = "Play failed: " + ex.Message;
        }
    }

    private bool LoadAndPlay(TrackItem item, bool autoPlay)
    {
        _loadGeneration++;
        try
        {
            if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
            {
                Status = "File not found: " + item.Title;
                return false;
            }
            if (!_playback.Load(item.FilePath))
            {
                Status = "Could not play " + item.Title + (_playback.LastError == null ? string.Empty : ": " + _playback.LastError.Message);
                MarkStopped();
                return false;
            }
            NowPlaying = item;
            _positionSeconds = 0;
            OnPropertyChanged(nameof(PositionSeconds));
            OnPropertyChanged(nameof(PositionString));
            if (_playback.Duration.TotalSeconds > 0)
            {
                item.Duration = _playback.Duration;
                NowPlayingDurationSeconds = _playback.Duration.TotalSeconds;
            }
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
            ScheduleSave();
            UpdateRpcPresence(true);
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Could not play {item.Title} ({ex.Message})";
            MarkStopped();
            return false;
        }
    }

    private void MarkStopped()
    {
        _isPlaying = false;
        RefreshUI();
        UpdateRpcPresence(true);
    }

    private void Next() => Advance(1, automatic: false);

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
        Advance(-1, automatic: false);
    }

    private void Advance(int step, bool automatic)
    {
        int count = Tracks.Count;
        TrackItem? anchor = string.IsNullOrEmpty(NowPlaying?.FilePath) ? null : NowPlaying;
        for (int attempt = 0; attempt < count; attempt++)
        {
            TrackItem candidate = step > 0 && IsShuffling && count > 1 ? NextShuffled(anchor) : NextInOrder(anchor, step);
            SelectedTrack = candidate;
            if (ReferenceEquals(NowPlaying, candidate) && _playback.HasTrack)
                return;
            if (!automatic)
                return;
            anchor = candidate;
        }
        if (automatic && count > 0)
            Status = "None of the tracks in the library could be played.";
    }

    private TrackItem NextInOrder(TrackItem? anchor, int step)
    {
        int count = Tracks.Count;
        int index = anchor == null ? -1 : Tracks.IndexOf(anchor);
        if (index < 0)
            return Tracks[step > 0 ? 0 : count - 1];
        return Tracks[((index + step) % count + count) % count];
    }

    private TrackItem NextShuffled(TrackItem? anchor)
    {
        for (int guard = 0; guard <= Tracks.Count * 2; guard++)
        {
            if (_shufflePosition >= _shuffleOrder.Count)
            {
                _shuffleOrder.Clear();
                _shuffleOrder.AddRange(Tracks);
                Random.Shared.Shuffle(CollectionsMarshal.AsSpan(_shuffleOrder));
                int current = anchor == null ? -1 : _shuffleOrder.IndexOf(anchor);
                if (current >= 0 && current < _shuffleOrder.Count - 1)
                    (_shuffleOrder[current], _shuffleOrder[^1]) = (_shuffleOrder[^1], _shuffleOrder[current]);
                _shufflePosition = 0;
            }
            TrackItem candidate = _shuffleOrder[_shufflePosition++];
            if (!ReferenceEquals(candidate, anchor))
                return candidate;
        }
        return NextInOrder(anchor, 1);
    }

    private void Stop()
    {
        _playback.Stop();
        _isPlaying = false;
        PositionSeconds = 0.0;
        Status = "Stopped.";
        RefreshUI();
        ScheduleSave();
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

    private static void ProbeDurationAsync(TrackItem item)
    {
        string path = item.FilePath;
        _ = Task.Run(async () =>
        {
            await ProbeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                TimeSpan duration = MusicPlayback.ProbeDuration(path);
                if (duration > TimeSpan.Zero)
                    Application.Current?.Dispatcher.BeginInvoke(() => item.Duration = duration);
            }
            catch (Exception)
            {
            }
            finally
            {
                ProbeGate.Release();
            }
        });
    }

    private void ConnectRpc(bool isAutoReconnect = false)
    {
        try
        {
            if (!isAutoReconnect && _rpcClient != null)
            {
                try { _rpcClient.ClearPresence(); } catch { }
                DisconnectRpcClient();
                _rpcWanted = false;
                ScheduleSave();
                Status = "RPC disconnected.";
                OnPropertyChanged(nameof(RpcButtonLabel));
                Frontend.ShowMessageBox("Discord RPC disconnected.");
                return;
            }
            Status = "Connecting to RPC...";
            DisconnectRpcClient();
            if (!Voidstrap.Integrations.DiscordIpc.TryFindPipe(out int pipe))
            {
                Status = "Discord is not running.";
                OnPropertyChanged(nameof(RpcButtonLabel));
                if (!isAutoReconnect)
                    Frontend.ShowMessageBox("Discord is not running.");
                return;
            }
            _showRpcConnectedMessage = !isAutoReconnect;
            _rpcClient = new DiscordRpcClient("1375529225230094507", pipe, null, true, null);
            _rpcClient.OnReady += RpcClient_OnReady;
            _rpcClient.OnError += RpcClient_OnError;
            _rpcClient.Initialize();
            _rpcWanted = true;
            OnPropertyChanged(nameof(RpcButtonLabel));
            ScheduleSave();
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
        TrackItem? track = NowPlaying;
        if (track == null || string.IsNullOrWhiteSpace(track.FilePath) || string.IsNullOrWhiteSpace(track.Title))
        {
            presence = new RichPresence
            {
                Details = "Idle",
                State = "Nothing playing",
                Assets = new Assets
                {
                    LargeImageKey = App.ProjectLogoUrl,
                    LargeImageText = "Voidstrap Music Player"
                }
            };
        }
        else
        {
            double pos = Math.Max(0.0, PositionSeconds);
            double dur = Math.Max(1.0, track.Duration.TotalSeconds);
            string artist = string.IsNullOrWhiteSpace(track.Artist) ? string.Empty : "by " + track.Artist;
            string state = _isPlaying
                ? (artist.Length > 0 ? artist : "Playing")
                : "Paused at " + FormatTime(pos) + " of " + FormatTime(dur);
            if (IsLooping)
                state += " \u00b7 On repeat";
            presence = new RichPresence
            {
                Details = DiscordPresenceGuard.Text(track.Title),
                State = DiscordPresenceGuard.Text(state),
                Assets = new Assets
                {
                    LargeImageKey = App.ProjectLogoUrl,
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
        _rpcClient.SetPresenceSafe(presence);
    }

    private void ScheduleSave(bool equalizer = false)
    {
        if (_disposed || (_loadingLibrary && !equalizer))
            return;
        if (equalizer)
            _equalizerDirty = true;
        else
            _libraryDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        if (_libraryDirty)
            SaveLibrary();
        if (_equalizerDirty)
            SaveEqualizer();
    }

    private void QueueWrite(string path, object data)
    {
        _writeChain = _writeChain.ContinueWith(_ =>
        {
            try
            {
                Voidstrap.Utility.JsonFile.SerializeAtomic(path, data, Voidstrap.Utility.JsonOptions.Indented);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("MusicPlayer", "Could not save " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void SaveLibrary()
    {
        _libraryDirty = false;
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
                RpcConnected = _rpcWanted
            };
            QueueWrite(_savePath, data);
        }
        catch (Exception ex)
        {
            Status = "Failed to save library: " + ex.Message;
        }
    }

    private sealed record SavedTrack(string Title, string Artist, string FilePath, string FileType, double Duration);

    private sealed record SavedLibrary(List<SavedTrack> Tracks, double Volume, bool Looping, bool? Shuffling, bool RpcWanted, double Position, bool WasPlaying, string Restore);

    private SavedLibrary? ReadLibrary()
    {
        if (!File.Exists(_savePath))
            return null;
        JsonElement root = Voidstrap.Utility.JsonFile.Deserialize<JsonElement>(_savePath, Voidstrap.Utility.JsonOptions.Tolerant, 16777216);
        List<SavedTrack> tracks = new();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(nameof(Tracks), out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in list.EnumerateArray())
            {
                string path = ReadString(element, "FilePath");
                if (path.Length == 0 || !File.Exists(path))
                    continue;
                string title = ReadString(element, "Title");
                string type = ReadString(element, "FileType");
                tracks.Add(new SavedTrack(
                    title.Length > 0 ? title : Path.GetFileNameWithoutExtension(path),
                    ReadString(element, "Artist"),
                    path,
                    type.Length > 0 ? type : "FILE",
                    Math.Max(0.0, ReadNumber(element, "Duration", 0.0))));
            }
        }
        string nowPlaying = ReadString(root, nameof(NowPlaying));
        return new SavedLibrary(
            tracks,
            Math.Clamp(ReadNumber(root, nameof(Volume), 1.0), 0.0, 1.0),
            ReadBool(root, "Looping") ?? false,
            ReadBool(root, "Shuffling"),
            ReadBool(root, "RpcConnected") ?? false,
            Math.Max(0.0, ReadNumber(root, "Position", 0.0)),
            ReadBool(root, "WasPlaying") ?? false,
            nowPlaying.Length > 0 ? nowPlaying : ReadString(root, "Selected"));
    }

    private async Task LoadLibraryAsync()
    {
        SavedLibrary? saved;
        try
        {
            Status = "Loading library...";
            saved = await Task.Run(ReadLibrary);
        }
        catch (Exception ex)
        {
            Status = "Failed to load library: " + ex.Message;
            return;
        }
        if (_disposed)
            return;
        if (saved == null)
        {
            Status = "Ready";
            return;
        }

        _loadingLibrary = true;
        try
        {
            HashSet<string> known = new(Tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
            using (_libraryView.DeferRefresh())
            {
                foreach (SavedTrack track in saved.Tracks)
                {
                    if (!known.Add(track.FilePath))
                        continue;
                    Tracks.Add(new TrackItem
                    {
                        Title = track.Title,
                        Artist = track.Artist,
                        FilePath = track.FilePath,
                        FileType = track.FileType,
                        Icon = GetFileIcon(track.FilePath),
                        Duration = TimeSpan.FromSeconds(track.Duration)
                    });
                }
            }
            foreach (TrackItem track in Tracks)
            {
                if (track.Duration <= TimeSpan.Zero)
                    ProbeDurationAsync(track);
            }

            _volume = saved.Volume;
            _playback.Volume = _isMuted ? 0.0 : _volume;
            _isLooping = saved.Looping;
            if (saved.Shuffling is bool shuffling)
                _isShuffling = shuffling;
            _rpcWanted = saved.RpcWanted;

            TrackItem? found = saved.Restore.Length == 0
                ? null
                : Tracks.FirstOrDefault(t => string.Equals(t.FilePath, saved.Restore, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                _suppressAutoPlay = true;
                SelectedTrack = found;
                _suppressAutoPlay = false;
                if (LoadAndPlay(found, false))
                {
                    if (saved.Position > 0 && saved.Position < _playback.Duration.TotalSeconds)
                    {
                        _playback.Position = TimeSpan.FromSeconds(saved.Position);
                        _positionSeconds = saved.Position;
                        OnPropertyChanged(nameof(PositionSeconds));
                        OnPropertyChanged(nameof(PositionString));
                    }
                    if (saved.WasPlaying)
                    {
                        _playback.Play();
                        _isPlaying = true;
                        RefreshUI();
                    }
                }
            }

            if (_rpcWanted)
                ConnectRpc(true);

            OnPropertyChanged(nameof(IsLooping));
            OnPropertyChanged(nameof(LoopLabel));
            OnPropertyChanged(nameof(IsShuffling));
            OnPropertyChanged(nameof(ShuffleLabel));
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(VolumePercent));
            OnPropertyChanged(nameof(HasNoTracks));
            OnPropertyChanged(nameof(HasNoSearchMatches));
            Status = $"Loaded {Tracks.Count} tracks.";
        }
        catch (Exception ex)
        {
            Status = "Failed to load library: " + ex.Message;
        }
        finally
        {
            _loadingLibrary = false;
        }
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static double ReadNumber(JsonElement element, string name, double fallback)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number)
            && double.IsFinite(number)
            ? number
            : fallback;
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private void SaveEqualizer()
    {
        _equalizerDirty = false;
        var data = new
        {
            Enabled = _eqEnabled,
            Preset = _selectedEqPreset,
            Bands = EqBands.Select(b => b.Gain).ToArray()
        };
        QueueWrite(_eqPath, data);
    }

    private void LoadEqualizer()
    {
        try
        {
            if (!File.Exists(_eqPath))
                return;
            JsonElement root = Voidstrap.Utility.JsonFile.Deserialize<JsonElement>(_eqPath, Voidstrap.Utility.JsonOptions.Tolerant, 4194304);
            _eqEnabled = ReadBool(root, "Enabled") ?? false;
            _playback.EqualizerEnabled = _eqEnabled;
            string preset = ReadString(root, "Preset");
            if (preset.Length > 0)
                _selectedEqPreset = preset;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Bands", out JsonElement bands) && bands.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                _applyingPreset = true;
                foreach (JsonElement g in bands.EnumerateArray())
                {
                    if (i >= EqBands.Count)
                        break;
                    double gain = g.ValueKind == JsonValueKind.Number && g.TryGetDouble(out double value) && double.IsFinite(value) ? value : 0.0;
                    EqBands[i].SetGainSilent(gain);
                    _playback.SetBandGain(i, (float)gain);
                    i++;
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MusicPlayer", "The equalizer settings could not be loaded: " + ex.Message);
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    private static BitmapSource? GetFileIcon(string path)
    {
        string key = Path.GetExtension(path);
        if (key.Length == 0)
            key = path;
        if (IconCache.TryGetValue(key, out BitmapSource? cached))
            return cached;
        BitmapSource? icon = ExtractFileIcon(path);
        IconCache[key] = icon;
        return icon;
    }

    private static BitmapSource? ExtractFileIcon(string path)
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

    internal static string FormatTime(double seconds)
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
        _saveTimer.Stop();
        _saveTimer.Tick -= SaveTimer_Tick;
        SaveLibrary();
        SaveEqualizer();
        try
        {
            _writeChain.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
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
