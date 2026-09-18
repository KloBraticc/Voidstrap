using System;
using System.IO;
using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;

namespace Voidstrap.Utility;

public sealed class OpusWaveStream : WaveStream
{
	private const int OpusSampleRate = 48000;

	private readonly Stream _stream;

	private readonly int _channels;

	private OpusOggReadStream _reader;

	private readonly WaveFormat _format;

	private readonly long _length;

	private short[] _pending = [];

	private int _pendingOffset;

	private long _position;

	private bool _disposed;

	public OpusWaveStream(string path)
	{
		_stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
		try
		{
			_channels = ReadChannelCount(_stream);
			_reader = OpenReader();
			_format = WaveFormat.CreateIeeeFloatWaveFormat(OpusSampleRate, _channels);
			_length = (long)(_reader.TotalTime.TotalSeconds * OpusSampleRate) * _format.BlockAlign;
		}
		catch
		{
			_stream.Dispose();
			throw;
		}
	}

	private OpusOggReadStream OpenReader()
	{
		_stream.Position = 0;
		return new OpusOggReadStream(OpusCodecFactory.CreateDecoder(OpusSampleRate, _channels), _stream);
	}

	private static int ReadChannelCount(Stream stream)
	{
		byte[] head = new byte[512];
		int read = stream.Read(head, 0, head.Length);
		stream.Position = 0;
		for (int offset = 0; offset + 10 <= read; offset++)
		{
			if (head[offset] == 'O' && head[offset + 1] == 'p' && head[offset + 2] == 'u' && head[offset + 3] == 's' && head[offset + 4] == 'H' && head[offset + 5] == 'e' && head[offset + 6] == 'a' && head[offset + 7] == 'd')
			{
				int channels = head[offset + 9];
				if (channels is >= 1 and <= 8)
					return channels;
				break;
			}
		}
		throw new InvalidDataException("The Opus header could not be read");
	}

	public override WaveFormat WaveFormat => _format;

	public override long Length => _length;

	public override long Position
	{
		get => _position;
		set
		{
			long target = Math.Clamp(value, 0, Math.Max(_length, 0));
			_reader = OpenReader();
			_pending = [];
			_pendingOffset = 0;
			_position = 0;
			if (target > 0)
			{
				_reader.SeekTo(TimeSpan.FromSeconds((double)(target / _format.BlockAlign) / OpusSampleRate));
				_position = target;
			}
		}
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> buffer)
	{
		int written = 0;
		Span<float> output = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.Slice(0, buffer.Length / _format.BlockAlign * _format.BlockAlign));
		while (written < output.Length)
		{
			if (_pendingOffset >= _pending.Length)
			{
				if (!_reader.HasNextPacket)
					break;
				short[]? packet = _reader.DecodeNextPacket();
				if (packet == null)
					continue;
				_pending = packet;
				_pendingOffset = 0;
				continue;
			}
			int available = Math.Min(_pending.Length - _pendingOffset, output.Length - written);
			for (int i = 0; i < available; i++)
				output[written + i] = _pending[_pendingOffset + i] / 32768f;
			_pendingOffset += available;
			written += available;
		}
		int bytes = written * sizeof(float);
		_position += bytes;
		return bytes;
	}

	protected override void Dispose(bool disposing)
	{
		if (!_disposed)
		{
			_disposed = true;
			if (disposing)
				_stream.Dispose();
		}
		base.Dispose(disposing);
	}
}
