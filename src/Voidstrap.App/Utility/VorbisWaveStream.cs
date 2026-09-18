using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NVorbis;

namespace Voidstrap.Utility;

public sealed class VorbisWaveStream : WaveStream
{
	private readonly VorbisReader _reader;

	private readonly WaveFormat _format;

	private bool _disposed;

	public VorbisWaveStream(string path)
		: this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan), closeOnDispose: true)
	{
	}

	public VorbisWaveStream(Stream stream, bool closeOnDispose)
	{
		VorbisReader reader;
		try
		{
			reader = new VorbisReader(stream, closeOnDispose);
		}
		catch
		{
			if (closeOnDispose)
				stream.Dispose();
			throw;
		}
		if (reader.Channels < 1 || reader.SampleRate < 1)
		{
			reader.Dispose();
			throw new InvalidDataException("The Vorbis stream has no audio");
		}
		_reader = reader;
		_format = WaveFormat.CreateIeeeFloatWaveFormat(reader.SampleRate, reader.Channels);
	}

	public override WaveFormat WaveFormat => _format;

	public override long Length => _reader.TotalSamples * _format.BlockAlign;

	public override long Position
	{
		get => _reader.SamplePosition * _format.BlockAlign;
		set => _reader.SeekTo(Math.Clamp(value / _format.BlockAlign, 0, _reader.TotalSamples));
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> buffer)
	{
		int frames = buffer.Length / _format.BlockAlign;
		if (frames <= 0)
			return 0;
		Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer.Slice(0, frames * _format.BlockAlign));
		int read = _reader.ReadSamples(samples);
		return read * sizeof(float);
	}

	protected override void Dispose(bool disposing)
	{
		if (!_disposed)
		{
			_disposed = true;
			if (disposing)
				_reader.Dispose();
		}
		base.Dispose(disposing);
	}
}
