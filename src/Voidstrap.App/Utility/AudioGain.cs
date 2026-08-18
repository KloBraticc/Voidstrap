using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Vorbis;
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
		string extension = Path.GetExtension(path).ToLowerInvariant();
		List<Func<WaveStream>> readers = [];
		if (extension is ".ogg" or ".oga")
			readers.Add(() => new VorbisWaveReader(path));
		if (extension is ".wav" or ".wave")
			readers.Add(() => new WaveFileReader(path));
		if (extension is ".aif" or ".aiff" or ".aifc")
			readers.Add(() => new AiffFileReader(path));
		if (extension is ".mp3" or ".mp2" or ".mpa")
			readers.Add(() => new Mp3FileReader(path));
		readers.Add(() => new MediaFoundationReader(path));
		if (extension is not ".ogg" and not ".oga")
			readers.Add(() => new VorbisWaveReader(path));
		if (extension is not ".wav" and not ".wave")
			readers.Add(() => new WaveFileReader(path));
		if (extension is not ".aif" and not ".aiff" and not ".aifc")
			readers.Add(() => new AiffFileReader(path));
		if (extension is not ".mp3" and not ".mp2" and not ".mpa")
			readers.Add(() => new Mp3FileReader(path));

		Exception? lastError = null;
		foreach (Func<WaveStream> createReader in readers)
		{
			try
			{
				WaveStream reader = createReader();
				if (reader.WaveFormat.Channels > 0 && reader.WaveFormat.SampleRate > 0)
					return reader;
				reader.Dispose();
			}
			catch (Exception ex)
			{
				lastError = ex;
			}
		}
		throw new InvalidDataException("Windows could not decode this audio format", lastError);
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
