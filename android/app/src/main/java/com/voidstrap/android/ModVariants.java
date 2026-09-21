package com.voidstrap.android;

import android.content.Context;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class ModVariants {
    private static final String INSTALLED = "Installed";
    static final Set<String> SLOT_EXTENSIONS = new HashSet<>(Arrays.asList("png", "jpg", "jpeg", "bmp", "tga", "dds", "ktx", "webp", "tex", "ogg", "mp3", "wav", "flac", "mesh", "ttf", "otf", "gif"));
    static final Set<String> PREVIEW_EXTENSIONS = new HashSet<>(Arrays.asList("png", "jpg", "jpeg", "bmp", "gif", "webp"));
    private static final Pattern SUFFIX = Pattern.compile("^(?<base>.+?)(?:\\s*\\(\\d+\\)|\\s+copy(?:\\s*\\d+)?|[\\s_-]+(?:v?\\d{1,2}|alt\\d*|variant\\d*|version\\d*))$", Pattern.CASE_INSENSITIVE);

    public static final class Option {
        public final String label;
        public final File source;

        Option(String label, File source) {
            this.label = label;
            this.source = source;
        }
    }

    public static final class Slot {
        public final String target;
        public final List<Option> options = new ArrayList<>();
        public int selected;

        Slot(String target) {
            this.target = target;
        }
    }

    private ModVariants() {
    }

    public static File sources(Context c, String id) {
        return new File(new File(ManagedMods.root(c), "Sources"), id.toLowerCase(Locale.ROOT));
    }

    public static boolean hasSlots(Context c, String id) {
        File s = sources(c, id);
        for (String rel : ManagedMods.files(s)) if (!rel.startsWith(INSTALLED + "/")) return true;
        for (String rel : ManagedMods.files(ManagedMods.folder(c, id))) if (SLOT_EXTENSIONS.contains(Mods.extension(rel))) return true;
        return false;
    }

    static String resolveSlot(Context c, String relative) {
        String ext = Mods.extension(relative);
        if (!SLOT_EXTENSIONS.contains(ext)) return null;
        String direct = ContentPlacer.resolve(c, relative);
        if (direct != null) return direct;
        String file = relative.substring(relative.lastIndexOf('/') + 1);
        String stem = file.contains(".") ? file.substring(0, file.lastIndexOf('.')) : file;
        Matcher m = SUFFIX.matcher(stem);
        if (!m.matches()) return null;
        String dir = relative.contains("/") ? relative.substring(0, relative.lastIndexOf('/') + 1) : "";
        return ContentPlacer.resolve(c, dir + m.group(1).trim() + "." + ext);
    }

    public static int capture(Context c, String id, File archive, AtomicBoolean cancel) throws IOException {
        File root = sources(c, id);
        String canonical = root.getCanonicalPath();
        int[] written = {0};
        long[] total = {0};
        ModArchives.read(archive, (name, size, data) -> {
            String n = ModArchives.normalize(name);
            if (!SLOT_EXTENSIONS.contains(Mods.extension(n)) || ModArchives.inspectPath(c, n) != null) return;
            File out = new File(root, n);
            if (!out.getCanonicalPath().startsWith(canonical + File.separator)) return;
            File parent = out.getParentFile();
            if (parent != null && !parent.isDirectory() && !parent.mkdirs()) return;
            try (OutputStream os = new FileOutputStream(out)) {
                byte[] buf = new byte[65536];
                int r;
                while ((r = data.read(buf)) > 0) {
                    total[0] += r;
                    if (total[0] > ModArchives.MAX_EXTRACTED) throw new IOException("too large");
                    os.write(buf, 0, r);
                }
            }
            written[0]++;
        }, cancel);
        return written[0];
    }

    public static List<Slot> build(Context c, String id) {
        File mod = ManagedMods.folder(c, id);
        File src = sources(c, id);
        captureInstalled(mod, src);
        Map<String, List<File>> groups = new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
        for (String rel : ManagedMods.files(src)) {
            String slot = rel.startsWith(INSTALLED + "/") ? rel.substring(INSTALLED.length() + 1) : resolveSlot(c, rel);
            if (slot == null || !SLOT_EXTENSIONS.contains(Mods.extension(slot))) continue;
            List<File> files = groups.get(slot);
            if (files == null) groups.put(slot, files = new ArrayList<>());
            files.add(new File(src, rel));
        }
        List<Slot> out = new ArrayList<>();
        for (Map.Entry<String, List<File>> g : groups.entrySet()) {
            Slot slot = new Slot(g.getKey());
            slot.options.add(new Option(null, null));
            Map<String, File> seen = new HashMap<>();
            List<File> ordered = new ArrayList<>(g.getValue());
            ordered.sort((a, b) -> {
                int ia = installedCopy(src, a) ? 1 : 0;
                int ib = installedCopy(src, b) ? 1 : 0;
                return ia != ib ? ia - ib : a.getName().compareToIgnoreCase(b.getName());
            });
            for (File f : ordered) {
                String hash = ModPresets.sha256(f);
                if (hash.isEmpty() || seen.containsKey(hash)) continue;
                seen.put(hash, f);
                String label = installedCopy(src, f) ? "" : f.getName();
                for (Option o : slot.options) {
                    if (label.equals(o.label)) {
                        File parent = f.getParentFile();
                        label += " (" + (parent == null ? "" : parent.getName()) + ")";
                        break;
                    }
                }
                slot.options.add(new Option(label, f));
            }
            File installed = new File(mod, slot.target);
            if (installed.isFile()) {
                File match = seen.get(ModPresets.sha256(installed));
                for (int i = 0; i < slot.options.size(); i++) if (match != null && match.equals(slot.options.get(i).source)) slot.selected = i;
            }
            out.add(slot);
        }
        return out;
    }

    public static int apply(Context c, String id, List<Slot> slots) {
        File mod = ManagedMods.folder(c, id);
        int changed = 0;
        for (Slot s : slots) {
            File target = new File(mod, s.target);
            if (!Mods.inside(mod, target)) continue;
            File source = s.options.get(s.selected).source;
            try {
                if (source == null) {
                    if (target.isFile() && target.delete()) changed++;
                    continue;
                }
                if (target.isFile() && ModPresets.sha256(target).equals(ModPresets.sha256(source))) continue;
                ModPresets.copy(source, target);
                changed++;
            } catch (IOException ignored) {
            }
        }
        return changed;
    }

    public static void delete(Context c, String id) {
        Mods.delete(sources(c, id));
    }

    private static void captureInstalled(File mod, File src) {
        Set<String> known = new HashSet<>();
        for (String rel : ManagedMods.files(src)) known.add(ModPresets.sha256(new File(src, rel)));
        for (String rel : ManagedMods.files(mod)) {
            if (!SLOT_EXTENSIONS.contains(Mods.extension(rel))) continue;
            File f = new File(mod, rel);
            String hash = ModPresets.sha256(f);
            if (hash.isEmpty() || known.contains(hash)) continue;
            try {
                ModPresets.copy(f, new File(new File(src, INSTALLED), rel));
                known.add(hash);
            } catch (IOException ignored) {
            }
        }
    }

    private static boolean installedCopy(File src, File f) {
        return f.getPath().startsWith(new File(src, INSTALLED).getPath() + File.separator);
    }
}
