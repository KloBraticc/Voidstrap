using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Keystrokes;

public static class KeystrokeAudioService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, float[]> CachedSamples = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PortableWavCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Task> DockAudioReady = new(() => Task.Run(Initialize));
    private const string DockOpenCue = "DockOpen";
    private const string DockCloseCue = "DockClose";

    private static IWavePlayer? _wavePlayer;
    private static MixingSampleProvider? _mixer;
    private static volatile bool _initialized;
    private static int _portablePlayers;
    private const int MaxPortablePlayers = 4;
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private static string? _portablePlayerExecutable;
    private static int _dockCueRequest;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
                return;

            EnsureBuiltinPacks();

            if (Voidstrap.Utility.Platform.IsWindows)
            {
                CacheDockSamples(DockOpenCue);
                CacheDockSamples(DockCloseCue);
                try
                {
                    _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
                    var waveOut = new WaveOut
                    {
                        BufferMilliseconds = 40,
                        NumberOfBuffers = 2
                    };
                    waveOut.Init(_mixer);
                    waveOut.Play();
                    _wavePlayer = waveOut;
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteLine("KeystrokeAudioService", "Audio player initialization failed: " + ex.Message);
                }
            }
            else if (Voidstrap.Utility.Platform.UsesLinuxHost || Voidstrap.Utility.Platform.IsMacOS)
            {
                _portablePlayerExecutable = ResolvePortablePlayer();
                CachePortableWavFiles();
            }

            _initialized = true;
        }
    }

    public static void PlayKeySound() => PlayKeyboardPress(false);

    public static void PlayMouseSound() => PlayMousePress(false);

    internal static void PrepareDockSounds() => _ = DockAudioReady.Value;

    internal static async Task PlayDockTransitionAsync(bool opening)
    {
        const string LOG_IDENT = "KeystrokeAudioService::PlayDockTransitionAsync";
        int request = Interlocked.Increment(ref _dockCueRequest);
        try
        {
            await DockAudioReady.Value.ConfigureAwait(false);
            if (request != Volatile.Read(ref _dockCueRequest))
                return;
            string cue = opening ? DockOpenCue : DockCloseCue;
            if (Voidstrap.Utility.Platform.IsWindows)
            {
                Play(cue, null, 1);
                return;
            }
            if (Voidstrap.Utility.Platform.UsesLinuxHost
                && await Task.Run(() => request == Volatile.Read(ref _dockCueRequest) && TryPlayLinuxDockCue(cue)).ConfigureAwait(false))
                return;
            if (request == Volatile.Read(ref _dockCueRequest))
                PlayPortable(cue, null);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Dock audio could not be played: " + ex.Message);
        }
    }

    private static bool TryPlayLinuxDockCue(string name)
    {
        if (Interlocked.Increment(ref _portablePlayers) > MaxPortablePlayers)
        {
            Interlocked.Decrement(ref _portablePlayers);
            return false;
        }
        try
        {
            using WaveFileReader wave = new(new MemoryStream(ReadDockWave(name), writable: false));
            byte[] pcm = new byte[checked((int)wave.Length)];
            return wave.Read(pcm, 0, pcm.Length) == pcm.Length
                && Voidstrap.Platform.Linux.LinuxPcmPlayback.TryPlay(
                    Strings.Ui_Mods_SessionDock, pcm, (uint)wave.WaveFormat.SampleRate, (byte)wave.WaveFormat.Channels);
        }
        finally
        {
            Interlocked.Decrement(ref _portablePlayers);
        }
    }

    public static void PlayKeyboardPress(bool force = false)
    {
        var settings = App.Settings.Prop;
        if (!force && !settings.KeystrokesKeyboardSound)
            return;

        string pack = settings.KeystrokesKeyboardSoundPack ?? "Mechanical";
        string customPath = settings.KeystrokesKeyboardCustomWav;
        double volume = Math.Clamp(settings.KeystrokesKeyboardVolume, 0.05, 1.0);
        Play(pack, customPath, volume);
    }

    public static void PlayMousePress(bool force = false)
    {
        var settings = App.Settings.Prop;
        if (!force && !settings.KeystrokesMouseSound)
            return;

        string pack = settings.KeystrokesMouseSoundPack ?? "Crisp";
        string customPath = settings.KeystrokesMouseCustomWav;
        double volume = Math.Clamp(settings.KeystrokesMouseVolume, 0.05, 1.0);
        Play(pack, customPath, volume);
    }

    public static void Play(string packName, string? customPath, double volume)
    {
        if (volume <= 0.001)
            return;

        if (!_initialized)
            Initialize();

        float[]? samples = ResolveSamples(packName, customPath);
        if (samples == null || samples.Length == 0)
            return;

        if (Voidstrap.Utility.Platform.IsWindows)
        {
            MixingSampleProvider? mixer;
            lock (Gate)
            {
                mixer = _mixer;
            }
            if (mixer != null)
            {
                try
                {
                    bool stereo = string.Equals(packName, DockOpenCue, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(packName, DockCloseCue, StringComparison.OrdinalIgnoreCase);
                    mixer.AddMixerInput(new StereoCachedSampleProvider(samples, (float)volume, stereo ? 2 : 1));
                }
                catch
                {
                }
            }
        }
        else if (Voidstrap.Utility.Platform.UsesLinuxHost)
        {
            PlayPortable(packName, customPath);
        }
    }

    private static float[]? ResolveSamples(string packName, string? customPath)
    {
        lock (Gate)
        {
            if (string.Equals(packName, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(customPath) || !File.Exists(customPath))
                    return CachedSamples.TryGetValue("Mechanical", out var fallback) ? fallback : null;

                string key = "custom:" + customPath;
                if (!CachedSamples.TryGetValue(key, out var customSamples))
                {
                    customSamples = LoadWavFile(customPath);
                    if (customSamples != null)
                        CachedSamples[key] = customSamples;
                }
                return customSamples ?? (CachedSamples.TryGetValue("Mechanical", out var def) ? def : null);
            }

            return CachedSamples.TryGetValue(packName, out var packSamples) ? packSamples : null;
        }
    }

    private static void PlayPortable(string packName, string? customPath)
    {
        string? exe = _portablePlayerExecutable ??= ResolvePortablePlayer();
        if (string.IsNullOrEmpty(exe))
            return;

        string? filePath = null;
        lock (Gate)
        {
            if (string.Equals(packName, "Custom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
                filePath = customPath;
            else if (PortableWavCache.TryGetValue(packName, out var cached))
                filePath = cached;
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        if (Interlocked.Increment(ref _portablePlayers) > MaxPortablePlayers)
        {
            Interlocked.Decrement(ref _portablePlayers);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                ProcessStartInfo start = new()
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add(filePath);
                using var proc = Process.Start(start);
                proc?.WaitForExit();
            }
            catch
            {
            }
            finally
            {
                Interlocked.Decrement(ref _portablePlayers);
            }
        });
    }

    private static string? ResolvePortablePlayer()
    {
        if (Voidstrap.Utility.Platform.IsMacOS)
            return File.Exists("/usr/bin/afplay") ? "/usr/bin/afplay" : null;

        Core.SystemProcessService processes = new();
        string[] candidates = ["pw-play", "paplay", "aplay"];
        foreach (string candidate in candidates)
        {
            try
            {
                string? path = processes.FindExecutable(candidate);
                if (path != null)
                    return path;
            }
            catch
            {
            }
        }
        return null;
    }

    private static void CachePortableWavFiles()
    {
        try
        {
            string dir = Path.Combine(Paths.Temp, "KeystrokeAudio");
            Directory.CreateDirectory(dir);
            foreach (var kvp in CachedSamples)
            {
                if (kvp.Key.StartsWith("custom:", StringComparison.Ordinal))
                    continue;
                CachePortableWavFile(dir, kvp.Key, CreateWavBytes(kvp.Value, MixFormat.SampleRate));
            }
            CachePortableWavFile(dir, DockOpenCue, ReadDockWave(DockOpenCue));
            CachePortableWavFile(dir, DockCloseCue, ReadDockWave(DockCloseCue));
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("KeystrokeAudioService", "Portable wav cache failed: " + ex.Message);
        }
    }

    private static void CachePortableWavFile(string directory, string name, byte[] bytes)
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string filePath = Path.Combine(directory, $"{name}-{hash}.wav");
        if (!File.Exists(filePath))
        {
            string temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                File.Move(temporaryPath, filePath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        PortableWavCache[name] = filePath;
    }

    private static byte[] ReadDockWave(string name)
    {
        using Stream stream = typeof(KeystrokeAudioService).Assembly.GetManifestResourceStream($"Voidstrap.Resources.SessionDock.{name}.wav")
            ?? throw new FileNotFoundException($"Missing embedded dock cue {name}");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void CacheDockSamples(string name)
    {
        const string LOG_IDENT = "KeystrokeAudioService::CacheDockSamples";
        try
        {
            using WaveFileReader wave = new(new MemoryStream(ReadDockWave(name), writable: false));
            WaveFormat format = wave.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm || format.Channels != MixFormat.Channels
                || format.SampleRate != MixFormat.SampleRate || format.BitsPerSample != 16)
                throw new InvalidDataException($"Unsupported format for dock cue {name}");
            float[] samples = new float[checked((int)(wave.Length / sizeof(short)))];
            if (wave.ToSampleProvider().Read(samples.AsSpan()) != samples.Length)
                throw new EndOfStreamException($"Incomplete dock cue {name}");
            CachedSamples[name] = samples;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Dock audio could not be loaded: " + ex.Message);
        }
    }

    private static void EnsureBuiltinPacks()
    {
        CachedSamples["Mechanical"] = SynthesizeMechanical();
        CachedSamples["Thock"] = SynthesizeThock();
        CachedSamples["Bubble"] = SynthesizeBubble();
        CachedSamples["Soft"] = SynthesizeSoft();
        CachedSamples["Crisp"] = SynthesizeCrisp();
        CachedSamples["Muted"] = SynthesizeMuted();
        CachedSamples["Thocky"] = SynthesizeThocky();
    }

    private static float[] SynthesizeMechanical()
    {
        int length = 1250;
        float[] samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / 44100.0;
            double click = Math.Sin(2.0 * Math.PI * 2700.0 * t) * Math.Exp(-t / 0.002);
            double thud = Math.Sin(2.0 * Math.PI * 175.0 * t) * Math.Exp(-t / 0.011);
            double noise = ((i % 7) - 3.5) / 3.5 * Math.Exp(-t / 0.0025);
            samples[i] = (float)(click * 0.45 + thud * 0.45 + noise * 0.1);
        }
        Normalize(samples, 0.85f);
        return samples;
    }

    private static float[] SynthesizeThock()
    {
        int length = 1600;
        float[] samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / 44100.0;
            double f1 = Math.Sin(2.0 * Math.PI * 120.0 * t) * Math.Exp(-t / 0.016);
            double f2 = Math.Sin(2.0 * Math.PI * 240.0 * t) * 0.4 * Math.Exp(-t / 0.010);
            double f3 = Math.Sin(2.0 * Math.PI * 360.0 * t) * 0.2 * Math.Exp(-t / 0.006);
            double sub = Math.Sin(2.0 * Math.PI * 70.0 * t) * 0.5 * Math.Exp(-t / 0.022);
            samples[i] = (float)(f1 + f2 + f3 + sub);
        }
        Normalize(samples, 0.85f);
        return samples;
    }

    private static float[] SynthesizeBubble()
    {
        int length = 1450;
        float[] samples = new float[length];
        double phase = 0;
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / 44100.0;
            double freq = 520.0 * Math.Exp(-t / 0.015) + 180.0;
            phase += 2.0 * Math.PI * freq / 44100.0;
            double attack = Math.Min(1.0, t / 0.002);
            double decay = Math.Exp(-t / 0.012);
            samples[i] = (float)(Math.Sin(phase) * attack * decay);
        }
        Normalize(samples, 0.85f);
        return samples;
    }

    private static float[] SynthesizeSoft()
    {
        int length = 950;
        float[] samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / 44100.0;
            double body = Math.Sin(2.0 * Math.PI * 210.0 * t) * Math.Exp(-t / 0.008);
            double noise = ((i % 11) - 5.5) / 5.5 * Math.Exp(-t / 0.004);
            samples[i] = (float)(body * 0.75 + noise * 0.25);
        }
        Normalize(samples, 0.85f);
        return samples;
    }

    private static float[] SynthesizeCrisp()
    {
        int length = 800;
        float[] samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / 44100.0;
            double click = Math.Sin(2.0 * Math.PI * 3300.0 * t) * Math.Exp(-t / 0.0016);
            double metallic = Math.Sin(2.0 * Math.PI * 5100.0 * t) * 0.28 * Math.Exp(-t / 0.001);
            double body = Math.Sin(2.0 * Math.PI * 620.0 * t) * 0.35 * Math.Exp(-t / 0.005);
            samples[i] = (float)(click + metallic + body);
        }
        Normalize(samples, 0.85f);
        return samples;
    }

    private static float[] SynthesizeMuted()
    {
        int length = 750;
        float[] samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / 44100.0;
            double body = Math.Sin(2.0 * Math.PI * 360.0 * t) * Math.Exp(-t / 0.006);
            double soft = Math.Sin(2.0 * Math.PI * 720.0 * t) * 0.15 * Math.Exp(-t / 0.003);
            samples[i] = (float)(body + soft);
        }
        Normalize(samples, 0.85f);
        return samples;
    }

    private static float[] SynthesizeThocky()
    {
        int length = 1000;
        float[] samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / 44100.0;
            double snap = Math.Sin(2.0 * Math.PI * 1850.0 * t) * Math.Exp(-t / 0.002);
            double body = Math.Sin(2.0 * Math.PI * 270.0 * t) * Math.Exp(-t / 0.009);
            samples[i] = (float)(snap * 0.5 + body * 0.6);
        }
        Normalize(samples, 0.85f);
        return samples;
    }

    private static void Normalize(float[] samples, float targetPeak)
    {
        float max = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > max)
                max = abs;
        }
        if (max > 0.0001f)
        {
            float scale = targetPeak / max;
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= scale;
        }
    }

    private static float[]? LoadWavFile(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider sampleProvider = reader;
            var samples = new List<float>();
            float[] buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            int read;
            while ((read = sampleProvider.Read(buffer.AsSpan())) > 0)
            {
                if (reader.WaveFormat.Channels == 1)
                {
                    for (int i = 0; i < read; i++)
                        samples.Add(buffer[i]);
                }
                else
                {
                    for (int i = 0; i < read; i += reader.WaveFormat.Channels)
                    {
                        float sum = 0;
                        for (int c = 0; c < reader.WaveFormat.Channels; c++)
                            sum += buffer[i + c];
                        samples.Add(sum / reader.WaveFormat.Channels);
                    }
                }
            }
            if (samples.Count == 0)
                return null;

            float[] res = samples.ToArray();
            Normalize(res, 0.85f);
            return res;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("KeystrokeAudioService", "Failed to load custom wav " + path + ": " + ex.Message);
            return null;
        }
    }

    private static byte[] CreateWavBytes(float[] samples, int sampleRate)
    {
        short[] pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            pcm[i] = (short)Math.Clamp((int)(samples[i] * 32767.0f), -32768, 32767);

        int dataSize = pcm.Length * 2;
        byte[] bytes = new byte[44 + dataSize];
        using var ms = new MemoryStream(bytes);
        using var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        for (int i = 0; i < pcm.Length; i++)
            bw.Write(pcm[i]);
        return bytes;
    }

    public static void Dispose()
    {
        lock (Gate)
        {
            if (!_initialized)
                return;

            try
            {
                _wavePlayer?.Stop();
                _wavePlayer?.Dispose();
                _wavePlayer = null;
                _mixer = null;
            }
            catch
            {
            }

            _initialized = false;
        }
    }

    private sealed class StereoCachedSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly float _volume;
        private readonly int _channels;
        private int _position;

        public StereoCachedSampleProvider(float[] samples, float volume, int channels)
        {
            _samples = samples;
            _volume = Math.Clamp(volume, 0f, 1f);
            _channels = channels;
        }

        public WaveFormat WaveFormat => MixFormat;

        public int Read(Span<float> buffer)
        {
            int available = (_samples.Length - _position) / _channels;
            int frames = Math.Min(available, buffer.Length / 2);
            for (int i = 0; i < frames; i++)
            {
                buffer[i * 2] = _samples[_position] * _volume;
                buffer[i * 2 + 1] = _samples[_position + _channels - 1] * _volume;
                _position += _channels;
            }
            return frames * 2;
        }

        public int Read(float[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));
    }
}
