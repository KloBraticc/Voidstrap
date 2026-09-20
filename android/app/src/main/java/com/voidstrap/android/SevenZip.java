package com.voidstrap.android;

import org.apache.commons.compress.archivers.sevenz.SevenZArchiveEntry;
import org.apache.commons.compress.archivers.sevenz.SevenZFile;
import org.apache.commons.compress.PasswordRequiredException;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FilterInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.concurrent.atomic.AtomicBoolean;

final class SevenZip {
    private SevenZip() {
    }

    static void read(File f, ModArchives.Handler h, AtomicBoolean cancel) throws IOException {
        int count = 0;
        try (SevenZFile sz = SevenZFile.builder().setFile(f).setMaxMemoryLimitKiB(256 * 1024).get()) {
            SevenZArchiveEntry e;
            while ((e = sz.getNextEntry()) != null) {
                if (cancel != null && cancel.get()) throw new IOException("cancelled");
                if (e.isDirectory() || e.isAntiItem()) continue;
                if (e.getSize() > ModArchives.MAX_EXTRACTED) throw new ModArchives.Rejected("The package expands to more than " + ModArchives.MAX_EXTRACTED / 1048576 + " MB.");
                if (++count > ModArchives.MAX_ENTRIES) throw new ModArchives.Rejected("The package contains more than " + ModArchives.MAX_ENTRIES + " files.");
                if (!e.hasStream()) {
                    h.entry(e.getName(), 0, new ByteArrayInputStream(new byte[0]));
                    continue;
                }
                InputStream in = sz.getInputStream(e);
                h.entry(e.getName(), e.getSize(), new FilterInputStream(in) {
                    @Override
                    public void close() {
                    }
                });
            }
        } catch (PasswordRequiredException e) {
            throw new ModArchives.Rejected("The 7z package is password protected.");
        } catch (org.apache.commons.compress.MemoryLimitException e) {
            throw new ModArchives.Rejected("The 7z package needs more memory than this device allows.");
        } catch (RuntimeException e) {
            throw new ModArchives.Rejected("The 7z package is damaged.");
        }
    }
}
