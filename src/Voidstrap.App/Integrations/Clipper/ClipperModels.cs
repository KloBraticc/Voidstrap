using System;
using System.Text.Json.Serialization;

namespace Voidstrap.Integrations.Clipper;

public enum ClipperRecordingMode
{
    Clip = 0,
    Record = 1
}

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
                return "00:00";
            TimeSpan ts = TimeSpan.FromMilliseconds(DurationMs);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
    }

    public string FormattedDate => CreatedLocal.ToString("MMM d, h:mm tt");
}

public sealed class NativeClipperConfig
{
    [JsonPropertyName("ffmpeg")]
    public string Ffmpeg { get; set; } = "ffmpeg";

    [JsonPropertyName("bufferSeconds")]
    public int BufferSeconds { get; set; } = 30;

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 60;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 1080;

    [JsonPropertyName("quality")]
    public int Quality { get; set; } = 2;

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "h264";

    [JsonPropertyName("gameAudio")]
    public bool GameAudio { get; set; } = true;

    [JsonPropertyName("microphone")]
    public bool Microphone { get; set; } = false;

    [JsonPropertyName("cursor")]
    public bool Cursor { get; set; } = false;

    [JsonPropertyName("window")]
    public long Window { get; set; }
}

public sealed class NativeClipperStatus
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("recording")]
    public bool Recording { get; set; }

    [JsonPropertyName("recordingMs")]
    public long RecordingMs { get; set; }

    [JsonPropertyName("recordingBytes")]
    public long RecordingBytes { get; set; }

    [JsonPropertyName("encoderId")]
    public string EncoderId { get; set; } = string.Empty;

    [JsonPropertyName("encoderLabel")]
    public string EncoderLabel { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; set; }

    [JsonPropertyName("bufferedMs")]
    public long BufferedMs { get; set; }

    [JsonPropertyName("bufferSeconds")]
    public int BufferSeconds { get; set; }

    [JsonPropertyName("bufferedBytes")]
    public long BufferedBytes { get; set; }

    [JsonPropertyName("lastError")]
    public string LastError { get; set; } = string.Empty;
}
