"""Draws Cuedeck's logo, the Spotlight C, at each size the app uses (title bar, window icon) and as logo-source.png:
a C that's a lighting fixture, its lens lit, its beam spilling out of the C's opening, on a dark rounded tile. The
title bar's 16 and 22 pixel sizes are drawn bolder, with a wider beam and a bigger lens, so they still read. Each is
drawn sixteen times over and scaled down, for smooth edges. The 16, 32, 48 and 256 pixel sizes are also gathered into
logo.ico, the executable's icon in Explorer and the taskbar. Needs Pillow.

    python make_logo.py
"""

import math
import os
import struct

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))

TILE = (0x16, 0x17, 0x1D, 255)
EDGE = (0x2E, 0x30, 0x39, 255)
ACCENT = (0xEB, 0x78, 0x28)
LENS = (0xFF, 0xE2, 0xB0, 255)

# The drawing, in a 32 unit square: (the C's gap either side of its opening in degrees, its stroke, the lens's
# radius, the beam's far half-height, the beam's brightness at the lens and at the edge).
LARGE = dict(gap=45, stroke=4.5, lens=2.6, spread=8, near=0.6, far=0.22)
SMALL = dict(gap=40, stroke=5.6, lens=3.3, spread=9, near=0.75, far=0.35)

SUPER = 16


def draw(size, style):
    n = size * SUPER
    u = n / 32  # one unit of the 32 unit drawing

    # The tile, with a faint edge so it stands off a dark taskbar.
    tile = Image.new("L", (n, n), 0)
    ImageDraw.Draw(tile).rounded_rectangle((0, 0, n - 1, n - 1), radius=7 * u, fill=255)
    image = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    d.rounded_rectangle((0, 0, n - 1, n - 1), radius=7 * u, fill=EDGE)
    d.rounded_rectangle((0.6 * u, 0.6 * u, n - 1 - 0.6 * u, n - 1 - 0.6 * u), radius=6.4 * u, fill=TILE)

    # The beam: a wedge out of the C's opening to the tile's edge, fading as it goes.
    beam = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    apex = (19 * u, 17 * u)
    for x in range(int(apex[0]), n):
        t = (x - apex[0]) / (n - apex[0])
        half = style["spread"] * u * t
        alpha = int(255 * (style["near"] + (style["far"] - style["near"]) * t))
        ImageDraw.Draw(beam).line((x, apex[1] - half, x, apex[1] + half), fill=(*ACCENT, alpha))
    beam.putalpha(Image.composite(beam.getchannel("A"), Image.new("L", (n, n), 0), tile))
    image.alpha_composite(beam)

    # The C: a thick arc open to the right, its ends rounded.
    cx, cy, r, w = 16 * u, 17 * u, 9 * u, style["stroke"] * u
    outer = r + w / 2
    d = ImageDraw.Draw(image)
    d.arc((cx - outer, cy - outer, cx + outer, cy + outer), style["gap"], 360 - style["gap"], fill=(*ACCENT, 255), width=round(w))
    for angle in (style["gap"], -style["gap"]):
        ex, ey = cx + r * math.cos(math.radians(angle)), cy + r * math.sin(math.radians(angle))
        d.ellipse((ex - w / 2, ey - w / 2, ex + w / 2, ey + w / 2), fill=(*ACCENT, 255))

    # The lens, lit.
    lx, ly, lr = 17 * u, 17 * u, style["lens"] * u
    d.ellipse((lx - lr, ly - lr, lx + lr, ly + lr), fill=LENS)

    return image.resize((size, size), Image.LANCZOS)


def write_ico(path, pngs):
    """An icon file holding PNG images, as Windows Vista and later read them: pngs is (size, path) pairs."""
    images = [(size, open(png, "rb").read()) for size, png in pngs]
    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for size, data in images:
        # A size of 256 is written as 0.
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        out += data
    with open(path, "wb") as f:
        f.write(out)


def main():
    draw(512, LARGE).save(os.path.join(HERE, "logo-source.png"), optimize=True)
    for size in (16, 22, 32, 48, 256):
        path = os.path.join(HERE, f"logo-{size}.png")
        draw(size, SMALL if size <= 22 else LARGE).save(path, optimize=True)
        print("wrote", path)
    ico = os.path.join(HERE, "logo.ico")
    write_ico(ico, [(size, os.path.join(HERE, f"logo-{size}.png")) for size in (16, 32, 48, 256)])
    print("wrote", ico)


if __name__ == "__main__":
    main()
