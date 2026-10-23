#!/usr/bin/env python3
"""Split the Windows installer into Pages-sized parts and write the site manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from pathlib import Path

CHUNK = 9 * 1024 * 1024


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(16 * 1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def find_installer(release_dir: Path) -> Path:
    found = sorted(p for p in release_dir.rglob("*.exe") if p.is_file())
    if len(found) != 1:
        names = ", ".join(p.name for p in found) or "none"
        raise SystemExit(f"expected exactly one .exe in {release_dir}, found: {names}")
    return found[0]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--installer", type=Path, help="installer .exe")
    parser.add_argument("--release-dir", type=Path, help="directory containing the installer")
    parser.add_argument("--dist", type=Path, default=Path("site/dist"))
    parser.add_argument("--version", default="1.0")
    parser.add_argument("--name", help="filename shown in the browser download")
    args = parser.parse_args()

    if args.installer:
        installer = args.installer
    elif args.release_dir:
        installer = find_installer(args.release_dir)
    else:
        raise SystemExit("pass --installer or --release-dir")

    if not installer.is_file():
        raise SystemExit(f"installer not found: {installer}")

    dist = args.dist.resolve()
    download = dist / "download"
    if download.exists():
        shutil.rmtree(download)

    folder = download / f"beadwatch-{args.version}"
    folder.mkdir(parents=True)

    total = installer.stat().st_size
    parts: list[dict[str, object]] = []
    index = 1
    with installer.open("rb") as src:
        while True:
            data = src.read(CHUNK)
            if not data:
                break
            name = f"beadwatch-{args.version}.part{index:02d}"
            (folder / name).write_bytes(data)
            parts.append({
                "url": f"download/beadwatch-{args.version}/{name}",
                "size": len(data),
            })
            index += 1

    digest = sha256(installer)
    display_name = args.name or f"拼豆先锋_v{args.version}_安装程序.exe"
    manifest = {
        "ok": True,
        "name": display_name,
        "version": args.version,
        "size": total,
        "sha256": digest,
        "parts": parts,
    }
    (download / "installer.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    (download / "SHA256SUMS").write_text(f"{digest}  {display_name}\n", encoding="utf-8")

    print(json.dumps({
        "installer": str(installer),
        "size": total,
        "sha256": digest,
        "parts": len(parts),
        "dist": str(dist),
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
