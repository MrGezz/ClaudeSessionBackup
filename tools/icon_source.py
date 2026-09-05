# -*- coding: utf-8 -*-
"""
icon_source.py - draw the Claude Session Backup app icon.

This is the SOURCE of src/ClaudeSessionBackup.App/Assets/app.ico. The .ico is a
build output, not an asset to hand-edit: change the drawing here and regenerate.

    python tools/icon_source.py && python tools/build_icon.py

THE DESIGN, and why:

  * A SPEECH BUBBLE with a DOWNWARD ARROW cut out of it. The bubble is the
    thing being protected (a conversation); the arrow is the verb (put it
    somewhere safe). Two shapes only, so it survives 16 px.

  * Same tile rule as the sibling ACC Sync icon: the tile is the CM2 ACCENT
    (#00BCD4) and the mark is the palette's AccentInk (#0B0E14). A saturated
    cyan tile is unmistakable on both a light and a dark taskbar; white on this
    accent fails WCAG AA (1.9:1), the near-black passes (8.4:1).

  * The arrow is a CUT-OUT (tile colour showing through the ink), not a second
    ink shape: at 16 px two adjacent ink shapes fuse into a blob, while a hole
    in a solid shape stays readable because it is bounded by ink on all sides.

  * 16x16 IS THE DESIGN TARGET - taskbar, Alt+Tab, window corner, Explorer.
    Bubble ~11x8 px, arrow shaft 3 px wide, head 7 px wide at that size.

  * Drawn once at 1024 and downscaled with LANCZOS to each size; drawing
    directly at 16 gives jagged edges.
"""
from __future__ import annotations

import os
import sys

from PIL import Image, ImageDraw

SIZES = [256, 128, 64, 48, 32, 16]

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO, "build", "icon")

# CM2 palette
ACCENT = (0, 188, 212, 255)   # #00BCD4
INK = (11, 14, 20, 255)       # #0B0E14  AccentInk
CLEAR = (0, 0, 0, 0)

MASTER = 1024

# Geometry as fractions of the tile.
TILE_RADIUS = 0.22          # rounded tile corner
BUB_L, BUB_T = 0.16, 0.14   # bubble box
BUB_R, BUB_B = 0.84, 0.66
BUB_RADIUS = 0.13
TAIL_W = 0.20               # tail base width along the bubble bottom
TAIL_H = 0.16               # tail drop below the bubble
TAIL_X = 0.30               # where the tail starts (left edge)
ARROW_CX = 0.50
ARROW_TOP = 0.24            # shaft top
ARROW_SHAFT_W = 0.13
ARROW_HEAD_W = 0.36
ARROW_HEAD_TOP = 0.43       # where the head begins
ARROW_TIP = 0.58            # arrow tip y (inside the bubble)


def draw_master() -> Image.Image:
    s = MASTER
    img = Image.new("RGBA", (s, s), CLEAR)
    d = ImageDraw.Draw(img)

    # tile
    r = int(TILE_RADIUS * s)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=ACCENT)

    # bubble (ink) + tail
    l, t, rr, b = (int(BUB_L * s), int(BUB_T * s), int(BUB_R * s), int(BUB_B * s))
    d.rounded_rectangle([l, t, rr, b], radius=int(BUB_RADIUS * s), fill=INK)
    tx = int(TAIL_X * s)
    d.polygon([(tx, b - int(0.02 * s)), (tx + int(TAIL_W * s), b - int(0.02 * s)), (tx, b + int(TAIL_H * s))], fill=INK)

    # arrow cut-out: paint the tile colour back over the ink
    cx = int(ARROW_CX * s)
    sw = int(ARROW_SHAFT_W * s) // 2
    d.rectangle([cx - sw, int(ARROW_TOP * s), cx + sw, int(ARROW_HEAD_TOP * s) + sw], fill=ACCENT)
    hw = int(ARROW_HEAD_W * s) // 2
    d.polygon([(cx - hw, int(ARROW_HEAD_TOP * s)), (cx + hw, int(ARROW_HEAD_TOP * s)), (cx, int(ARROW_TIP * s))], fill=ACCENT)
    return img


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT
    os.makedirs(out, exist_ok=True)
    master = draw_master()
    master.save(os.path.join(out, "icon_master_1024.png"))
    for size in SIZES:
        master.resize((size, size), Image.Resampling.LANCZOS).save(os.path.join(out, f"icon_{size}.png"))
    print(f"rendered {len(SIZES)} sizes + master to {out}")


if __name__ == "__main__":
    main()
