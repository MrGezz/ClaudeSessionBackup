# -*- coding: utf-8 -*-
"""
build_icon.py - assemble src/ClaudeSessionBackup.App/Assets/app.ico from per-size PNGs.

Each size is rendered separately by icon_source.py from a supersampled master and
packed as-is: the ICO plugin's own resize would resample one image, and a mark tuned
for 256 turns muddy at 16, which is the size the taskbar actually shows.

    python tools/icon_source.py && python tools/build_icon.py
    python tools/build_icon.py <dir-of-icon_NNN.png>
"""
from __future__ import annotations

import os
import sys

from PIL import Image

SIZES = [256, 128, 64, 48, 32, 16]

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "src", "ClaudeSessionBackup.App", "Assets")
OUT_ICO = os.path.join(OUT_DIR, "app.ico")


def load(src_dir: str, size: int) -> Image.Image:
    path = os.path.join(src_dir, f"icon_{size}.png")
    if not os.path.exists(path):
        raise SystemExit(f"missing {path}")
    img = Image.open(path).convert("RGBA")
    if img.size != (size, size):
        img = img.resize((size, size), Image.Resampling.LANCZOS)
    return img


def main() -> None:
    src_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "build", "icon")
    if not os.path.isdir(src_dir):
        raise SystemExit(f"no rendered PNGs at {src_dir}\nrun: python tools/icon_source.py")
    os.makedirs(OUT_DIR, exist_ok=True)
    images = [load(src_dir, s) for s in SIZES]
    # Pillow writes one ICO directory entry per size in append_images (+ the first image).
    images[0].save(OUT_ICO, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[1:])
    check = Image.open(OUT_ICO)
    print(f"wrote {OUT_ICO} ({os.path.getsize(OUT_ICO)} bytes) sizes={sorted(check.ico.sizes())}")


if __name__ == "__main__":
    main()
