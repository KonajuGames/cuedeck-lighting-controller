"""Makes the logo's PNGs (title bar, window icon) from logo-source.png, sLight's 512 pixel icon, at each size the app
uses. Needs Pillow.

    python make_logo.py
"""

import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    source = Image.open(os.path.join(HERE, "logo-source.png")).convert("RGBA")
    for size in (16, 22, 32, 48, 256):
        path = os.path.join(HERE, f"logo-{size}.png")
        source.resize((size, size), Image.LANCZOS).save(path, optimize=True)
        print("wrote", path)


if __name__ == "__main__":
    main()
