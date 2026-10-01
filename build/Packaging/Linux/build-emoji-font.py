import hashlib
import sys
import urllib.request
from pathlib import Path

import uharfbuzz
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables._g_l_y_f import Glyph

FONT_URL = "https://github.com/mozilla/twemoji-colr/releases/download/v0.7.0/Twemoji.Mozilla.ttf"
FONT_SHA256 = "6d90152ee0d29e82fe2a87793af5aa4b7ad13e6538360889e141e81ed299ee8e"
LIST_URL = "https://unicode.org/Public/emoji/14.0/emoji-test.txt"
LIST_SHA256 = "ec474be073670aa7ce6dc1de9025b9fbb9b875fc63df815c254a5d1686fc6109"
FAMILY = "Voidstrap Emoji"
POSTSCRIPT = "VoidstrapEmoji-Regular"
PAD_CODE_POINT = 0xE000
FIRST_CODE_POINT = 0xE001
LAST_CODE_POINT = 0xF8FF
PAD_GLYPH = "voidstrap.pad"
SPACE_CODE_POINT = 0x20
SPACE_GLYPH = "voidstrap.space"


def download(url, expected):
    with urllib.request.urlopen(url, timeout=120) as response:
        data = response.read()
    actual = hashlib.sha256(data).hexdigest()
    if actual != expected:
        raise SystemExit(f"{url} has sha256 {actual}, expected {expected}")
    return data


def read_sequences(text):
    sequences = []
    seen = set()
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or ";" not in line:
            continue
        points = tuple(int(value, 16) for value in line.split(";", 1)[0].split())
        if points and points not in seen:
            seen.add(points)
            sequences.append(points)
    return sequences


def shape_single_glyph(font, points):
    buffer = uharfbuzz.Buffer()
    buffer.add_codepoints(list(points))
    buffer.guess_segment_properties()
    uharfbuzz.shape(font, buffer)
    visible = [
        info.codepoint
        for info, position in zip(buffer.glyph_infos, buffer.glyph_positions)
        if position.x_advance > 0
    ]
    if any(info.codepoint == 0 for info in buffer.glyph_infos) or len(visible) != 1:
        return None
    return visible[0]


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python3 build-emoji-font.py <output directory>")
    output = Path(sys.argv[1])
    output.mkdir(parents=True, exist_ok=True)

    font_data = download(FONT_URL, FONT_SHA256)
    sequences = read_sequences(download(LIST_URL, LIST_SHA256).decode("utf-8"))

    shaper = uharfbuzz.Font(uharfbuzz.Face(font_data))
    source = output / "source.ttf"
    source.write_bytes(font_data)
    font = TTFont(str(source))
    glyph_order = font.getGlyphOrder()

    code_point_by_glyph = {}
    lines = []
    next_code_point = FIRST_CODE_POINT
    for points in sequences:
        glyph = shape_single_glyph(shaper, points)
        if glyph is None:
            continue
        if glyph not in code_point_by_glyph:
            if next_code_point > LAST_CODE_POINT:
                raise SystemExit("The private use area is full")
            code_point_by_glyph[glyph] = next_code_point
            next_code_point += 1
        lines.append(" ".join(f"{point:X}" for point in points) + "\t" + f"{code_point_by_glyph[glyph]:X}")

    font["glyf"].glyphs[PAD_GLYPH] = Glyph()
    font["hmtx"].metrics[PAD_GLYPH] = (0, 0)
    font["glyf"].glyphs[SPACE_GLYPH] = Glyph()
    font["hmtx"].metrics[SPACE_GLYPH] = (font["head"].unitsPerEm // 4, 0)
    font.setGlyphOrder(glyph_order + [PAD_GLYPH, SPACE_GLYPH])

    mapping = {code_point: glyph_order[glyph] for glyph, code_point in code_point_by_glyph.items()}
    mapping[PAD_CODE_POINT] = PAD_GLYPH
    mapping[SPACE_CODE_POINT] = SPACE_GLYPH
    for table in font["cmap"].tables:
        if table.isUnicode() and table.format in (4, 12):
            table.cmap.update(mapping)

    for record in font["name"].names:
        if record.nameID in (1, 16):
            record.string = FAMILY
        elif record.nameID == 4:
            record.string = FAMILY
        elif record.nameID == 6:
            record.string = POSTSCRIPT
        elif record.nameID == 3:
            record.string = POSTSCRIPT + " derived from Twemoji Mozilla 0.7.0"

    font.save(str(output / "VoidstrapEmoji.ttf"))
    source.unlink()
    (output / "VoidstrapEmoji.map").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Mapped {len(lines)} emoji sequences to {len(code_point_by_glyph)} glyphs in {output}")


if __name__ == "__main__":
    main()
