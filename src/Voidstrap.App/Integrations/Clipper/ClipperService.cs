using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Clipper;

public static class ClipperService
{
    private static readonly SemaphoreSlim OpGate = new(1, 1);
    private static string _activeGameName = "Roblox";
    private static long _activeWindowHandle;
    private static bool _inGame;
    private static bool _clientRunning;
    private static DateTime _lastProcessCheckUtc = DateTime.MinValue;
    private static bool _lastProcessAlive;
    private static string _provisionStatus = string.Empty;
    private static NativeClipperConfig? _appliedConfig;

    public static event Action<SavedClipInfo>? ClipSaved;
    public static event Action? StateChanged;

    public static string ProvisionStatus => _provisionStatus;

    public static bool InGame => _inGame || _clientRunning || IsRobloxProcessAlive();

    public static ClipperRecordingMode CurrentMode =>
        string.Equals(App.Settings.Prop.ClipperMode, "clip", StringComparison.OrdinalIgnoreCase)
            ? ClipperRecordingMode.Clip
            : ClipperRecordingMode.Record;

    public static bool IsRecording => NativeClipper.GetStatus().Recording;

    private static bool IsRobloxProcessAlive()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastProcessCheckUtc).TotalMilliseconds < 1500)
            return _lastProcessAlive;
        _lastProcessCheckUtc = now;
        try
        {
            Process[] procs = Process.GetProcessesByName(Voidstrap.Utility.Platform.RobloxPlayerProcessName);
            _lastProcessAlive = procs.Length > 0;
            foreach (Process p in procs)
                p.Dispose();
        }
        catch
        {
            _lastProcessAlive = false;
        }
        return _lastProcessAlive;
    }

    public static string GetClipsDirectory()
    {
        string custom = App.Settings.Prop.ClipperOutputFolder;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                Directory.CreateDirectory(custom);
                return custom;
            }
            catch
            {
            }
        }

        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string fallback = string.IsNullOrWhiteSpace(videos)
            ? Path.Combine(Paths.DocumentsData, "Clips")
            : Path.Combine(videos, "Voidstrap Clips");
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch
        {
        }
        return fallback;
    }

    public static NativeClipperStatus GetStatus() => NativeClipper.GetStatus();

    public static void NotifyClientRunning(bool running)
    {
        _clientRunning = running;
        if (running)
        {
            if (App.Settings.Prop.ClipperRecordOnGameStart)
            {
                _ = StartRecordingAsync(_activeGameName);
            }
            else if (App.Settings.Prop.ClipperEnabled)
            {
                _ = StartOrReconfigureAsync(_activeWindowHandle, _activeGameName);
            }
        }
        else if (!_inGame)
        {
            _ = StopSessionAndFinalizeRecordingAsync();
        }
    }

    public static void NotifyGameSession(bool inGame, string? gameName = null, long windowHandle = 0)
    {
        _inGame = inGame;
        if (!string.IsNullOrWhiteSpace(gameName))
            _activeGameName = gameName;
        if (windowHandle != 0)
            _activeWindowHandle = windowHandle;

        if (inGame)
        {
            if (App.Settings.Prop.ClipperRecordOnGameStart)
            {
                _ = StartRecordingAsync(_activeGameName);
            }
            else if (App.Settings.Prop.ClipperEnabled)
            {
                _ = StartOrReconfigureAsync(_activeWindowHandle, _activeGameName);
            }
        }
        else
        {
            _ = StopSessionAndFinalizeRecordingAsync();
        }
    }

    private static async Task StopSessionAndFinalizeRecordingAsync()
    {
        if (GetStatus().Recording)
        {
            await StopRecordingAsync(_activeGameName).ConfigureAwait(false);
        }
        if (!_inGame && !_clientRunning)
        {
            Stop();
        }
        else
        {
            ClipperIndicatorOverlayWindow.Reconcile();
            RaiseStateChanged();
        }
    }

    public static void ReconcileWithSettings()
    {
        if (!App.Settings.Prop.ClipperEnabled)
        {
            if (GetStatus().Recording)
            {
                _ = StopRecordingAndDisableAsync();
                return;
            }
            Stop();
            return;
        }

        ClipperIndicatorOverlayWindow.Reconcile();
        RaiseStateChanged();

        NativeClipperStatus st = GetStatus();
        if (st.Running && _appliedConfig != null && ConfigMatchesEncoderSettings(_appliedConfig))
        {
            return;
        }

        if (st.Recording)
        {
            return;
        }

        if (InGame || st.Running)
        {
            _ = StartOrReconfigureAsync(_activeWindowHandle, _activeGameName, forceRun: true);
        }
    }

    private static async Task StopRecordingAndDisableAsync()
    {
        await StopRecordingAsync(_activeGameName).ConfigureAwait(false);
        Stop();
    }

    private static bool ConfigMatchesEncoderSettings(NativeClipperConfig cfg)
    {
        var prop = App.Settings.Prop;
        int buf = Math.Clamp(prop.ClipperBufferSeconds, 5, 300);
        int fps = Math.Clamp(prop.ClipperFps, 15, 240);
        int height = Math.Max(0, prop.ClipperResolutionHeight);
        int quality = Math.Clamp(prop.ClipperQuality, 0, 3);
        string codec = string.Equals(prop.ClipperCodec, "hevc", StringComparison.OrdinalIgnoreCase) ? "hevc" : "h264";

        return cfg.BufferSeconds == buf
            && cfg.Fps == fps
            && cfg.Height == height
            && cfg.Quality == quality
            && string.Equals(cfg.Codec, codec, StringComparison.OrdinalIgnoreCase)
            && cfg.GameAudio == prop.ClipperGameAudio
            && cfg.Microphone == prop.ClipperMicrophone
            && cfg.Cursor == prop.ClipperCaptureCursor;
    }

    public static async Task<bool> StartOrReconfigureAsync(long windowHandle = 0, string? gameName = null, bool forceRun = false)
    {
        if (!string.IsNullOrWhiteSpace(gameName))
            _activeGameName = gameName;
        if (windowHandle != 0)
            _activeWindowHandle = windowHandle;

        if (!App.Settings.Prop.ClipperEnabled && !forceRun)
        {
            Stop();
            return false;
        }

        await OpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StartSessionInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            OpGate.Release();
        }
    }

    private static async Task<bool> StartSessionInternalAsync()
    {
        try
        {
            string? ffmpeg = await FfmpegProvisioner.EnsureAvailableAsync(status =>
            {
                _provisionStatus = status;
                RaiseStateChanged();
            }).ConfigureAwait(false);

            _provisionStatus = string.Empty;
            if (string.IsNullOrEmpty(ffmpeg))
            {
                App.Logger.WriteLine("ClipperService", "FFmpeg is not available, clipper cannot start");
                RaiseStateChanged();
                return false;
            }

            var cfg = new NativeClipperConfig
            {
                Ffmpeg = ffmpeg,
                BufferSeconds = Math.Clamp(App.Settings.Prop.ClipperBufferSeconds, 5, 300),
                Fps = Math.Clamp(App.Settings.Prop.ClipperFps, 15, 240),
                Height = Math.Max(0, App.Settings.Prop.ClipperResolutionHeight),
                Quality = Math.Clamp(App.Settings.Prop.ClipperQuality, 0, 3),
                Codec = string.Equals(App.Settings.Prop.ClipperCodec, "hevc", StringComparison.OrdinalIgnoreCase) ? "hevc" : "h264",
                GameAudio = App.Settings.Prop.ClipperGameAudio,
                Microphone = App.Settings.Prop.ClipperMicrophone,
                Cursor = App.Settings.Prop.ClipperCaptureCursor,
                Window = _activeWindowHandle
            };

            bool ok = await Task.Run(() => NativeClipper.Start(cfg, out _)).ConfigureAwait(false);
            if (ok)
                _appliedConfig = cfg;
            ClipperIndicatorOverlayWindow.Reconcile();
            RaiseStateChanged();
            return ok;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("ClipperService::StartSessionInternalAsync", ex);
            return false;
        }
    }

    public static void Stop()
    {
        try
        {
            NativeClipper.Stop();
        }
        catch
        {
        }
        _appliedConfig = null;
        ClipperIndicatorOverlayWindow.Reconcile();
        RaiseStateChanged();
    }

    public static async Task<(bool Ok, string Error)> StartRecordingAsync(string? gameNameOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(gameNameOverride))
            _activeGameName = gameNameOverride;

        if (!App.Settings.Prop.ClipperEnabled)
        {
            App.Settings.Prop.ClipperEnabled = true;
            App.Settings.SaveDeferred();
        }

        await OpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            NativeClipperStatus st = NativeClipper.GetStatus();
            if (st.Recording)
                return (true, string.Empty);

            if (!st.Running)
            {
                bool started = await StartSessionInternalAsync().ConfigureAwait(false);
                if (!started)
                    return (false, NativeClipper.GetLastError());

                for (int i = 0; i < 15; i++)
                {
                    if (NativeClipper.GetStatus().Running)
                        break;
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }

            string dir = GetClipsDirectory();
            Directory.CreateDirectory(dir);
            string tempTs = Path.Combine(dir, $".active_recording_{Environment.ProcessId}.ts");
            try
            {
                if (File.Exists(tempTs))
                    File.Delete(tempTs);
            }
            catch
            {
            }

            string err = string.Empty;
            bool ok = await Task.Run(() => NativeClipper.StartRecording(tempTs, out err)).ConfigureAwait(false);
            ClipperIndicatorOverlayWindow.Reconcile();
            RaiseStateChanged();
            return (ok, err);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("ClipperService::StartRecordingAsync", ex);
            return (false, ex.Message);
        }
        finally
        {
            OpGate.Release();
        }
    }

    public static async Task<(bool Ok, SavedClipInfo? Clip, string Error)> StopRecordingAsync(string? gameNameOverride = null)
    {
        await OpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            NativeClipperStatus st = NativeClipper.GetStatus();
            if (!st.Recording)
                return (false, null, "Recording is not active");

            if (st.RecordingBytes == 0 && st.BufferedBytes == 0)
            {
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    st = NativeClipper.GetStatus();
                    if (st.RecordingBytes > 0 || st.BufferedBytes > 0)
                        break;
                }
            }

            string fullPath = AllocateOutputFilePath(gameNameOverride, isFullRecording: true);
            string stopErr = string.Empty;
            bool ok = await Task.Run(() => NativeClipper.StopRecording(fullPath, out stopErr)).ConfigureAwait(false);
            if (!ok || !File.Exists(fullPath))
            {
                ClipperIndicatorOverlayWindow.Reconcile();
                RaiseStateChanged();
                return (false, null, string.IsNullOrWhiteSpace(stopErr) ? "Could not finalize recording" : stopErr);
            }

            SavedClipInfo info = await Task.Run(() => BuildClipInfo(fullPath, ensureMetadata: true)).ConfigureAwait(false);
            ClipperIndicatorOverlayWindow.PulseSaved();
            ClipperIndicatorOverlayWindow.Reconcile();
            ClipSaved?.Invoke(info);
            RaiseStateChanged();
            return (true, info, string.Empty);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("ClipperService::StopRecordingAsync", ex);
            ClipperIndicatorOverlayWindow.Reconcile();
            RaiseStateChanged();
            return (false, null, ex.Message);
        }
        finally
        {
            OpGate.Release();
        }
    }

    public static async Task<(bool IsNowRecording, SavedClipInfo? SavedClip, string Error)> ToggleRecordingAsync(string? gameNameOverride = null)
    {
        if (GetStatus().Recording)
        {
            var (ok, clip, err) = await StopRecordingAsync(gameNameOverride).ConfigureAwait(false);
            return (false, clip, ok ? string.Empty : err);
        }
        else
        {
            var (ok, err) = await StartRecordingAsync(gameNameOverride).ConfigureAwait(false);
            return (ok, null, ok ? string.Empty : err);
        }
    }

    public static async Task<(bool Ok, SavedClipInfo? Clip, bool StartedRecording, string Error)> TriggerShortcutAsync(string? gameNameOverride = null)
    {
        NativeClipperStatus st = GetStatus();
        if (st.Recording)
        {
            var (ok, clip, err) = await StopRecordingAsync(gameNameOverride).ConfigureAwait(false);
            return (ok, clip, false, err);
        }

        if (CurrentMode == ClipperRecordingMode.Record)
        {
            var (ok, err) = await StartRecordingAsync(gameNameOverride).ConfigureAwait(false);
            return (ok, null, ok, err);
        }

        var (saveOk, savedClip, saveErr) = await SaveClipAsync(App.Settings.Prop.ClipperBufferSeconds, gameNameOverride).ConfigureAwait(false);
        return (saveOk, savedClip, false, saveErr);
    }

    public static async Task<(bool Ok, SavedClipInfo? Clip, string Error)> SaveClipAsync(int? secondsOverride = null, string? gameNameOverride = null)
    {
        await OpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            NativeClipperStatus st = NativeClipper.GetStatus();
            if (!st.Running && App.Settings.Prop.ClipperEnabled)
            {
                await StartSessionInternalAsync().ConfigureAwait(false);
            }

            string fullPath = AllocateOutputFilePath(gameNameOverride, isFullRecording: false);
            int seconds = secondsOverride ?? Math.Clamp(App.Settings.Prop.ClipperBufferSeconds, 5, 300);
            string saveError = string.Empty;
            bool ok = await Task.Run(() => NativeClipper.Save(fullPath, seconds, out saveError)).ConfigureAwait(false);
            if (!ok || !File.Exists(fullPath))
            {
                return (false, null, string.IsNullOrWhiteSpace(saveError) ? "Could not save replay clip" : saveError);
            }

            SavedClipInfo info = await Task.Run(() => BuildClipInfo(fullPath, ensureMetadata: true)).ConfigureAwait(false);
            ClipperIndicatorOverlayWindow.PulseSaved();
            ClipSaved?.Invoke(info);
            RaiseStateChanged();
            return (true, info, string.Empty);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("ClipperService::SaveClipAsync", ex);
            return (false, null, ex.Message);
        }
        finally
        {
            OpGate.Release();
        }
    }

    private static string AllocateOutputFilePath(string? gameNameOverride, bool isFullRecording)
    {
        string dir = GetClipsDirectory();
        Directory.CreateDirectory(dir);

        string rawTitle = !string.IsNullOrWhiteSpace(gameNameOverride) ? gameNameOverride : _activeGameName;
        string safeTitle = SanitizeFileSegment(rawTitle);
        string prefix = isFullRecording ? $"{safeTitle}_Rec" : safeTitle;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"{prefix}_{stamp}.mp4";
        string fullPath = Path.Combine(dir, fileName);
        int counter = 2;
        while (File.Exists(fullPath))
        {
            fileName = $"{prefix}_{stamp}_{counter}.mp4";
            fullPath = Path.Combine(dir, fileName);
            counter++;
        }
        return fullPath;
    }

    public static async Task<(bool Ok, SavedClipInfo? Clip, string Error)> TrimClipAsync(string inputPath, long startMs, long endMs, bool replaceOriginal)
    {
        if (!File.Exists(inputPath))
            return (false, null, "Source clip does not exist");

        string? ffmpeg = FfmpegProvisioner.TryFindExisting() ?? await FfmpegProvisioner.EnsureAvailableAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(ffmpeg))
            return (false, null, "FFmpeg is not available");

        await OpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string dir = Path.GetDirectoryName(inputPath) ?? GetClipsDirectory();
            string baseName = Path.GetFileNameWithoutExtension(inputPath);
            string targetPath = replaceOriginal
                ? Path.Combine(dir, $"{baseName}.trim_tmp.mp4")
                : Path.Combine(dir, $"{baseName}_trimmed_{DateTime.Now:HHmmss}.mp4");

            string trimErr = string.Empty;
            bool ok = await Task.Run(() => NativeClipper.Trim(ffmpeg, inputPath, targetPath, startMs, endMs, out trimErr)).ConfigureAwait(false);
            if (!ok || !File.Exists(targetPath))
                return (false, null, string.IsNullOrWhiteSpace(trimErr) ? "Trim failed" : trimErr);

            string finalPath = targetPath;
            if (replaceOriginal)
            {
                File.Move(targetPath, inputPath, overwrite: true);
                finalPath = inputPath;
                string oldThumb = GetThumbnailPath(finalPath);
                if (File.Exists(oldThumb))
                {
                    try { File.Delete(oldThumb); } catch { }
                }
            }

            SavedClipInfo info = await Task.Run(() => BuildClipInfo(finalPath, ensureMetadata: true)).ConfigureAwait(false);
            RaiseStateChanged();
            return (true, info, string.Empty);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("ClipperService::TrimClipAsync", ex);
            return (false, null, ex.Message);
        }
        finally
        {
            OpGate.Release();
        }
    }

    public static async Task<IReadOnlyList<SavedClipInfo>> GetSavedClipsAsync(int maxCount = 60)
    {
        return await Task.Run(() =>
        {
            string dir = GetClipsDirectory();
            if (!Directory.Exists(dir))
                return Array.Empty<SavedClipInfo>();

            List<SavedClipInfo> result = new();
            foreach (FileInfo fi in new DirectoryInfo(dir)
                .EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".trim_tmp.mp4", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Take(maxCount))
            {
                result.Add(BuildClipInfo(fi.FullName, ensureMetadata: true));
            }
            return (IReadOnlyList<SavedClipInfo>)result;
        }).ConfigureAwait(false);
    }

    private static SavedClipInfo BuildClipInfo(string fullPath, bool ensureMetadata)
    {
        FileInfo fi = new(fullPath);
        string rawName = Path.GetFileNameWithoutExtension(fi.Name);
        string title = rawName.Replace('_', ' ').Replace('-', ' ');
        string thumbPath = GetThumbnailPath(fullPath);
        long durationMs = 0;

        if (ensureMetadata)
        {
            string? ffmpeg = FfmpegProvisioner.TryFindExisting();
            if (!string.IsNullOrEmpty(ffmpeg))
            {
                durationMs = Math.Max(0, NativeClipper.ProbeDurationMs(ffmpeg, fullPath));
                if (!File.Exists(thumbPath))
                {
                    long thumbTime = durationMs > 1200 ? Math.Min(durationMs / 3, 2500) : 0;
                    NativeClipper.CreateThumbnail(ffmpeg, fullPath, thumbPath, thumbTime, 360);
                }
            }
        }

        return new SavedClipInfo
        {
            FullPath = fullPath,
            FileName = fi.Name,
            Title = title,
            CreatedLocal = fi.CreationTime,
            SizeBytes = fi.Exists ? fi.Length : 0,
            DurationMs = durationMs,
            ThumbnailPath = File.Exists(thumbPath) ? thumbPath : string.Empty
        };
    }

    private static string GetThumbnailPath(string clipPath)
    {
        string dir = Path.GetDirectoryName(clipPath) ?? GetClipsDirectory();
        string thumbDir = Path.Combine(dir, ".thumbs");
        try
        {
            Directory.CreateDirectory(thumbDir);
        }
        catch
        {
        }
        return Path.Combine(thumbDir, Path.GetFileNameWithoutExtension(clipPath) + ".jpg");
    }

    private static string SanitizeFileSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Roblox";
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) || c == '-' ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim('_');
        if (cleaned.Length > 36)
            cleaned = cleaned[..36].TrimEnd('_');
        return string.IsNullOrEmpty(cleaned) ? "Roblox" : cleaned;
    }

    private static void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch
        {
        }
    }
}
