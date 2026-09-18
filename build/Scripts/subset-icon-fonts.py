import os
import re
import sys

from fontTools import subset

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
WPFUI = os.path.join(ROOT, "external", "wpfui", "src", "Wpf.Ui")
SCAN = [os.path.join(ROOT, "src"), os.path.join(ROOT, "external", "wpfui", "src")]
SKIP = {"obj", "bin", ".vs", ".git", "node_modules"}
EXTENSIONS = (".cs", ".xaml", ".json", ".resx", ".xml", ".js")


def read_enum(name):
    text = open(os.path.join(WPFUI, "Common", name), encoding="utf-8").read()
    return {m.group(1): int(m.group(2), 16) for m in re.finditer(r"(\w+)\s*=\s*0x([0-9A-Fa-f]+)", text)}


def used_names(known):
    token = re.compile(r"\b[A-Z][A-Za-z0-9]*\b")
    found = set()
    for base in SCAN:
        for folder, dirs, files in os.walk(base):
            dirs[:] = [d for d in dirs if d not in SKIP and not d.startswith(("bin-", "obj-"))]
            for file in files:
                if file in ("SymbolRegular.cs", "SymbolFilled.cs") or not file.endswith(EXTENSIONS):
                    continue
                with open(os.path.join(folder, file), encoding="utf-8", errors="ignore") as handle:
                    found |= {w for w in token.findall(handle.read()) if w in known}
    return found


def main():
    regular = read_enum("SymbolRegular.cs")
    filled = read_enum("SymbolFilled.cs")
    names = used_names(set(regular) | set(filled))
    for font, table in (("Regular", regular), ("Filled", filled)):
        codepoints = sorted({table[n] for n in names if n in table and table[n] > 0})
        options = subset.Options()
        options.layout_features = ["*"]
        options.name_IDs = ["*"]
        options.notdef_outline = True
        options.glyph_names = False
        source = os.path.join(WPFUI, "Fonts", "Source", f"FluentSystemIcons-{font}.ttf")
        target = os.path.join(WPFUI, "Fonts", f"FluentSystemIcons-{font}.ttf")
        loaded = subset.load_font(source, options)
        subsetter = subset.Subsetter(options)
        subsetter.populate(unicodes=codepoints)
        subsetter.subset(loaded)
        subset.save_font(loaded, target, options)
        print(f"{font}: {len(codepoints)} glyphs, {os.path.getsize(source) // 1024} KB to {os.path.getsize(target) // 1024} KB")
    print(f"{len(names)} icon names in use")


if __name__ == "__main__":
    sys.exit(main())
