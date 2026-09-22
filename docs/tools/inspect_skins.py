"""Read-only inventory of TTPlayer skin packages; creates planning evidence only."""
from pathlib import Path
from io import BytesIO
import collections
import hashlib
import json
import re
import zipfile
import xml.etree.ElementTree as ET
from PIL import Image

SOURCE = Path(r"E:\ttplayer-cpp")
OUT = Path(__file__).resolve().parents[1]
records = []
selected = {"经典皮肤": "classic", "iBlue": "iblue", "HiFi 3.1": "hifi"}

def decode_xml(data):
    for enc in ("utf-8-sig", "gb18030"):
        try:
            return data.decode(enc), enc
        except UnicodeDecodeError:
            pass
    return data.decode("gb18030", errors="replace"), "gb18030-replacement"

for path in sorted(SOURCE.rglob("*.skn")):
    rec = {"file": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    try:
        with zipfile.ZipFile(path) as archive:
            names = {n.lower(): n for n in archive.namelist()}
            key = next(n for n in names if n.endswith("skin.xml"))
            content, rec["encoding"] = decode_xml(archive.read(names[key]))
            rec["zip_crc_error"] = archive.testzip()
            try:
                ET.fromstring(content)
                rec["strict_xml"] = True
            except ET.ParseError as exc:
                rec["strict_xml"] = False
                rec["xml_error"] = str(exc)
            # Lexical inventory still works on legacy XML with unquoted attributes.
            windows = {}
            for m in re.finditer(r"<(\w+_window)\b([^>]*)>(.*?)</\1>", content, re.S):
                tag, attrs, body = m.groups()
                info = dict(re.findall(r'([\w_]+)\s*=\s*"([^"]*)"', attrs))
                info["elements"] = {}
                for e in re.finditer(r"<([\w_]+)\b([^<>]*)/>", body, re.S):
                    info["elements"][e.group(1)] = dict(re.findall(r'([\w_]+)\s*=\s*"([^"]*)"', e.group(2)))
                image_name = info.get("image", "").lower()
                if image_name in names:
                    with Image.open(BytesIO(archive.read(names[image_name]))) as im:
                        info["bitmap_size"] = list(im.size)
                windows[tag] = info
            rec["windows"] = windows
            for term, slug in selected.items():
                if term not in path.name:
                    continue
                dest = OUT / "skin-reference" / slug
                dest.mkdir(parents=True, exist_ok=True)
                (dest / "Skin.xml.txt").write_text(content, encoding="utf-8")
                for n in names:
                    if n.endswith(".xml"):
                        txt, _ = decode_xml(archive.read(names[n]))
                        (dest / (Path(n).name + ".txt")).write_text(txt, encoding="utf-8")
                assets = {}
                for win in windows.values():
                    used = [win.get("image", "")]
                    for elem in win["elements"].values():
                        used += [v for k, v in elem.items() if k.endswith("image")]
                    for name in used:
                        if name.lower() not in names or not name.lower().endswith(".bmp"):
                            continue
                        with Image.open(BytesIO(archive.read(names[name.lower()]))) as im:
                            im = im.convert("RGBA")
                            im.putdata([(r,g,b,0 if (r,g,b)==(255,0,255) else a) for r,g,b,a in im.getdata()])
                            filename = Path(name.lower()).stem + ".png"
                            im.save(dest / filename)
                            assets[name.lower()] = {"file": filename, "size": list(im.size)}
                (dest / "layout.json").write_text(json.dumps({"source": str(path), "windows": windows, "assets": assets}, ensure_ascii=False, indent=2), encoding="utf-8")
    except Exception as exc:
        rec["error"] = str(exc)
    records.append(rec)

summary = {
    "count": len(records),
    "read_errors": sum("error" in r for r in records),
    "strict_xml_ok": sum(r.get("strict_xml", False) for r in records),
    "encodings": dict(collections.Counter(r.get("encoding") for r in records)),
    "windows": dict(collections.Counter(k for r in records for k in r.get("windows", {}))),
    "player_elements": dict(collections.Counter(k for r in records for k in r.get("windows", {}).get("player_window", {}).get("elements", {}))),
}
(OUT / "skin-inventory.json").write_text(json.dumps({"summary": summary, "skins": records}, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps(summary, ensure_ascii=False, indent=2))
for r in records:
    if not r.get("strict_xml", False):
        print("LEGACY_XML", Path(r["file"]).name, r.get("xml_error", r.get("error")))
for term, slug in selected.items():
    info = json.loads((OUT / "skin-reference" / slug / "layout.json").read_text(encoding="utf-8"))
    print(slug, {k: v.get("bitmap_size") for k,v in info["windows"].items()})
