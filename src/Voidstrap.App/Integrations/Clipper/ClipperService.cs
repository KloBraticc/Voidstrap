using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Clipper;

public sealed class SavedClipInfo
{
    public string FullPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public DateTime CreatedLocal { get; init; }
    public long SizeBytes { get; init; }
    public long DurationMs { get; set; }
    public string ThumbnailPath { get; set; } = string.Empty;

    public string FormattedSize
    {
        get
        {
            double mb = SizeBytes / (1024.0 * 1024.0);
            return mb >= 100 ? $"{mb:0} MB" : $"{mb:0.0} MB";
        }
    }

    public string FormattedDuration
    {
        get
        {
            if (DurationMs <= 0)
                return "--:--";
            TimeSpan ts = TimeSpan.FromMilliseconds(DurationMs);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    public string FormattedDate => CreatedLocal.ToString("MMM d, h:mm tt");
}

public static class ClipperService
{
    private static readonly SemaphoreSlim OpGate = new(1, 1);
    private static string _activeGameName = "Roblox";
    private static long _activeWindowHandle;
    private static bool _inGame;
    private static string _provisionStatus = string.Empty;

    public static event Action<SavedClipInfo>? ClipSaved;
    public static event Action? StateChanged;

    public static string ProvisionStatus => _provisionStatus;

    public static bool InGame => _inGame;

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

    public static void NotifyGameSession(bool inGame, string? gameName = null, long windowHandle = 0)
    {
        _inGame = inGame;
        if (!string.IsNullOrWhiteSpace(gameName))
            _activeGameName = gameName;
        if (windowHandle != 0)
            _activeWindowHandle = windowHandle;

        if (inGame && App.Settings.Prop.ClipperEnabled)
        {
            _ = StartOrReconfigureAsync(_activeWindowHandle, _activeGameName);
        }
        else if (!inGame)
        {
            Stop();
        }
    }

    public static void ReconcileWithSettings()
    {
        if (!App.Settings.Prop.ClipperEnabled)
        {
            Stop();
            return;
        }
        if (_inGame)
        {
            _ = StartOrReconfigureAsync(_activeWindowHandle, _activeGameName);
        }
        else
        {
            ClipperIndicatorOverlayWindow.Reconcile();
            RaiseStateChanged();
        }
    }

    public static async Task<bool> StartOrReconfigureAsync(long windowHandle = 0, string? gameName = null)
    {
        if (!string.IsNullOrWhiteSpace(gameName))
            _activeGameName = gameName;
        if (windowHandle != 0)
            _activeWindowHandle = windowHandle;

        if (!App.Settings.Prop.ClipperEnabled)
        {
            Stop();
            return false;
        }

        await OpGate.WaitAsync().ConfigureAwait(false);
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
                App.Logger.WriteLine("ClipperService", "FFmpeg is not available; clipper cannot start");
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
            ClipperIndicatorOverlayWindow.Reconcile();
            RaiseStateChanged();
            return ok;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("ClipperService::StartOrReconfigureAsync", ex);
            return false;
        }
        finally
        {
            OpGate.Release();
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
        ClipperIndicatorOverlayWindow.Reconcile();
        RaiseStateChanged();
    }

    public static async Task<(bool Ok, SavedClipInfo? Clip, string Error)> SaveClipAsync(int? secondsOverride = null, string? gameNameOverride = null)
    {
        await OpGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string dir = GetClipsDirectory();
            Directory.CreateDirectory(dir);

            string rawTitle = !string.IsNullOrWhiteSpace(gameNameOverride) ? gameNameOverride : _activeGameName;
            string safeTitle = SanitizeFileSegment(rawTitle);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string fileName = $"{safeTitle}_{stamp}.mp4";
            string fullPath = Path.Combine(dir, fileName);
            int counter = 2;
            while (File.Exists(fullPath))
            {
                fileName = $"{safeTitle}_{stamp}_{counter}.mp4";
                fullPath = Path.Combine(dir, fileName);
                counter++;
            }

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
        string title = rawName.Replace('_', ' ');
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
        char[] chars = value.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
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
