package com.voidstrap.android;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.CRC32;

final class Rar5 {
    private static final int NC = 306;
    private static final int DCB = 64;
    private static final int DCX = 80;
    private static final int LDC = 16;
    private static final int RC = 44;
    private static final int BC = 20;
    private static final int QUICK_BITS = 10;
    private static final int MAX_SIZE = 0x8000;
    private static final int UNPACK_MAX_WRITE = 0x400000;
    private static final int MAX_INC_LZ_MATCH = 0x1001 + 3;
    private static final int MAX_FILTER_BLOCK = 0x400000;
    private static final int MAX_FILTERS = 8192;
    private static final long MAX_WINDOW = 128L * 1024 * 1024;
    private static final int FILTER_DELTA = 0;
    private static final int FILTER_E8 = 1;
    private static final int FILTER_E8E9 = 2;
    private static final int FILTER_ARM = 3;
    private static final int FILTER_NONE = 99;

    private Rar5() {
    }

    static final class Item {
        String name;
        boolean directory;
        long unpacked;
        long dataStart;
        long packed;
        boolean hasCrc;
        int crc;
        int method;
        boolean solid;
        boolean extraDist;
        long dict;
        boolean redirect;
    }

    static void read(File f, ModArchives.Handler h, AtomicBoolean cancel) throws IOException {
        List<Item> items = scan(f);
        long total = 0;
        long dict = 0;
        for (Item it : items) {
            if (it.directory || it.redirect) continue;
            total += it.unpacked;
            if (it.method != 0) dict = Math.max(dict, it.dict);
        }
        long window = 1 << 18;
        long want = Math.min(dict, Math.max(total, 1 << 16));
        while (window < want) window <<= 1;
        if (dict > 0 && window > MAX_WINDOW) throw new ModArchives.Rejected("The rar package needs more memory than this device allows.");
        Decoder d = dict > 0 ? new Decoder((int) window) : null;
        File tmpDir = f.getParentFile();
        try (RandomAccessFile raf = new RandomAccessFile(f, "r")) {
            for (Item it : items) {
                if (cancel != null && cancel.get()) throw new IOException("cancelled");
                if (it.directory || it.redirect) continue;
                File tmp = File.createTempFile("rar5", ".part", tmpDir);
                try {
                    CRC32 crc = new CRC32();
                    try (OutputStream out = new FileOutputStream(tmp)) {
                        if (it.method == 0) {
                            raf.seek(it.dataStart);
                            byte[] buf = new byte[65536];
                            long left = it.packed;
                            while (left > 0) {
                                int n = raf.read(buf, 0, (int) Math.min(buf.length, left));
                                if (n <= 0) throw new ModArchives.Rejected("The rar package is damaged.");
                                crc.update(buf, 0, n);
                                out.write(buf, 0, n);
                                left -= n;
                            }
                        } else {
                            if (d == null) throw new ModArchives.Rejected("The rar package is damaged.");
                            d.unpack(raf, it, out, crc, cancel);
                        }
                    }
                    if (it.hasCrc && (int) crc.getValue() != it.crc) throw new ModArchives.Rejected("The rar package is damaged.");
                    try (InputStream in = new FileInputStream(tmp)) {
                        h.entry(it.name, tmp.length(), in);
                    }
                } finally {
                    tmp.delete();
                }
            }
        }
    }

    static List<Item> scan(File f) throws IOException {
        List<Item> items = new ArrayList<>();
        try (RandomAccessFile raf = new RandomAccessFile(f, "r")) {
            byte[] sig = new byte[8];
            raf.readFully(sig);
            if (sig[0] != 'R' || sig[1] != 'a' || sig[2] != 'r' || sig[3] != '!' || sig[6] != 1 || sig[7] != 0) throw new ModArchives.Rejected("The package is not a rar 5 archive.");
            long length = raf.length();
            long pos = 8;
            boolean solidArchive = false;
            while (pos + 7 <= length) {
                raf.seek(pos + 4);
                long[] v = new long[1];
                int sizeLen = readVint(raf, v);
                long headerSize = v[0];
                if (headerSize <= 0 || headerSize > 2 * 1024 * 1024) throw new ModArchives.Rejected("The rar package is damaged.");
                long headerStart = pos + 4 + sizeLen;
                byte[] header = new byte[(int) headerSize];
                raf.seek(headerStart);
                raf.readFully(header);
                int[] p = {0};
                long type = vint(header, p);
                long flags = vint(header, p);
                long extraSize = (flags & 1) != 0 ? vint(header, p) : 0;
                long dataSize = (flags & 2) != 0 ? vint(header, p) : 0;
                long dataStart = headerStart + headerSize;
                if (type == 1) {
                    long archiveFlags = vint(header, p);
                    solidArchive = (archiveFlags & 4) != 0;
                    if ((archiveFlags & 1) != 0) throw new ModArchives.Rejected("Split rar packages are not supported. Use a single file package.");
                } else if (type == 2) {
                    Item it = new Item();
                    long fileFlags = vint(header, p);
                    it.unpacked = vint(header, p);
                    vint(header, p);
                    if ((fileFlags & 2) != 0) p[0] += 4;
                    if ((fileFlags & 4) != 0) {
                        it.hasCrc = true;
                        it.crc = le32(header, p[0]);
                        p[0] += 4;
                    }
                    long info = vint(header, p);
                    vint(header, p);
                    int nameLength = (int) vint(header, p);
                    if (nameLength < 0 || p[0] + nameLength > header.length) throw new ModArchives.Rejected("The rar package is damaged.");
                    it.name = new String(header, p[0], nameLength, StandardCharsets.UTF_8);
                    p[0] += nameLength;
                    it.directory = (fileFlags & 1) != 0;
                    int version = (int) (info & 0x3F);
                    it.solid = (info & 0x40) != 0;
                    it.method = (int) ((info >> 7) & 7);
                    int power = (int) ((info >> 10) & (version == 0 ? 0xF : 0x1F));
                    it.dict = 0x20000L << power;
                    if (version == 1) {
                        long fraction = (info >> 15) & 0x1F;
                        it.dict += it.dict / 32 * fraction;
                        it.extraDist = (info & 0x100000) == 0;
                    } else if (version != 0) {
                        throw new ModArchives.Rejected("This rar package uses a newer format than Voidstrap supports.");
                    }
                    if ((flags & 0x08) != 0 || (flags & 0x10) != 0) throw new ModArchives.Rejected("Split rar packages are not supported. Use a single file package.");
                    if ((fileFlags & 8) != 0) throw new ModArchives.Rejected("The rar package is damaged.");
                    if (extraSize > 0) {
                        int ep = (int) (header.length - extraSize);
                        int[] q = {ep};
                        while (q[0] < header.length) {
                            long recSize = vint(header, q);
                            int recStart = q[0];
                            long recType = vint(header, q);
                            if (recType == 1) throw new ModArchives.Rejected("The rar package is password protected.");
                            if (recType == 5) it.redirect = true;
                            q[0] = (int) (recStart + recSize);
                        }
                    }
                    if (it.unpacked > ModArchives.MAX_EXTRACTED) throw new ModArchives.Rejected("The package expands to more than " + ModArchives.MAX_EXTRACTED / 1048576 + " MB.");
                    it.dataStart = dataStart;
                    it.packed = dataSize;
                    if (it.solid && !solidArchive) it.solid = true;
                    items.add(it);
                    if (items.size() > ModArchives.MAX_ENTRIES) throw new ModArchives.Rejected("The package contains more than " + ModArchives.MAX_ENTRIES + " files.");
                } else if (type == 4) {
                    throw new ModArchives.Rejected("The rar package is password protected.");
                } else if (type == 5) {
                    break;
                }
                pos = dataStart + dataSize;
            }
        }
        return items;
    }

    private static int readVint(RandomAccessFile raf, long[] out) throws IOException {
        long v = 0;
        for (int i = 0; i < 10; i++) {
            int b = raf.read();
            if (b < 0) throw new ModArchives.Rejected("The rar package is damaged.");
            v |= (long) (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) {
                out[0] = v;
                return i + 1;
            }
        }
        throw new ModArchives.Rejected("The rar package is damaged.");
    }

    private static long vint(byte[] b, int[] p) throws IOException {
        long v = 0;
        for (int i = 0; i < 10; i++) {
            if (p[0] >= b.length) throw new ModArchives.Rejected("The rar package is damaged.");
            int x = b[p[0]++] & 0xFF;
            v |= (long) (x & 0x7F) << (7 * i);
            if ((x & 0x80) == 0) return v;
        }
        throw new ModArchives.Rejected("The rar package is damaged.");
    }

    private static int le32(byte[] b, int o) {
        return (b[o] & 0xFF) | (b[o + 1] & 0xFF) << 8 | (b[o + 2] & 0xFF) << 16 | (b[o + 3] & 0xFF) << 24;
    }

    static final class Table {
        final int[] decodeLen = new int[16];
        final int[] decodePos = new int[16];
        final int[] decodeNum = new int[NC + 64];
        final int[] quickLen = new int[1 << QUICK_BITS];
        final int[] quickNum = new int[1 << QUICK_BITS];
        int maxNum;
        int quickBits;
    }

    static final class Filter {
        int blockStart;
        int blockLength;
        int type;
        int channels;
        boolean nextWindow;
    }

    static final class Decoder {
        final byte[] window;
        final int winSize;
        final int winMask;
        final byte[] in = new byte[MAX_SIZE + 64];
        int inAddr;
        int inBit;
        int readTop;
        int readBorder;
        int unpPtr;
        int wrPtr;
        int writeBorder;
        final long[] oldDist = new long[4];
        int lastLength;
        boolean tablesRead;
        final Table ld = new Table();
        final Table dd = new Table();
        final Table ldd = new Table();
        final Table rd = new Table();
        final Table bd = new Table();
        final List<Filter> filters = new ArrayList<>();
        int blockStart;
        int blockSize;
        int blockBitSize;
        boolean lastBlock;
        boolean tablePresent;
        boolean extraDist;
        RandomAccessFile raf;
        long packedLeft;
        long destSize;
        long written;
        OutputStream out;
        CRC32 crc;
        AtomicBoolean cancel;

        Decoder(int size) {
            window = new byte[size];
            winSize = size;
            winMask = size - 1;
        }

        void unpack(RandomAccessFile raf, Item it, OutputStream out, CRC32 crc, AtomicBoolean cancel) throws IOException {
            this.raf = raf;
            this.out = out;
            this.crc = crc;
            this.cancel = cancel;
            this.extraDist = it.extraDist;
            raf.seek(it.dataStart);
            packedLeft = it.packed;
            destSize = it.unpacked;
            written = 0;
            if (!it.solid) {
                oldDist[0] = oldDist[1] = oldDist[2] = oldDist[3] = -1;
                lastLength = 0;
                unpPtr = wrPtr = 0;
                writeBorder = Math.min(winSize, UNPACK_MAX_WRITE) & winMask;
                tablesRead = false;
            }
            filters.clear();
            inAddr = 0;
            inBit = 0;
            readTop = 0;
            readBorder = 0;
            blockSize = -1;
            blockStart = 0;
            java.util.Arrays.fill(in, (byte) 0);
            if (!readBuf()) return;
            if (!readBlockHeader() || !readTables() || !tablesRead) throw new ModArchives.Rejected("The rar package is damaged.");
            long guard = 0;
            while (true) {
                unpPtr &= winMask;
                if (((++guard) & 0xFFFF) == 0 && cancel != null && cancel.get()) throw new IOException("cancelled");
                if (inAddr >= readBorder) {
                    boolean done = false;
                    while (inAddr > blockStart + blockSize - 1 || inAddr == blockStart + blockSize - 1 && inBit >= blockBitSize) {
                        if (lastBlock) {
                            done = true;
                            break;
                        }
                        if (!readBlockHeader() || !readTables()) throw new ModArchives.Rejected("The rar package is damaged.");
                    }
                    if (done || !readBuf()) break;
                }
                if (((writeBorder - unpPtr) & winMask) < MAX_INC_LZ_MATCH && writeBorder != unpPtr) {
                    writeBuf();
                    if (written > destSize) return;
                }
                int mainSlot = decode(ld);
                if (mainSlot < 256) {
                    window[unpPtr++] = (byte) mainSlot;
                    continue;
                }
                if (mainSlot >= 262) {
                    int length = slotToLength(mainSlot - 262);
                    int distSlot = decode(dd);
                    long distance = 1;
                    int dBits;
                    if (distSlot < 4) {
                        dBits = 0;
                        distance += distSlot;
                    } else {
                        dBits = distSlot / 2 - 1;
                        distance += (long) (2 | (distSlot & 1)) << dBits;
                    }
                    if (dBits > 0) {
                        if (dBits >= 4) {
                            if (dBits > 4) {
                                if (dBits > 36) {
                                    int hi = dBits - 36;
                                    distance += ((getbits32() & 0xFFFFFFFFL) >>> (32 - hi)) << 36;
                                    addbits(hi);
                                    distance += (getbits32() & 0xFFFFFFFFL) << 4;
                                    addbits(32);
                                } else {
                                    distance += ((getbits32() & 0xFFFFFFFFL) >>> (36 - dBits)) << 4;
                                    addbits(dBits - 4);
                                }
                            }
                            distance += decode(ldd);
                        } else {
                            distance += (getbits32() & 0xFFFFFFFFL) >>> (32 - dBits);
                            addbits(dBits);
                        }
                    }
                    if (distance > 0x100) {
                        length++;
                        if (distance > 0x2000) {
                            length++;
                            if (distance > 0x40000) length++;
                        }
                    }
                    insertOldDist(distance);
                    lastLength = length;
                    copyString(length, distance);
                    continue;
                }
                if (mainSlot == 256) {
                    Filter flt = readFilter();
                    addFilter(flt);
                    continue;
                }
                if (mainSlot == 257) {
                    if (lastLength != 0) copyString(lastLength, oldDist[0]);
                    continue;
                }
                int distNum = mainSlot - 258;
                long distance = oldDist[distNum];
                for (int i = distNum; i > 0; i--) oldDist[i] = oldDist[i - 1];
                oldDist[0] = distance;
                int lengthSlot = decode(rd);
                int length = slotToLength(lengthSlot);
                lastLength = length;
                copyString(length, distance);
            }
            writeBuf();
        }

        private void insertOldDist(long d) {
            oldDist[3] = oldDist[2];
            oldDist[2] = oldDist[1];
            oldDist[1] = oldDist[0];
            oldDist[0] = d;
        }

        private void copyString(int length, long distance) throws IOException {
            if (distance <= 0 || distance > winSize) throw new ModArchives.Rejected("The rar package is damaged.");
            int src = (int) ((unpPtr - distance) & winMask);
            byte[] w = window;
            int mask = winMask;
            int dst = unpPtr;
            while (length-- > 0) {
                w[dst] = w[src];
                src = (src + 1) & mask;
                dst = (dst + 1) & mask;
            }
            unpPtr = dst;
        }

        private int slotToLength(int slot) {
            int lBits;
            int length = 2;
            if (slot < 8) {
                lBits = 0;
                length += slot;
            } else {
                lBits = slot / 4 - 1;
                length += (4 | (slot & 3)) << lBits;
            }
            if (lBits > 0) {
                length += getbits() >>> (16 - lBits);
                addbits(lBits);
            }
            return length;
        }

        private int getbits() {
            int b = (in[inAddr] & 0xFF) << 16 | (in[inAddr + 1] & 0xFF) << 8 | (in[inAddr + 2] & 0xFF);
            b >>>= (8 - inBit);
            return b & 0xFFFF;
        }

        private int getbits32() {
            int b = (in[inAddr] & 0xFF) << 24 | (in[inAddr + 1] & 0xFF) << 16 | (in[inAddr + 2] & 0xFF) << 8 | (in[inAddr + 3] & 0xFF);
            b <<= inBit;
            b |= (in[inAddr + 4] & 0xFF) >>> (8 - inBit);
            return b;
        }

        private void addbits(int bits) {
            bits += inBit;
            inAddr += bits >> 3;
            inBit = bits & 7;
        }

        private boolean readBuf() throws IOException {
            int dataSize = readTop - inAddr;
            if (dataSize < 0) return false;
            blockStart -= inAddr;
            if (inAddr > MAX_SIZE / 2) {
                if (dataSize > 0) System.arraycopy(in, inAddr, in, 0, dataSize);
                inAddr = 0;
                readTop = dataSize;
            } else {
                dataSize = readTop;
            }
            int read = 0;
            if (MAX_SIZE != dataSize && packedLeft > 0) {
                int want = (int) Math.min(MAX_SIZE - dataSize, packedLeft);
                read = raf.read(in, dataSize, want);
                if (read > 0) packedLeft -= read;
            }
            if (read > 0) readTop += read;
            java.util.Arrays.fill(in, readTop, in.length, (byte) 0);
            readBorder = readTop - 30;
            blockStart += inAddr;
            if (blockSize != -1) readBorder = Math.min(readBorder, blockStart + blockSize - 1);
            return read != -1;
        }

        private boolean readBlockHeader() throws IOException {
            if (inAddr > readTop - 7) if (!readBuf()) return false;
            addbits((8 - inBit) & 7);
            int flags = getbits() >>> 8;
            addbits(8);
            int byteCount = ((flags >> 3) & 3) + 1;
            if (byteCount == 4) return false;
            blockBitSize = (flags & 7) + 1;
            int saved = getbits() >>> 8;
            addbits(8);
            int size = 0;
            for (int i = 0; i < byteCount; i++) {
                size += (getbits() >>> 8) << (i * 8);
                addbits(8);
            }
            int check = (0x5A ^ flags ^ size ^ (size >> 8) ^ (size >> 16)) & 0xFF;
            if (check != saved) return false;
            blockSize = size;
            blockStart = inAddr;
            readBorder = Math.min(readBorder, blockStart + blockSize - 1);
            lastBlock = (flags & 0x40) != 0;
            tablePresent = (flags & 0x80) != 0;
            return true;
        }

        private boolean readTables() throws IOException {
            if (!tablePresent) return true;
            if (inAddr > readTop - 25) if (!readBuf()) return false;
            byte[] bitLength = new byte[BC];
            for (int i = 0; i < BC; i++) {
                int length = getbits() >>> 12;
                addbits(4);
                if (length == 15) {
                    int zero = getbits() >>> 12;
                    addbits(4);
                    if (zero == 0) bitLength[i] = 15;
                    else {
                        zero += 2;
                        while (zero-- > 0 && i < BC) bitLength[i++] = 0;
                        i--;
                    }
                } else bitLength[i] = (byte) length;
            }
            makeTables(bitLength, 0, bd, BC);
            int dc = extraDist ? DCX : DCB;
            int size = NC + dc + LDC + RC;
            byte[] table = new byte[size];
            for (int i = 0; i < size; ) {
                if (inAddr > readTop - 5) if (!readBuf()) return false;
                int number = decode(bd);
                if (number < 16) {
                    table[i++] = (byte) number;
                } else if (number < 18) {
                    int n;
                    if (number == 16) {
                        n = (getbits() >>> 13) + 3;
                        addbits(3);
                    } else {
                        n = (getbits() >>> 9) + 11;
                        addbits(7);
                    }
                    if (i == 0) return false;
                    while (n-- > 0 && i < size) {
                        table[i] = table[i - 1];
                        i++;
                    }
                } else {
                    int n;
                    if (number == 18) {
                        n = (getbits() >>> 13) + 3;
                        addbits(3);
                    } else {
                        n = (getbits() >>> 9) + 11;
                        addbits(7);
                    }
                    while (n-- > 0 && i < size) table[i++] = 0;
                }
            }
            tablesRead = true;
            if (inAddr > readTop) return false;
            makeTables(table, 0, ld, NC);
            makeTables(table, NC, dd, dc);
            makeTables(table, NC + dc, ldd, LDC);
            makeTables(table, NC + dc + LDC, rd, RC);
            return true;
        }

        private void makeTables(byte[] lengths, int off, Table t, int size) {
            t.maxNum = size;
            int[] count = new int[16];
            for (int i = 0; i < size; i++) count[lengths[off + i] & 0xF]++;
            count[0] = 0;
            java.util.Arrays.fill(t.decodeNum, 0);
            t.decodePos[0] = 0;
            t.decodeLen[0] = 0;
            int upper = 0;
            for (int i = 1; i < 16; i++) {
                upper += count[i];
                int left = upper << (16 - i);
                upper *= 2;
                t.decodeLen[i] = left;
                t.decodePos[i] = t.decodePos[i - 1] + count[i - 1];
            }
            int[] copy = t.decodePos.clone();
            for (int i = 0; i < size; i++) {
                int len = lengths[off + i] & 0xF;
                if (len != 0) {
                    int last = copy[len];
                    if (last < t.decodeNum.length) t.decodeNum[last] = i;
                    copy[len]++;
                }
            }
            t.quickBits = size == NC ? QUICK_BITS : QUICK_BITS - 3;
            int quickSize = 1 << t.quickBits;
            int cur = 1;
            for (int code = 0; code < quickSize; code++) {
                int field = code << (16 - t.quickBits);
                while (cur < 16 && field >= t.decodeLen[cur]) cur++;
                t.quickLen[code] = cur;
                int dist = field - t.decodeLen[cur - 1];
                dist >>>= (16 - cur);
                int pos;
                if (cur < 16 && (pos = t.decodePos[cur] + dist) < size) t.quickNum[code] = t.decodeNum[pos];
                else t.quickNum[code] = 0;
            }
        }

        private int decode(Table t) {
            int field = getbits() & 0xFFFE;
            if (field < t.decodeLen[t.quickBits]) {
                int code = field >>> (16 - t.quickBits);
                addbits(t.quickLen[code]);
                return t.quickNum[code];
            }
            int bits = 15;
            for (int i = t.quickBits + 1; i < 15; i++) {
                if (field < t.decodeLen[i]) {
                    bits = i;
                    break;
                }
            }
            addbits(bits);
            int dist = field - t.decodeLen[bits - 1];
            dist >>>= (16 - bits);
            int pos = t.decodePos[bits] + dist;
            if (pos >= t.maxNum) pos = 0;
            return t.decodeNum[pos];
        }

        private Filter readFilter() throws IOException {
            if (inAddr > readTop - 16) readBuf();
            Filter f = new Filter();
            f.blockStart = readFilterData();
            f.blockLength = readFilterData();
            if (f.blockLength > MAX_FILTER_BLOCK) f.blockLength = 0;
            f.type = getbits() >>> 13;
            addbits(3);
            if (f.type == FILTER_DELTA) {
                f.channels = (getbits() >>> 11) + 1;
                addbits(5);
            }
            return f;
        }

        private int readFilterData() {
            int count = (getbits() >>> 14) + 1;
            addbits(2);
            int data = 0;
            for (int i = 0; i < count; i++) {
                data += (getbits() >>> 8) << (i * 8);
                addbits(8);
            }
            return data;
        }

        private void addFilter(Filter f) throws IOException {
            if (filters.size() >= MAX_FILTERS) {
                writeBuf();
                if (filters.size() >= MAX_FILTERS) filters.clear();
            }
            f.nextWindow = wrPtr != unpPtr && ((wrPtr - unpPtr) & winMask) <= f.blockStart;
            f.blockStart = (f.blockStart + unpPtr) & winMask;
            filters.add(f);
        }

        private void writeBuf() throws IOException {
            int start = wrPtr;
            int writeSize = (unpPtr - start) & winMask;
            for (int i = 0; i < filters.size(); i++) {
                Filter f = filters.get(i);
                if (f.type == FILTER_NONE) continue;
                if (f.nextWindow) {
                    if (((f.blockStart - wrPtr) & winMask) <= writeSize) f.nextWindow = false;
                    continue;
                }
                int bs = f.blockStart;
                int bl = f.blockLength;
                if (((bs - wrPtr) & winMask) < writeSize) {
                    if (wrPtr != bs) {
                        writeArea(wrPtr, bs);
                        wrPtr = bs;
                        writeSize = (unpPtr - wrPtr) & winMask;
                    }
                    if (bl <= writeSize) {
                        int be = (bs + bl) & winMask;
                        byte[] mem = new byte[bl];
                        if (bs < be || be == 0) {
                            System.arraycopy(window, bs, mem, 0, bl);
                        } else {
                            int first = winSize - bs;
                            System.arraycopy(window, bs, mem, 0, first);
                            System.arraycopy(window, 0, mem, first, be);
                        }
                        byte[] outMem = applyFilter(mem, bl, f);
                        f.type = FILTER_NONE;
                        if (outMem != null) writeData(outMem, 0, bl);
                        wrPtr = be;
                        writeSize = (unpPtr - wrPtr) & winMask;
                    } else {
                        writeBorder = bs;
                        return;
                    }
                }
            }
            for (int i = filters.size() - 1; i >= 0; i--) if (filters.get(i).type == FILTER_NONE) filters.remove(i);
            writeArea(wrPtr, unpPtr);
            wrPtr = unpPtr;
            writeBorder = (unpPtr + Math.min(winSize, UNPACK_MAX_WRITE)) & winMask;
            if (writeBorder == unpPtr || wrPtr != unpPtr && ((wrPtr - unpPtr) & winMask) < ((writeBorder - unpPtr) & winMask)) writeBorder = wrPtr;
        }

        private void writeArea(int start, int end) throws IOException {
            if (end < start) {
                writeData(window, start, winSize - start);
                writeData(window, 0, end);
            } else {
                writeData(window, start, end - start);
            }
        }

        private void writeData(byte[] data, int off, int size) throws IOException {
            if (written >= destSize) {
                written += size;
                return;
            }
            long left = destSize - written;
            int n = (int) Math.min(size, left);
            if (n > 0) {
                out.write(data, off, n);
                crc.update(data, off, n);
            }
            written += size;
        }

        private byte[] applyFilter(byte[] data, int size, Filter f) {
            switch (f.type) {
                case FILTER_E8:
                case FILTER_E8E9: {
                    int fileOffset = (int) written;
                    final int fileSize = 0x1000000;
                    int cmp2 = f.type == FILTER_E8E9 ? 0xE9 : 0xE8;
                    int p = 0;
                    for (int cur = 0; cur + 4 < size; ) {
                        int b = data[p++] & 0xFF;
                        cur++;
                        if (b == 0xE8 || b == cmp2) {
                            long offset = ((cur + (fileOffset & 0xFFFFFFFFL)) % fileSize);
                            int addr = (data[p] & 0xFF) | (data[p + 1] & 0xFF) << 8 | (data[p + 2] & 0xFF) << 16 | (data[p + 3] & 0xFF) << 24;
                            if ((addr & 0x80000000) != 0) {
                                if (((addr + offset) & 0x80000000L) == 0) put4(data, p, (int) (addr + fileSize));
                            } else if (((addr - fileSize) & 0x80000000) != 0) {
                                put4(data, p, (int) (addr - offset));
                            }
                            p += 4;
                            cur += 4;
                        }
                    }
                    return data;
                }
                case FILTER_ARM: {
                    int fileOffset = (int) written;
                    for (int cur = 0; cur + 3 < size; cur += 4) {
                        if ((data[cur + 3] & 0xFF) == 0xEB) {
                            int offset = (data[cur] & 0xFF) + (data[cur + 1] & 0xFF) * 0x100 + (data[cur + 2] & 0xFF) * 0x10000;
                            offset -= (int) (((fileOffset & 0xFFFFFFFFL) + cur) / 4);
                            data[cur] = (byte) offset;
                            data[cur + 1] = (byte) (offset >> 8);
                            data[cur + 2] = (byte) (offset >> 16);
                        }
                    }
                    return data;
                }
                case FILTER_DELTA: {
                    int channels = f.channels;
                    int src = 0;
                    byte[] dst = new byte[size];
                    for (int ch = 0; ch < channels; ch++) {
                        byte prev = 0;
                        for (int d = ch; d < size; d += channels) dst[d] = prev = (byte) (prev - data[src++]);
                    }
                    return dst;
                }
                default:
                    return null;
            }
        }

        private static void put4(byte[] b, int o, int v) {
            b[o] = (byte) v;
            b[o + 1] = (byte) (v >> 8);
            b[o + 2] = (byte) (v >> 16);
            b[o + 3] = (byte) (v >> 24);
        }
    }
}
