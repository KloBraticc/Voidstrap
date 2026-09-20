package com.voidstrap.android;

import android.media.MediaCodec;
import android.media.MediaExtractor;
import android.media.MediaFormat;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

public final class AudioGain {
    private static final int MAX_SECONDS = 60;
    private static final long MAX_RAW = 64L * 1024 * 1024;

    private AudioGain() {
    }

    public static byte[] render(File source, double gain) throws IOException {
        gain = Math.max(0.01, Math.min(8.0, gain));
        if (Math.abs(gain - 1.0) < 0.005 && playable(source) && source.length() <= MAX_RAW) {
            try (InputStream in = new FileInputStream(source)) {
                return ModEngine.readAll(in);
            }
        }
        return decode(source, gain);
    }

    static boolean playable(File f) {
        byte[] h = new byte[12];
        try (InputStream in = new FileInputStream(f)) {
            if (in.read(h) < 12) return false;
        } catch (IOException e) {
            return false;
        }
        boolean ogg = h[0] == 'O' && h[1] == 'g' && h[2] == 'g' && h[3] == 'S';
        boolean wav = h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'A' && h[10] == 'V' && h[11] == 'E';
        boolean id3 = h[0] == 'I' && h[1] == 'D' && h[2] == '3';
        boolean mpeg = (h[0] & 0xFF) == 0xFF && (h[1] & 0xE0) == 0xE0 && (h[1] & 0x06) != 0;
        return ogg || wav || id3 || mpeg;
    }

    private static byte[] decode(File source, double gain) throws IOException {
        MediaExtractor ex = new MediaExtractor();
        MediaCodec codec = null;
        try {
            ex.setDataSource(source.getAbsolutePath());
            int track = -1;
            MediaFormat format = null;
            for (int i = 0; i < ex.getTrackCount(); i++) {
                MediaFormat f = ex.getTrackFormat(i);
                String mime = f.getString(MediaFormat.KEY_MIME);
                if (mime != null && mime.startsWith("audio/")) {
                    track = i;
                    format = f;
                    break;
                }
            }
            if (track < 0) throw new IOException("That file has no audio");
            ex.selectTrack(track);
            codec = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME));
            codec.configure(format, null, null, 0);
            codec.start();
            int rate = format.containsKey(MediaFormat.KEY_SAMPLE_RATE) ? format.getInteger(MediaFormat.KEY_SAMPLE_RATE) : 44100;
            int channels = format.containsKey(MediaFormat.KEY_CHANNEL_COUNT) ? format.getInteger(MediaFormat.KEY_CHANNEL_COUNT) : 2;
            int encoding = 2;
            ByteArrayOutputStream pcm = new ByteArrayOutputStream();
            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            boolean inputDone = false;
            boolean outputDone = false;
            long limit = 0;
            while (!outputDone) {
                if (!inputDone) {
                    int in = codec.dequeueInputBuffer(10000);
                    if (in >= 0) {
                        ByteBuffer buf = codec.getInputBuffer(in);
                        int n = buf == null ? -1 : ex.readSampleData(buf, 0);
                        if (n < 0) {
                            codec.queueInputBuffer(in, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                            inputDone = true;
                        } else {
                            codec.queueInputBuffer(in, 0, n, ex.getSampleTime(), 0);
                            ex.advance();
                        }
                    }
                }
                int out = codec.dequeueOutputBuffer(info, 10000);
                if (out == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    MediaFormat f = codec.getOutputFormat();
                    if (f.containsKey(MediaFormat.KEY_SAMPLE_RATE)) rate = f.getInteger(MediaFormat.KEY_SAMPLE_RATE);
                    if (f.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) channels = f.getInteger(MediaFormat.KEY_CHANNEL_COUNT);
                    if (f.containsKey(MediaFormat.KEY_PCM_ENCODING)) encoding = f.getInteger(MediaFormat.KEY_PCM_ENCODING);
                } else if (out >= 0) {
                    ByteBuffer buf = codec.getOutputBuffer(out);
                    if (buf != null && info.size > 0) {
                        buf.position(info.offset);
                        buf.limit(info.offset + info.size);
                        buf.order(ByteOrder.LITTLE_ENDIAN);
                        limit = (long) rate * Math.max(1, channels) * 2 * MAX_SECONDS;
                        while (buf.remaining() > 0 && pcm.size() < limit) {
                            float s;
                            if (encoding == 4) {
                                if (buf.remaining() < 4) break;
                                s = buf.getFloat();
                            } else if (encoding == 3) {
                                s = ((buf.get() & 0xFF) - 128) / 128f;
                            } else {
                                if (buf.remaining() < 2) break;
                                s = buf.getShort() / 32768f;
                            }
                            int v = (int) Math.round(Math.max(-1.0, Math.min(1.0, s * gain)) * 32767.0);
                            pcm.write(v & 0xFF);
                            pcm.write((v >> 8) & 0xFF);
                        }
                    }
                    codec.releaseOutputBuffer(out, false);
                    if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0 || (limit > 0 && pcm.size() >= limit)) outputDone = true;
                }
            }
            if (pcm.size() == 0) throw new IOException("That file has no audio");
            return wav(pcm.toByteArray(), rate, Math.max(1, channels));
        } catch (IllegalStateException | IllegalArgumentException e) {
            throw new IOException("That audio file could not be read", e);
        } finally {
            if (codec != null) {
                try {
                    codec.stop();
                } catch (IllegalStateException ignored) {
                }
                codec.release();
            }
            ex.release();
        }
    }

    static byte[] wav(byte[] pcm, int rate, int channels) {
        ByteBuffer h = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
        h.put(new byte[]{'R', 'I', 'F', 'F'}).putInt(36 + pcm.length).put(new byte[]{'W', 'A', 'V', 'E'});
        h.put(new byte[]{'f', 'm', 't', ' '}).putInt(16).putShort((short) 1).putShort((short) channels)
                .putInt(rate).putInt(rate * channels * 2).putShort((short) (channels * 2)).putShort((short) 16);
        h.put(new byte[]{'d', 'a', 't', 'a'}).putInt(pcm.length);
        byte[] out = new byte[44 + pcm.length];
        System.arraycopy(h.array(), 0, out, 0, 44);
        System.arraycopy(pcm, 0, out, 44, pcm.length);
        return out;
    }
}
