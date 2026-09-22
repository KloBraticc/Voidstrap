package com.voidstrap.android;

import java.io.File;
import java.io.IOException;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicBoolean;

public final class ApkPatcher {
    static {
        Core.load();
    }

    public abstract static class Source {
        private Source() {
        }
    }

    public static final class FileSource extends Source {
        final File file;

        public FileSource(File file) {
            this.file = file;
        }
    }

    public static final class BytesSource extends Source {
        final byte[] bytes;

        public BytesSource(byte[] bytes) {
            this.bytes = bytes;
        }
    }

    public static final class Entry {
        public final String name;
        public final int method;
        public final long compressedSize;
        public final long size;
        final long localOffset;

        Entry(String name, int method, long compressedSize, long size, long localOffset) {
            this.name = name;
            this.method = method;
            this.compressedSize = compressedSize;
            this.size = size;
            this.localOffset = localOffset;
        }
    }

    public static final class Directory {
        public final long centralStart;
        public final Map<String, Entry> entries;

        Directory(long centralStart, Map<String, Entry> entries) {
            this.centralStart = centralStart;
            this.entries = entries;
        }
    }

    private ApkPatcher() {
    }

    public static Directory read(File apk) throws IOException {
        if (!Core.loaded()) throw new IOException(Core.UNAVAILABLE);
        Object[] raw = readNative(apk.getPath());
        if (raw == null || raw.length < 2 || !(raw[0] instanceof String[]) || !(raw[1] instanceof long[])) throw new IOException("unreadable apk");
        String[] names = (String[]) raw[0];
        long[] f = (long[]) raw[1];
        if (f.length < 1 + names.length * 4) throw new IOException("unreadable apk");
        Map<String, Entry> entries = new LinkedHashMap<>();
        for (int i = 0; i < names.length; i++) {
            int at = 1 + i * 4;
            if (names[i] == null) continue;
            entries.put(names[i], new Entry(names[i], (int) f[at], f[at + 1], f[at + 2], f[at + 3]));
        }
        return new Directory(f[0], entries);
    }

    public static void patch(File base, File out, Map<String, Source> changes, AtomicBoolean cancel) throws IOException {
        if (!Core.loaded()) throw new IOException(Core.UNAVAILABLE);
        int n = changes.size();
        String[] names = new String[n];
        String[] paths = new String[n];
        byte[][] bytes = new byte[n][];
        int i = 0;
        for (Map.Entry<String, Source> c : changes.entrySet()) {
            names[i] = c.getKey();
            if (c.getValue() instanceof FileSource) paths[i] = ((FileSource) c.getValue()).file.getPath();
            else bytes[i] = ((BytesSource) c.getValue()).bytes;
            i++;
        }
        patchNative(base.getPath(), out.getPath(), names, paths, bytes, cancel);
    }

    private static native Object[] readNative(String apk) throws IOException;

    private static native void patchNative(String base, String out, String[] names, String[] paths, byte[][] bytes, AtomicBoolean cancel) throws IOException;
}
