"""Builds the background wall of covers for entckidu.net.

Reads the records list from site/index.html and writes the covers in a fixed
order (the page itself shuffles them on every visit):
  site/images/wall.jpg  - one picture with every cover, 15 per row
  site/images/wall.js   - which cover sits in which cell (used to light one up)
Run from the repo root:  python tools/make-wall.py   (needs Pillow)
"""
import re, json, random
from PIL import Image

COLS, T, SEED = 15, 104, 7
PX = 76          # pixels stored per cover; shown at T px (it is a dimmed background, so a little soft is fine)
html = open("site/index.html", encoding="utf-8").read()
i = html.index("var records = [")
files = re.findall(r'\["([^"]+\.jpg)"', html[i:html.index("];", i)])
random.seed(SEED); random.shuffle(files)
rows = -(-len(files) // COLS)
cells = [files[k % len(files)] for k in range(COLS * rows)]   # last row filled by repeating
wall = Image.new("RGB", (COLS * PX, rows * PX))
for k, f in enumerate(cells):
    im = Image.open("site/images/records/" + f).convert("RGB").resize((PX, PX), Image.LANCZOS)
    wall.paste(im, ((k % COLS) * PX, (k // COLS) * PX))
wall.save("site/images/wall.jpg", quality=62, optimize=True, progressive=True)
with open("site/images/wall.js", "w", encoding="utf-8") as f:
    f.write("/* made by tools/make-wall.py - do not edit by hand */\n")
    f.write("var WALL = " + json.dumps({"cols": COLS, "rows": rows, "tile": T, "cells": cells}) + ";\n")
print(len(files), "covers,", rows, "rows")
