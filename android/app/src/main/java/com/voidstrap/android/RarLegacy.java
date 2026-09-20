package com.voidstrap.android;

import com.github.junrar.Archive;
import com.github.junrar.exception.RarException;
import com.github.junrar.rarfile.FileHeader;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.concurrent.atomic.AtomicBoolean;

final class RarLegacy {
    private RarLegacy() {
    }

    static void read(File f, ModArchives.Handler h, AtomicBoolean cancel) throws IOException {
        int count = 0;
        try (Archive archive = new Archive(f)) {
            if (archive.isEncrypted()) throw new ModArchives.Rejected("The rar package is password protected.");
            FileHeader fh;
            while ((fh = archive.nextFileHeader()) != null) {
                if (cancel != null && cancel.get()) throw new IOException("cancelled");
                if (fh.isDirectory()) continue;
                if (fh.isEncrypted()) throw new ModArchives.Rejected("The rar package is password protected.");
                if (fh.isSplitAfter() || fh.isSplitBefore()) throw new ModArchives.Rejected("Split rar packages are not supported. Use a single file package.");
                if (fh.getFullUnpackSize() > ModArchives.MAX_EXTRACTED) throw new ModArchives.Rejected("The package expands to more than " + ModArchives.MAX_EXTRACTED / 1048576 + " MB.");
                if (++count > ModArchives.MAX_ENTRIES) throw new ModArchives.Rejected("The package contains more than " + ModArchives.MAX_ENTRIES + " files.");
                String name = fh.getFileName().replace('\\', '/');
                File tmp = File.createTempFile("rar4", ".part", f.getParentFile());
                try {
                    try (OutputStream out = new FileOutputStream(tmp)) {
                        archive.extractFile(fh, out);
                    }
                    try (InputStream in = new FileInputStream(tmp)) {
                        h.entry(name, tmp.length(), in);
                    }
                } finally {
                    tmp.delete();
                }
            }
        } catch (RarException e) {
            throw new ModArchives.Rejected("The rar package is damaged or uses an unsupported feature.");
        } catch (RuntimeException e) {
            throw new ModArchives.Rejected("The rar package is damaged.");
        }
    }
}
