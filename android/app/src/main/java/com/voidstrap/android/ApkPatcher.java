package com.voidstrap.android;

import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.CRC32;

public final class ApkPatcher {
    private static final int LOCAL = 0x04034b50;
    private static final int CENTRAL = 0x02014b50;
    private static final int END = 0x06054b50;
    private static final int UTF8 = 0x0800;
    private static final int DOS_TIME = 0;
    private static final int DOS_DATE = 0x21;

    public interface Source {
        long length() throws IOException;

        InputStream open() throws IOException;
    }

    public static final class FileSource implements Source {
        final File file;

        public FileSource(File file) {
            this.file = file;
        }

        @Override
        public long length() {
            return file.length();
        }

        @Override
        public InputStream open() throws IOException {
            return new FileInputStream(file);
        }
    }

    public static final class BytesSource implements Source {
        final byte[] bytes;

        public BytesSource(byte[] bytes) {
            this.bytes = bytes;
        }

        @Override
        public long length() {
            return bytes.length;
        }

        @Override
        public InputStream open() {
            return new java.io.ByteArrayInputStream(bytes);
        }
    }

    public static final class Entry {
        public final String name;
        public final int method;
        public final long compressedSize;
        public final long size;
        final int centralOffset;
        final int centralLength;
        final long localOffset;

        Entry(String name, int method, long compressedSize, long size, int centralOffset, int centralLength, long localOffset) {
            this.name = name;
            this.method = method;
            this.compressedSize = compressedSize;
            this.size = size;
            this.centralOffset = centralOffset;
            this.centralLength = centralLength;
            this.localOffset = localOffset;
        }
    }

    public static final class Directory {
        public final long centralStart;
        final byte[] central;
        public final Map<String, Entry> entries;

        Directory(long centralStart, byte[] central, Map<String, Entry> entries) {
            this.centralStart = centralStart;
            this.central = central;
            this.entries = entries;
        }
    }

    private ApkPatcher() {
    }

    public static Directory read(File apk) throws IOException {
        try (RandomAccessFile raf = new RandomAccessFile(apk, "r")) {
            long length = raf.length();
            int tail = (int) Math.min(length, 65535 + 22);
            byte[] end = new byte[tail];
            raf.seek(length - tail);
            raf.readFully(end);
            int at = -1;
            for (int i = tail - 22; i >= 0; i--) {
                if (le32(end, i) == END) {
                    at = i;
                    break;
                }
            }
            if (at < 0) throw new IOException("not a zip");
            int total = le16(end, at + 10);
            long size = le32(end, at + 12) & 0xFFFFFFFFL;
            long start = le32(end, at + 16) & 0xFFFFFFFFL;
            if (total == 0xFFFF || size == 0xFFFFFFFFL || start == 0xFFFFFFFFL) throw new IOException("zip64 is not supported");
            if (start + size > length || size > 64L * 1024 * 1024) throw new IOException("bad central directory");
            byte[] central = new byte[(int) size];
            raf.seek(start);
            raf.readFully(central);
            Map<String, Entry> entries = new LinkedHashMap<>();
            int p = 0;
            for (int i = 0; i < total; i++) {
                if (p + 46 > central.length || le32(central, p) != CENTRAL) throw new IOException("bad central entry");
                int method = le16(central, p + 10);
                long compressed = le32(central, p + 20) & 0xFFFFFFFFL;
                long plain = le32(central, p + 24) & 0xFFFFFFFFL;
                int nameLength = le16(central, p + 28);
                int extraLength = le16(central, p + 30);
                int commentLength = le16(central, p + 32);
                long local = le32(central, p + 42) & 0xFFFFFFFFL;
                String name = new String(central, p + 46, nameLength, StandardCharsets.UTF_8);
                int recordLength = 46 + nameLength + extraLength + commentLength;
                entries.put(name, new Entry(name, method, compressed, plain, p, recordLength, local));
                p += recordLength;
            }
            return new Directory(start, central, entries);
        }
    }

    public static byte[] readEntry(File apk, Entry e) throws IOException {
        if (e.size > 64L * 1024 * 1024) throw new IOException("entry too large");
        try (RandomAccessFile raf = new RandomAccessFile(apk, "r")) {
            byte[] header = new byte[30];
            raf.seek(e.localOffset);
            raf.readFully(header);
            if (le32(header, 0) != LOCAL) throw new IOException("bad local header");
            long data = e.localOffset + 30 + le16(header, 26) + le16(header, 28);
            byte[] raw = new byte[(int) e.compressedSize];
            raf.seek(data);
            raf.readFully(raw);
            if (e.method == 0) return raw;
            if (e.method != 8) throw new IOException("unsupported method");
            java.util.zip.Inflater inf = new java.util.zip.Inflater(true);
            try {
                inf.setInput(raw);
                byte[] out = new byte[(int) e.size];
                int n = 0;
                while (n < out.length && !inf.finished()) {
                    int r = inf.inflate(out, n, out.length - n);
                    if (r == 0 && (inf.needsInput() || inf.needsDictionary())) break;
                    n += r;
                }
                if (n != out.length) throw new IOException("short entry");
                return out;
            } catch (java.util.zip.DataFormatException ex) {
                throw new IOException(ex);
            } finally {
                inf.end();
            }
        }
    }

    public static void patch(File base, File out, Map<String, Source> changes, AtomicBoolean cancel) throws IOException {
        Directory dir = read(base);
        Map<String, Source> sorted = new TreeMap<>(changes);
        List<long[]> written = new ArrayList<>();
        List<String> names = new ArrayList<>();
        try (FileInputStream in = new FileInputStream(base); FileChannel src = in.getChannel();
             FileOutputStream fos = new FileOutputStream(out); FileChannel dst = fos.getChannel()) {
            long copied = 0;
            while (copied < dir.centralStart) {
                if (cancel != null && cancel.get()) throw new IOException("cancelled");
                long n = src.transferTo(copied, Math.min(64L * 1024 * 1024, dir.centralStart - copied), dst);
                if (n <= 0) throw new IOException("copy failed");
                copied += n;
            }
            dst.position(dir.centralStart);
            OutputStream body = new BufferedOutputStream(fos, 1 << 16);
            long pos = dir.centralStart;
            byte[] buf = new byte[1 << 16];
            for (Map.Entry<String, Source> change : sorted.entrySet()) {
                if (cancel != null && cancel.get()) throw new IOException("cancelled");
                byte[] name = change.getKey().getBytes(StandardCharsets.UTF_8);
                Source s = change.getValue();
                long length = s.length();
                if (length > 0xFFFFFFFEL) throw new IOException("entry too large");
                CRC32 crc = new CRC32();
                long counted = 0;
                try (InputStream is = s.open()) {
                    int n;
                    while ((n = is.read(buf)) > 0) {
                        crc.update(buf, 0, n);
                        counted += n;
                    }
                }
                if (counted != length) throw new IOException("source changed");
                int pad = (int) ((4 - ((pos + 30 + name.length) % 4)) % 4);
                ByteBuffer h = ByteBuffer.allocate(30).order(ByteOrder.LITTLE_ENDIAN);
                h.putInt(LOCAL).putShort((short) 10).putShort((short) UTF8).putShort((short) 0)
                        .putShort((short) DOS_TIME).putShort((short) DOS_DATE).putInt((int) crc.getValue())
                        .putInt((int) length).putInt((int) length).putShort((short) name.length).putShort((short) pad);
                body.write(h.array());
                body.write(name);
                body.write(new byte[pad]);
                long total = 0;
                try (InputStream is = s.open()) {
                    int n;
                    while ((n = is.read(buf)) > 0) {
                        body.write(buf, 0, n);
                        total += n;
                    }
                }
                if (total != length) throw new IOException("source changed");
                written.add(new long[]{pos, crc.getValue(), length});
                names.add(change.getKey());
                pos += 30 + name.length + pad + length;
            }
            long centralStart = pos;
            ByteBuffer rec = ByteBuffer.allocate(46).order(ByteOrder.LITTLE_ENDIAN);
            int count = 0;
            long centralSize = 0;
            Map<String, Integer> index = new java.util.HashMap<>();
            for (int i = 0; i < names.size(); i++) index.put(names.get(i), i);
            for (Entry e : dir.entries.values()) {
                Integer w = index.remove(e.name);
                if (w == null) {
                    body.write(dir.central, e.centralOffset, e.centralLength);
                    centralSize += e.centralLength;
                } else {
                    centralSize += writeCentral(body, rec, e.name, written.get(w), le16(dir.central, e.centralOffset + 4), le32(dir.central, e.centralOffset + 38));
                }
                count++;
            }
            for (int i = 0; i < names.size(); i++) {
                if (!index.containsKey(names.get(i))) continue;
                centralSize += writeCentral(body, rec, names.get(i), written.get(i), 0x0314, 0100644 << 16);
                count++;
            }
            if (count > 0xFFFE || centralStart > 0xFFFFFFFEL) throw new IOException("zip64 is not supported");
            ByteBuffer end = ByteBuffer.allocate(22).order(ByteOrder.LITTLE_ENDIAN);
            end.putInt(END).putShort((short) 0).putShort((short) 0).putShort((short) count).putShort((short) count)
                    .putInt((int) centralSize).putInt((int) centralStart).putShort((short) 0);
            body.write(end.array());
            body.flush();
            fos.getFD().sync();
        }
    }

    private static int writeCentral(OutputStream body, ByteBuffer rec, String entry, long[] w, int madeBy, int external) throws IOException {
        byte[] name = entry.getBytes(StandardCharsets.UTF_8);
        rec.clear();
        rec.putInt(CENTRAL).putShort((short) madeBy).putShort((short) 10).putShort((short) UTF8).putShort((short) 0)
                .putShort((short) DOS_TIME).putShort((short) DOS_DATE).putInt((int) w[1]).putInt((int) w[2]).putInt((int) w[2])
                .putShort((short) name.length).putShort((short) 0).putShort((short) 0).putShort((short) 0).putShort((short) 0)
                .putInt(external).putInt((int) w[0]);
        body.write(rec.array());
        body.write(name);
        return 46 + name.length;
    }

    private static int le16(byte[] b, int o) {
        return (b[o] & 0xFF) | (b[o + 1] & 0xFF) << 8;
    }

    private static int le32(byte[] b, int o) {
        return (b[o] & 0xFF) | (b[o + 1] & 0xFF) << 8 | (b[o + 2] & 0xFF) << 16 | (b[o + 3] & 0xFF) << 24;
    }
}
