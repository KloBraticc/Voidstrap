using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OggVorbisEncoder;

namespace Voidstrap.Utility;

public static class AudioGain
{
	private const string LogIdent = "AudioGain";

	private const int MaxDurationSeconds = 60;

	private const int OutputSampleRate = 48000;

	private const float EncodeQuality = 0.7f;

	private const int BlockFrames = 4096;

	private const long MaxInputBytes = 256L * 1024L * 1024L;

	private const int MfInvalidStreamNumber = unchecked((int)0xC00D36B3);

	private const int MfUnsupportedByteStream = unchecked((int)0xC00D36C4);

	private const int MfInvalidMediaType = unchecked((int)0xC00D36B4);

	private const int MfCodecNotFound = unchecked((int)0xC00D5212);

	public static bool TryApplyGain(string sourcePath, string targetPath, double gain)
	{
		return TryApplyGain(sourcePath, targetPath, gain, CancellationToken.None, out _);
	}

	public static bool TryApplyGain(string sourcePath, string targetPath, double gain, CancellationToken cancellationToken, out string error)
	{
		error = string.Empty;
		string? tempPath = null;
		try
		{
			FileInfo source = new(sourcePath);
			if (!source.Exists)
				throw new FileNotFoundException("The selected audio file no longer exists", sourcePath);
			if (source.Length == 0)
				throw new InvalidDataException("The selected audio file is empty");
			if (source.Length > MaxInputBytes)
				throw new InvalidDataException("The selected audio file is larger than 256 MB");

			string? folder = Path.GetDirectoryName(targetPath);
			if (!string.IsNullOrEmpty(folder))
				Directory.CreateDirectory(folder);

			gain = Math.Clamp(gain, 0.01, 8.0);
			tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			Encode(sourcePath, tempPath, gain, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			Filesystem.AssertReadOnly(targetPath);
			File.Move(tempPath, targetPath, overwrite: true);
			tempPath = null;
			App.Logger?.WriteLine(LogIdent, "Converted the death sound at " + (int)Math.Round(gain * 100.0) + " percent volume");
			return true;
		}
		catch (OperationCanceledException)
		{
			error = "Audio conversion was cancelled";
			App.Logger?.WriteLine(LogIdent, error);
			return false;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			App.Logger?.WriteLine(LogIdent, "Could not convert the audio: " + ex.Message);
			return false;
		}
		finally
		{
			if (!string.IsNullOrEmpty(tempPath))
			{
				try
				{
					File.Delete(tempPath);
				}
				catch
				{
				}
			}
		}
	}

	private static float Limit(float sample)
	{
		const float knee = 0.8f;
		const float range = 1f - knee;
		float magnitude = Math.Abs(sample);
		if (magnitude <= knee)
			return sample;
		float shaped = knee + (range * MathF.Tanh((magnitude - knee) / range));
		return sample < 0f ? -shaped : shaped;
	}

	private static void Encode(string sourcePath, string targetPath, double gain, CancellationToken cancellationToken)
	{
		using WaveStream reader = OpenReader(sourcePath);
		if (reader.WaveFormat.Channels is < 1 or > 32)
			throw new InvalidDataException("The audio channel count is not supported");
		if (reader.WaveFormat.SampleRate is < 4000 or > 384000)
			throw new InvalidDataException("The audio sample rate is not supported");

		ISampleProvider provider = reader.ToSampleProvider();
		if (provider.WaveFormat.SampleRate != OutputSampleRate)
			provider = new WdlResamplingSampleProvider(provider, OutputSampleRate);

		int inputChannels = provider.WaveFormat.Channels;
		int outputChannels = inputChannels == 1 ? 1 : 2;
		VorbisInfo info = VorbisInfo.InitVariableBitRate(outputChannels, OutputSampleRate, EncodeQuality);
		OggStream oggStream = new(1);
		oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
		oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
		oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

		using FileStream output = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
		while (oggStream.PageOut(out OggPage headerPage, force: true))
			WritePage(output, headerPage);

		ProcessingState state = ProcessingState.Create(info);
		float[] interleaved = new float[BlockFrames * inputChannels];
		float[][] channelData = new float[outputChannels][];
		for (int channel = 0; channel < outputChannels; channel++)
			channelData[channel] = new float[BlockFrames];

		int totalFrames = 0;
		int maxFrames = OutputSampleRate * MaxDurationSeconds;
		float scale = (float)gain;
		while (totalFrames < maxFrames)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int requestedSamples = Math.Min(interleaved.Length, (maxFrames - totalFrames) * inputChannels);
			int samplesRead = provider.Read(interleaved.AsSpan(0, requestedSamples));
			int framesRead = samplesRead / inputChannels;
			if (framesRead == 0)
				break;

			ConvertChannels(interleaved, channelData, framesRead, inputChannels, scale);
			state.WriteData(channelData, framesRead, 0);
			Drain(output, oggStream, state, force: false);
			totalFrames += framesRead;
		}

		if (totalFrames == 0)
			throw new InvalidDataException("The selected audio file contains no playable samples");

		state.WriteEndOfStream();
		Drain(output, oggStream, state, force: true);
		output.Flush(flushToDisk: true);
	}

	private static WaveStream OpenReader(string path)
	{
		byte[] head = ReadHeader(path);
		bool ogg = StartsWith(head, "OggS");
		bool opus = ogg && Contains(head, "OpusHead");
		bool wave = StartsWith(head, "RIFF") && Matches(head, 8, "WAVE");
		bool aiff = StartsWith(head, "FORM") && (Matches(head, 8, "AIFF") || Matches(head, 8, "AIFC"));
		bool mpeg = StartsWith(head, "ID3") || (head.Length > 1 && head[0] == 0xFF && (head[1] & 0xE6) == 0xE2);
		string extension = Path.GetExtension(path).ToLowerInvariant();

		List<(string Name, Func<WaveStream> Create)> readers = [];
		void Add(string name, Func<WaveStream> create) => readers.Add((name, create));
		if (ogg && !opus)
			Add("vorbis", () => new VorbisWaveStream(path));
		if (opus)
			Add("opus", () => new OpusWaveStream(path));
		if (wave)
		{
			Add("wave", () => new WaveFileReader(path));
			Add("wave codec", () => ConvertToPcm(new WaveFileReader(path)));
		}
		if (aiff)
			Add("aiff", () => new AiffFileReader(path));
		if (mpeg)
			Add("mpeg", () => new Mp3FileReader(path));
		if (!ogg)
			Add("media foundation", () => new MediaFoundationReader(path));
		Add("media foundation stream", () => OpenMediaFoundationStream(path));
		if (!ogg && extension is ".ogg" or ".oga")
			Add("vorbis", () => new VorbisWaveStream(path));
		if (!wave)
			Add("wave", () => new WaveFileReader(path));
		if (!aiff)
			Add("aiff", () => new AiffFileReader(path));
		if (!mpeg)
			Add("mpeg", () => new Mp3FileReader(path));

		Exception? firstError = null;
		bool noAudioTrack = false;
		foreach ((string name, Func<WaveStream> createReader) in readers)
		{
			WaveStream? reader = null;
			try
			{
				reader = createReader();
				_ = reader.ToSampleProvider();
				if (reader.WaveFormat.Channels > 0 && reader.WaveFormat.SampleRate > 0 && HasAudio(reader))
				{
					App.Logger?.WriteLine(LogIdent, "Decoding " + Path.GetFileName(path) + " with the " + name + " reader");
					return reader;
				}
				reader.Dispose();
			}
			catch (Exception ex)
			{
				reader?.Dispose();
				firstError ??= ex;
				noAudioTrack |= ex.HResult == MfInvalidStreamNumber;
				App.Logger?.WriteLine(LogIdent, "The " + name + " reader could not open " + Path.GetFileName(path) + ": " + ex.Message.Split('\n')[0]);
			}
		}
		if (head.Length == 0)
			throw new InvalidDataException("The selected file is empty");
		if (noAudioTrack)
			throw new InvalidDataException("This file has no sound in it. It is probably a video saved without its audio track, pick a file that plays sound.", firstError);
		throw new InvalidDataException(DescribeFailure(firstError), firstError);
	}

	private static string DescribeFailure(Exception? error)
	{
		return error?.HResult switch
		{
			MfUnsupportedByteStream => "This file is not a sound file Voidstrap can read. Pick an MP3, OGG, WAV, M4A or FLAC file.",
			MfInvalidMediaType or MfCodecNotFound => "Windows has no decoder for the sound in this file. Convert it to MP3 or OGG, then add it again.",
			_ => "This file is not an audio format Windows can decode" + (error == null ? string.Empty : ", " + error.Message.Split('\n')[0])
		};
	}

	private static WaveStream ConvertToPcm(WaveStream source)
	{
		try
		{
			return WaveFormatConversionStream.CreatePcmStream(source);
		}
		catch
		{
			source.Dispose();
			throw;
		}
	}

	private static bool HasAudio(WaveStream reader)
	{
		if (!reader.CanSeek)
			return true;
		byte[] probe = new byte[Math.Max(reader.WaveFormat.BlockAlign, 1) * 64];
		int read = reader.Read(probe, 0, probe.Length);
		reader.Position = 0;
		return read > 0;
	}

	private static OwnedStreamMediaFoundationReader OpenMediaFoundationStream(string path)
	{
		FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
		try
		{
			return new OwnedStreamMediaFoundationReader(stream);
		}
		catch
		{
			stream.Dispose();
			throw;
		}
	}

	private sealed class OwnedStreamMediaFoundationReader : StreamMediaFoundationReader
	{
		private readonly Stream _stream;

		public OwnedStreamMediaFoundationReader(Stream stream)
			: base(stream)
		{
			_stream = stream;
		}

		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
			if (disposing)
				_stream.Dispose();
		}
	}

	private static byte[] ReadHeader(string path)
	{
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			byte[] buffer = new byte[64];
			int read = stream.Read(buffer, 0, buffer.Length);
			return read == buffer.Length ? buffer : buffer[..read];
		}
		catch
		{
			return [];
		}
	}

	private static bool StartsWith(byte[] data, string marker)
	{
		return Matches(data, 0, marker);
	}

	private static bool Matches(byte[] data, int offset, string marker)
	{
		if (data.Length < offset + marker.Length)
			return false;
		for (int i = 0; i < marker.Length; i++)
		{
			if (data[offset + i] != marker[i])
				return false;
		}
		return true;
	}

	private static bool Contains(byte[] data, string marker)
	{
		for (int offset = 0; offset + marker.Length <= data.Length; offset++)
		{
			if (Matches(data, offset, marker))
				return true;
		}
		return false;
	}

	private static void ConvertChannels(float[] input, float[][] output, int frames, int inputChannels, float gain)
	{
		if (output.Length == 1)
		{
			for (int frame = 0; frame < frames; frame++)
				output[0][frame] = Limit(input[frame] * gain);
			return;
		}

		for (int frame = 0; frame < frames; frame++)
		{
			int offset = frame * inputChannels;
			float left = 0f;
			float right = 0f;
			int leftCount = 0;
			int rightCount = 0;
			for (int channel = 0; channel < inputChannels; channel++)
			{
				if ((channel & 1) == 0)
				{
					left += input[offset + channel];
					leftCount++;
				}
				else
				{
					right += input[offset + channel];
					rightCount++;
				}
			}
			if (rightCount == 0)
			{
				right = left;
				rightCount = leftCount;
			}
			output[0][frame] = Limit((left / leftCount) * gain);
			output[1][frame] = Limit((right / rightCount) * gain);
		}
	}

	private static void Drain(Stream output, OggStream oggStream, ProcessingState state, bool force)
	{
		while (!oggStream.Finished && state.PacketOut(out OggPacket packet))
		{
			oggStream.PacketIn(packet);
			while (!oggStream.Finished && oggStream.PageOut(out OggPage page, force))
				WritePage(output, page);
		}
	}

	private static void WritePage(Stream output, OggPage page)
	{
		output.Write(page.Header, 0, page.Header.Length);
		output.Write(page.Body, 0, page.Body.Length);
	}
}
