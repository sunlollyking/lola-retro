#!/usr/bin/env python3
"""Make the Asset Store version of the Unity package: unity/asset-store/upload-project/Assets/Lola Retro.

The Asset Store's classic workflow publishes a folder of a project's Assets (a .unitypackage),
not a UPM package. This copies the package (unity/lola-retro) into the upload project's
Assets/Lola Retro, the same files with the same .meta files, except package.json (only the
Package Manager reads it) and the Demo sample, which becomes the folder Demo (Samples~ is not
imported from Assets). Open the upload project in Unity, then the Asset Store Publishing Tools
validate and upload that folder (unity/asset-store/README.md).

Usage: tools/make_asset_store_project.py
"""
import hashlib
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PACKAGE = ROOT / "unity" / "lola-retro"
PROJECT = ROOT / "unity" / "asset-store" / "upload-project"
OUT = PROJECT / "Assets" / "Lola Retro"

SKIP = {"package.json", "package.json.meta"}

FOLDER_META = ("fileFormatVersion: 2\nguid: {}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n"
               "  userData: \n  assetBundleName: \n  assetBundleVariant: \n")


def folder_meta(path):
    """A .meta file with a fixed GUID, for a folder the package does not have."""
    guid = hashlib.md5(("lola-retro-asset-store/" + path.relative_to(OUT.parent).as_posix()).encode()).hexdigest()
    path.with_name(path.name + ".meta").write_text(FOLDER_META.format(guid))


def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    shutil.copytree(PACKAGE, OUT, ignore=lambda d, names: [n for n in names if n in SKIP or n.startswith(".")
                                                             or n == "Samples~"])
    shutil.copytree(PACKAGE / "Samples~" / "Demo", OUT / "Demo")
    folder_meta(OUT)
    folder_meta(OUT / "Demo")
    files = [p for p in OUT.rglob("*") if p.is_file() and p.suffix != ".meta"]
    missing = [p for p in OUT.rglob("*") if p.suffix != ".meta" and not p.with_name(p.name + ".meta").exists()]
    if missing:
        raise SystemExit("no .meta for " + ", ".join(str(p.relative_to(OUT)) for p in missing))
    print(f"wrote {OUT.relative_to(ROOT)}: {len(files)} files")


if __name__ == "__main__":
    main()
