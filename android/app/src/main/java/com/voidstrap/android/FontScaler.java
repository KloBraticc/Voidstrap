package com.voidstrap.android;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

public final class FontScaler {
    private static final int MIN_UNITS = 16;
    private static final int MAX_UNITS = 16384;

    private static final class Table {
        String tag;
        byte[] data;
    }

    private FontScaler() {
    }

    public static boolean collection(byte[] data) {
        return data.length >= 4 && data[0] == 't' && data[1] == 't' && data[2] == 'c' && data[3] == 'f';
    }

    public static byte[] scale(byte[] data, double scale) {
        if (Math.abs(scale - 1.0) < 0.0001) return data;
        try {
            List<Table> tables = readTables(data);
            if (tables != null) {
                byte[] rebuilt = scaleOutlines(tables, scale);
                if (rebuilt != null) return rebuilt;
            }
            byte[] copy = Arrays.copyOf(data, data.length);
            if (scaleEm(copy, scale)) return copy;
        } catch (RuntimeException ignored) {
        }
        return data;
    }

    private static List<Table> readTables(byte[] data) {
        if (data.length < 12 || collection(data)) return null;
        int count = u16(data, 4);
        if (count <= 0 || count > 512 || 12 + count * 16 > data.length) return null;
        List<Table> tables = new ArrayList<>(count);
        for (int i = 0; i < count; i++) {
            int entry = 12 + i * 16;
            long offset = u32(data, entry + 8);
            long length = u32(data, entry + 12);
            if (offset < 0 || length < 0 || offset + length > data.length) return null;
            Table t = new Table();
            t.tag = new String(data, entry, 4, StandardCharsets.US_ASCII);
            t.data = Arrays.copyOfRange(data, (int) offset, (int) (offset + length));
            tables.add(t);
        }
        return tables;
    }

    private static Table find(List<Table> tables, String tag) {
        for (Table t : tables) if (t.tag.equals(tag)) return t;
        return null;
    }

    private static final class Bounds {
        int minX = Integer.MAX_VALUE;
        int minY = Integer.MAX_VALUE;
        int maxX = Integer.MIN_VALUE;
        int maxY = Integer.MIN_VALUE;
    }

    private static byte[] scaleOutlines(List<Table> tables, double scale) {
        Table head = find(tables, "head");
        Table maxp = find(tables, "maxp");
        Table loca = find(tables, "loca");
        Table glyf = find(tables, "glyf");
        Table hhea = find(tables, "hhea");
        Table hmtx = find(tables, "hmtx");
        if (head == null || maxp == null || loca == null || glyf == null || head.data.length < 54 || maxp.data.length < 6) return null;
        int glyphs = u16(maxp.data, 4);
        int locFormat = s16(head.data, 50);
        if (glyphs <= 0) return null;
        int[] offsets = new int[glyphs + 1];
        if (locFormat == 0) {
            if (loca.data.length < (glyphs + 1) * 2) return null;
            for (int i = 0; i <= glyphs; i++) offsets[i] = u16(loca.data, i * 2) * 2;
        } else {
            if (loca.data.length < (glyphs + 1) * 4) return null;
            for (int i = 0; i <= glyphs; i++) offsets[i] = (int) u32(loca.data, i * 4);
        }
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        int[] next = new int[glyphs + 1];
        Bounds b = new Bounds();
        for (int i = 0; i < glyphs; i++) {
            next[i] = out.size();
            int start = offsets[i];
            int length = offsets[i + 1] - start;
            if (length <= 0 || start < 0 || start + length > glyf.data.length) continue;
            byte[] scaled = scaleGlyph(Arrays.copyOfRange(glyf.data, start, start + length), scale, b);
            if (scaled == null) return null;
            out.write(scaled, 0, scaled.length);
            while (out.size() % 4 != 0) out.write(0);
        }
        next[glyphs] = out.size();
        glyf.data = out.toByteArray();
        byte[] newLoca = new byte[(glyphs + 1) * 4];
        for (int i = 0; i <= glyphs; i++) w32(newLoca, i * 4, next[i]);
        loca.data = newLoca;
        w16(head.data, 50, 1);
        if (b.minX <= b.maxX) {
            w16(head.data, 36, sat(b.minX));
            w16(head.data, 38, sat(b.minY));
            w16(head.data, 40, sat(b.maxX));
            w16(head.data, 42, sat(b.maxY));
        }
        if (hhea != null && hmtx != null && hhea.data.length >= 36) {
            int metrics = u16(hhea.data, 34);
            for (int i = 0; i < metrics; i++) {
                int at = i * 4;
                if (at + 4 > hmtx.data.length) break;
                int advance = u16(hmtx.data, at);
                int bearing = s16(hmtx.data, at + 2);
                w16(hmtx.data, at, Math.max(0, Math.min(65535, (int) Math.round(advance * scale))));
                w16(hmtx.data, at + 2, sat((int) Math.round(bearing * scale)));
            }
        }
        return rebuild(tables);
    }

    private static int sat(int v) {
        return Math.max(Short.MIN_VALUE, Math.min(Short.MAX_VALUE, v));
    }

    private static byte[] scaleGlyph(byte[] g, double scale, Bounds b) {
        if (g.length < 10) return null;
        int contours = s16(g, 0);
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        int gxMin = sat((int) Math.round(s16(g, 2) * scale));
        int gyMin = sat((int) Math.round(s16(g, 4) * scale));
        int gxMax = sat((int) Math.round(s16(g, 6) * scale));
        int gyMax = sat((int) Math.round(s16(g, 8) * scale));
        b.minX = Math.min(b.minX, gxMin);
        b.minY = Math.min(b.minY, gyMin);
        b.maxX = Math.max(b.maxX, gxMax);
        b.maxY = Math.max(b.maxY, gyMax);
        ws(out, contours);
        ws(out, gxMin);
        ws(out, gyMin);
        ws(out, gxMax);
        ws(out, gyMax);
        return contours >= 0 ? simple(g, contours, scale, out) : composite(g, scale, out);
    }

    private static byte[] simple(byte[] g, int contours, double scale, ByteArrayOutputStream out) {
        int p = 10;
        if (p + contours * 2 + 2 > g.length) return null;
        int points = 0;
        for (int i = 0; i < contours; i++) {
            int end = u16(g, p);
            out.write(g, p, 2);
            p += 2;
            points = end + 1;
        }
        int instructions = u16(g, p);
        p += 2;
        if (p + instructions > g.length) return null;
        p += instructions;
        ws(out, 0);
        if (points <= 0) return out.toByteArray();
        byte[] flags = new byte[points];
        int index = 0;
        while (index < points) {
            if (p >= g.length) return null;
            byte flag = g[p++];
            flags[index++] = flag;
            if ((flag & 0x08) != 0) {
                if (p >= g.length) return null;
                int repeat = g[p++] & 0xFF;
                for (int r = 0; r < repeat && index < points; r++) flags[index++] = flag;
            }
        }
        int[] dx = new int[points];
        int[] dy = new int[points];
        int[] pos = {p};
        if (!deltas(g, pos, flags, dx, 0x02, 0x10)) return null;
        if (!deltas(g, pos, flags, dy, 0x04, 0x20)) return null;
        int[] ax = new int[points];
        int[] ay = new int[points];
        int rx = 0;
        int ry = 0;
        for (int i = 0; i < points; i++) {
            rx += dx[i];
            ry += dy[i];
            ax[i] = (int) Math.round(rx * scale);
            ay[i] = (int) Math.round(ry * scale);
        }
        for (int i = 0; i < points; i++) out.write(flags[i] & 0x01);
        int prev = 0;
        for (int i = 0; i < points; i++) {
            ws(out, sat(ax[i] - prev));
            prev = ax[i];
        }
        prev = 0;
        for (int i = 0; i < points; i++) {
            ws(out, sat(ay[i] - prev));
            prev = ay[i];
        }
        return out.toByteArray();
    }

    private static boolean deltas(byte[] g, int[] pos, byte[] flags, int[] d, int shortBit, int sameBit) {
        for (int i = 0; i < flags.length; i++) {
            byte flag = flags[i];
            if ((flag & shortBit) != 0) {
                if (pos[0] >= g.length) return false;
                int v = g[pos[0]++] & 0xFF;
                d[i] = (flag & sameBit) != 0 ? v : -v;
            } else if ((flag & sameBit) != 0) {
                d[i] = 0;
            } else {
                if (pos[0] + 2 > g.length) return false;
                d[i] = s16(g, pos[0]);
                pos[0] += 2;
            }
        }
        return true;
    }

    private static byte[] composite(byte[] g, double scale, ByteArrayOutputStream out) {
        int p = 10;
        while (true) {
            if (p + 4 > g.length) return null;
            int flags = u16(g, p);
            int glyph = u16(g, p + 2);
            p += 4;
            boolean words = (flags & 0x0001) != 0;
            boolean xy = (flags & 0x0002) != 0;
            int a1;
            int a2;
            if (words) {
                if (p + 4 > g.length) return null;
                a1 = s16(g, p);
                a2 = s16(g, p + 2);
                p += 4;
            } else {
                if (p + 2 > g.length) return null;
                a1 = xy ? g[p] : g[p] & 0xFF;
                a2 = xy ? g[p + 1] : g[p + 1] & 0xFF;
                p += 2;
            }
            if (xy) {
                a1 = (int) Math.round(a1 * scale);
                a2 = (int) Math.round(a2 * scale);
            }
            ws(out, (flags | 0x0001) & ~0x0100);
            ws(out, glyph);
            ws(out, sat(a1));
            ws(out, sat(a2));
            int transform = (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            if (transform > 0) {
                if (p + transform > g.length) return null;
                out.write(g, p, transform);
                p += transform;
            }
            if ((flags & 0x0020) == 0) break;
        }
        return out.toByteArray();
    }

    private static byte[] rebuild(List<Table> tables) {
        tables.sort((a, b) -> a.tag.compareTo(b.tag));
        int count = tables.size();
        int dirSize = 12 + count * 16;
        int total = dirSize;
        for (Table t : tables) total += (t.data.length + 3) & ~3;
        byte[] out = new byte[total];
        w32(out, 0, 0x00010000);
        w16(out, 4, count);
        int power = 1;
        int exponent = 0;
        while (power * 2 <= count) {
            power *= 2;
            exponent++;
        }
        w16(out, 6, power * 16);
        w16(out, 8, exponent);
        w16(out, 10, count * 16 - power * 16);
        int offset = dirSize;
        int headOffset = -1;
        for (int i = 0; i < count; i++) {
            Table t = tables.get(i);
            int entry = 12 + i * 16;
            byte[] tag = t.tag.getBytes(StandardCharsets.US_ASCII);
            System.arraycopy(tag, 0, out, entry, 4);
            if (t.tag.equals("head")) {
                headOffset = offset;
                w32(t.data, 8, 0);
            }
            System.arraycopy(t.data, 0, out, offset, t.data.length);
            int padded = (t.data.length + 3) & ~3;
            w32(out, entry + 4, (int) checksum(out, offset, padded));
            w32(out, entry + 8, offset);
            w32(out, entry + 12, t.data.length);
            offset += padded;
        }
        if (headOffset >= 0) w32(out, headOffset + 8, (int) (0xB1B0AFBAL - checksum(out, 0, out.length)));
        return out;
    }

    private static long checksum(byte[] d, int off, int len) {
        long sum = 0;
        int i = 0;
        for (; i + 4 <= len; i += 4) sum = (sum + u32(d, off + i)) & 0xFFFFFFFFL;
        if (i < len) {
            long tail = 0;
            for (int k = 0; k < 4; k++) tail = (tail << 8) | (i + k < len ? d[off + i + k] & 0xFF : 0);
            sum = (sum + tail) & 0xFFFFFFFFL;
        }
        return sum;
    }

    private static boolean scaleEm(byte[] data, double scale) {
        if (readTables(data) == null) return false;
        int count = u16(data, 4);
        for (int i = 0; i < count; i++) {
            int entry = 12 + i * 16;
            if (!new String(data, entry, 4, StandardCharsets.US_ASCII).equals("head")) continue;
            int offset = (int) u32(data, entry + 8);
            if (offset + 20 > data.length) return false;
            int current = u16(data, offset + 18);
            if (current == 0) return false;
            int scaled = Math.max(MIN_UNITS, Math.min(MAX_UNITS, (int) Math.round(current / scale)));
            w16(data, offset + 18, scaled);
            w32(data, offset + 8, 0);
            return true;
        }
        return false;
    }

    private static void ws(ByteArrayOutputStream out, int v) {
        out.write((v >> 8) & 0xFF);
        out.write(v & 0xFF);
    }

    private static int u16(byte[] b, int o) {
        return (b[o] & 0xFF) << 8 | (b[o + 1] & 0xFF);
    }

    private static int s16(byte[] b, int o) {
        return (short) u16(b, o);
    }

    private static long u32(byte[] b, int o) {
        return ((long) (b[o] & 0xFF) << 24) | (b[o + 1] & 0xFF) << 16 | (b[o + 2] & 0xFF) << 8 | (b[o + 3] & 0xFF);
    }

    private static void w16(byte[] b, int o, int v) {
        b[o] = (byte) (v >> 8);
        b[o + 1] = (byte) v;
    }

    private static void w32(byte[] b, int o, int v) {
        b[o] = (byte) (v >> 24);
        b[o + 1] = (byte) (v >> 16);
        b[o + 2] = (byte) (v >> 8);
        b[o + 3] = (byte) v;
    }
}
