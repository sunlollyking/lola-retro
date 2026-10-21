#!/usr/bin/env python3
"""Make the Unity Asset Store pictures (unity/asset-store/images) in Unity, from the demo.

Each picture is rendered by the test project (unity/test-project, its LolaRetroTestCapture) in
batch mode, from the Demo sample's own pictures (test-images), and then cut to the sizes the
Asset Store asks for: icon 160 x 160, card 420 x 280, cover 1950 x 1300, social media 1200 x 630
(no text on any of them) and screenshots of at least 1200 wide. The Dracula comparison comes from
docs/ as it is.

Needs: Unity (--unity, or the newest editor in /Applications/Unity/Hub/Editor), the test project
with the demo imported (Lola Retro Test, Import the demo), pip install pillow.

Usage: tools/make_store_images.py [--unity PATH] [--only NAME ...] [--keep DIR]
"""
import argparse
import subprocess
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "unity" / "test-project"
OUT = ROOT / "unity" / "asset-store" / "images"

ROOM = ["-lolaSet", "realSizeScreenHeight=36", "-lolaSet", "monitorSize=14", "-lolaSet", "roomLight=3"]
SCENE = ["-lolaFrames", "30"]          # the scenes move: half a second in

# name -> Unity capture arguments
RENDERS = {
    "icon": ["-lolaSize", "640x640", *SCENE, "-lolaSet", "aroundTheTube=Synthwave", "-lolaSet", "realSizeScreenHeight=46",
             "-lolaSet", "monitorSize=14", "-lolaSet", "roomLight=3"],
    "card": ["-lolaSize", "840x560", *SCENE, "-lolaSet", "aroundTheTube=Synthwave", *ROOM],
    "cover": ["-lolaSize", "1950x1300", *SCENE, "-lolaSet", "aroundTheTube=Synthwave", *ROOM],
    "social": ["-lolaSize", "1200x630", *SCENE, "-lolaSet", "aroundTheTube=NightCity", "-lolaSet", "roomLight=2",
               "-lolaSet", "realSizeScreenHeight=44", "-lolaSet", "monitorSize=14"],
    "hero": ["-lolaSize", "1920x1080", *SCENE, "-lolaSet", "aroundTheTube=Synthwave", *ROOM],
    "snes-detail": ["-lolaSize", "2880x2160"],
    "md-composite": ["-lolaSize", "1440x1080", "-lolaPreset", "SystemMegaDriveGenesis", "-lolaPicture", "waterfall"],
    "md-rgb": ["-lolaSize", "1440x1080", "-lolaPreset", "SystemMegaDriveGenesis", "-lolaPicture", "waterfall",
               "-lolaSet", "cable=RGB"],
    "pc": ["-lolaSize", "1920x1080", "-lolaPreset", "PCMonitorVGA", "-lolaPicture", "dos", "-lolaSet", "aroundTheTube=Room",
           "-lolaSet", "realSizeScreenHeight=36", "-lolaSet", "roomLight=3"],
    "amiga": ["-lolaSize", "1920x1080", *SCENE, "-lolaPreset", "Commodore1084SAmiga", "-lolaPicture", "amiga",
              "-lolaSet", "aroundTheTube=AmigaDemo", "-lolaSet", "realSizeScreenHeight=36", "-lolaSet", "roomLight=3"],
    "rf": ["-lolaSize", "1920x1080", "-lolaPreset", "OldTVRF", "-lolaSet", "aroundTheTube=Room", *ROOM],
    "trinitron": ["-lolaSize", "1920x1080", *SCENE, "-lolaPreset", "SonyTrinitronSVideo", "-lolaPicture", "waterfall",
                  "-lolaSet", "aroundTheTube=NightCity", *ROOM],
    "tvshow": ["-lolaSize", "1920x1080", *SCENE, "-lolaPreset", "TVShowBroadcast", "-lolaSet", "aroundTheTube=Underwater", *ROOM],
}

JPG = dict(quality=94, subsampling=0, optimize=True)


def newest_unity():
    editors = sorted((Path("/Applications/Unity/Hub/Editor")).glob("*/Unity.app/Contents/MacOS/Unity"))
    if not editors:
        raise SystemExit("no Unity editor found; give --unity")
    return editors[-1]


def render(unity, name, out_dir):
    out = out_dir / f"{name}.png"
    log = out_dir / f"{name}.log"
    subprocess.run([str(unity), "-batchmode", "-projectPath", str(PROJECT), "-executeMethod", "LolaRetroTestCapture.Capture",
                    "-lolaOut", str(out), *RENDERS[name], "-quit", "-logFile", str(log)], check=False)
    if not out.exists():
        raise SystemExit(f"{name}: no picture; see {log}")
    print("rendered", name)
    return Image.open(out).convert("RGB")


def split(a, b, x):
    """a left of x, b right of it, a thin dark line between."""
    o = Image.new("RGB", a.size)
    o.paste(a.crop((0, 0, x, a.size[1])), (0, 0))
    o.paste(b.crop((x, 0, b.size[0], b.size[1])), (x, 0))
    ImageDraw.Draw(o).line([(x, 0), (x, a.size[1])], fill=(20, 20, 20), width=4)
    return o


def pixels_and_crt(crt):
    """The raw pixels and Lola Retro side by side, the same part of the picture at the same scale."""
    raw = Image.open(ROOT / "test-images" / "platformer.png").convert("RGB")      # 256 x 224
    w, h = crt.size
    # the TV shows 256 pixels across and its 240-line field down, overscan 0.05; 224 lines sit in the middle
    sx, sy = w / (256 * 0.95), h / (240 * 0.95)
    cx, cy = 60, 150                                   # the knight, at the window's middle
    px, py = w / 2 + (cx - 128) * sx, h / 2 + (cy - 112) * sy
    box = (int(px - 480), int(py - 540), int(px + 480), int(py + 540))
    big = raw.resize((int(256 * sx), int(224 * sy)), Image.NEAREST)
    ox, oy = w / 2 - big.size[0] / 2, h / 2 - big.size[1] / 2
    left = big.crop((int(box[0] - ox), int(box[1] - oy), int(box[2] - ox), int(box[3] - oy)))
    o = Image.new("RGB", (1920, 1080))
    o.paste(left, (0, 0))
    o.paste(crt.crop(box), (960, 0))
    ImageDraw.Draw(o).line([(960, 0), (960, 1080)], fill=(20, 20, 20), width=4)
    return o


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--unity", type=Path)
    ap.add_argument("--only", nargs="*", help="render only these (see RENDERS); the others are taken from --keep")
    ap.add_argument("--keep", type=Path, help="folder for the renders (kept between runs)")
    a = ap.parse_args()
    unity = a.unity or newest_unity()
    work = a.keep or Path(tempfile.mkdtemp(prefix="lola-store-"))
    work.mkdir(parents=True, exist_ok=True)
    pic = {}
    for name in RENDERS:
        f = work / f"{name}.png"
        pic[name] = render(unity, name, work) if a.only is None or name in a.only or not f.exists() \
            else Image.open(f).convert("RGB")

    OUT.mkdir(parents=True, exist_ok=True)
    pic["icon"].resize((160, 160), Image.LANCZOS).save(OUT / "icon-160x160.png")
    pic["card"].resize((420, 280), Image.LANCZOS).save(OUT / "card-420x280.png")
    pic["cover"].save(OUT / "cover-1950x1300.jpg", **JPG)
    pic["social"].save(OUT / "social-1200x630.jpg", **JPG)
    pic["hero"].save(OUT / "screenshot-01-synthwave.jpg", **JPG)
    pixels_and_crt(pic["snes-detail"]).save(OUT / "screenshot-02-pixels-and-crt.jpg", **JPG)
    split(pic["md-composite"], pic["md-rgb"], 720).save(OUT / "screenshot-03-composite-and-rgb.jpg", **JPG)
    for name, file in (("pc", "04-pc-monitor"), ("amiga", "05-amiga-1084s"), ("rf", "06-old-tv-rf"),
                       ("trinitron", "07-trinitron-s-video"), ("tvshow", "08-tv-show")):
        pic[name].save(OUT / f"screenshot-{file}.jpg", **JPG)
    d = Image.open(ROOT / "docs" / "dracula-vs-real-crt.jpg").convert("RGB")
    page = Image.new("RGB", (2000, 1125), (17, 17, 17))
    page.paste(d, (0, (1125 - d.size[1]) // 2))
    page.save(OUT / "screenshot-09-dracula-real-crt.jpg", **JPG)
    for f in sorted(OUT.iterdir()):
        print("wrote", f.relative_to(ROOT), Image.open(f).size)


if __name__ == "__main__":
    main()
