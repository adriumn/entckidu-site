"""Builds the album list from the captions in site/index.html (var records).

Writes:
  sql/002-seed-albums.sql  - fills dbo.Albums in Azure SQL (safe to run again)
  site/data/albums.json    - the saved copy the albums page shows when the database sleeps

Run after adding records:  python3 tools/make-albums.py
"""
import json, os, re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Captions without "Artist – Album": how to split them
SPECIAL = {
    "Mark-Almond II": ("Mark-Almond", "Mark-Almond II"),
    "Formații rock 4": ("Various Artists", "Formații rock 4"),
    "Never Never Land": ("Various Artists", "Never Never Land"),
    "Experimental": ("Unknown", "Experimental"),
}

def albums():
    s = open(os.path.join(ROOT, "site", "index.html"), encoding="utf-8").read()
    a = s.index("var records = [")
    b = s.index("];", a)
    rows = re.findall(r'\[\s*"([^"]+\.jpg)"\s*,\s*"([^"]*)"', s[a:b])
    seen, out = set(), []
    for _file, caption in rows:
        if " – " in caption:
            artist, album = caption.split(" – ", 1)
        else:
            artist, album = SPECIAL.get(caption, (caption, caption))   # self-titled
        artist, album = artist.strip(), album.strip()
        key = (artist.lower(), album.lower())
        if key not in seen:                     # LP + CD of the same album = one album
            seen.add(key)
            out.append((artist, album))
    out.sort(key=lambda x: (x[0].lower(), x[1].lower()))
    return out

def write_sql(rows):
    q = lambda t: "N'" + t.replace("'", "''") + "'"
    lines = ["-- 002: fill Albums from the captions on the music page (%d albums, doubles merged)." % len(rows),
             "-- Made by tools/make-albums.py. Safe to run again: albums already in the table are skipped.", ""]
    for i in range(0, len(rows), 150):
        lines.append("INSERT INTO dbo.Albums (Artist, Album)\nSELECT v.Artist, v.Album FROM (VALUES")
        lines.append(",\n".join("    (%s, %s)" % (q(a), q(b)) for a, b in rows[i:i + 150]))
        lines.append(") AS v(Artist, Album)\nWHERE NOT EXISTS (SELECT 1 FROM dbo.Albums a WHERE a.Artist = v.Artist AND a.Album = v.Album);\n")
    lines.append("SELECT COUNT(*) AS AlbumsInTable FROM dbo.Albums;")
    with open(os.path.join(ROOT, "sql", "002-seed-albums.sql"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")

def write_json(rows):
    os.makedirs(os.path.join(ROOT, "site", "data"), exist_ok=True)
    data = [{"artist": a, "album": b} for a, b in rows]
    with open(os.path.join(ROOT, "site", "data", "albums.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))

if __name__ == "__main__":
    rows = albums()
    write_sql(rows)
    write_json(rows)
    print("%d albums -> sql/002-seed-albums.sql, site/data/albums.json" % len(rows))
