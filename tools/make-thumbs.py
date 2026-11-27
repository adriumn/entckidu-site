"""Make small preview copies of the drawings for the grid on drawings.html.

Run from the repo root after adding drawings:   python3 tools/make-thumbs.py
For every site/images/drawings/*.jpg it writes site/images/drawings/thumbs/<same name>
(at most 600 px wide and 800 px high, JPEG quality 70). Existing previews that are
newer than their drawing are skipped, so it only does the new ones.
"""
import glob, os
from PIL import Image

SRC = "site/images/drawings"
DST = os.path.join(SRC, "thumbs")
os.makedirs(DST, exist_ok=True)

made = 0
for src in sorted(glob.glob(os.path.join(SRC, "*.jpg"))):
    dst = os.path.join(DST, os.path.basename(src))
    if os.path.exists(dst) and os.path.getmtime(dst) >= os.path.getmtime(src):
        continue
    im = Image.open(src).convert("RGB")
    im.thumbnail((600, 800), Image.LANCZOS)
    im.save(dst, "JPEG", quality=70, optimize=True, progressive=True)
    made += 1
    print(f"{os.path.basename(src):40s} {im.size[0]}x{im.size[1]}  {os.path.getsize(dst)//1024} KB")
print(f"{made} preview(s) made")
