#!/usr/bin/env python3
"""Install or verify the gd_feedback addon inside this Godot host project.

This test project owns a real installed copy of the addon (that is how a game consumes it:
``res://addons/gd_feedback/``). The script copies the addon's manifest allowlist into
``tests/godot-feedback-host/addons/gd_feedback/`` so the copy can never silently drift from the
source, and with ``--check`` it only compares.

Files Godot generates on import (``*.uid``, ``*.import``) are ignored; any other extra file in the
installed copy counts as drift.

Exit codes
    0  installed and in sync
    1  drift or missing files

Examples
    python tests/godot-feedback-host/tools/sync_addon.py
    python tests/godot-feedback-host/tools/sync_addon.py --check
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path

GENERATED_SUFFIXES = (".uid", ".import")

_COLOR = sys.stdout.isatty() and os.environ.get("NO_COLOR") is None
GREEN = "\033[32m" if _COLOR else ""
RED = "\033[31m" if _COLOR else ""
RESET = "\033[0m" if _COLOR else ""


def _configure_stdout() -> None:
    """Windows 控制台可能是旧代码页；中文输出不该让脚本崩掉。"""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def compare(source_root: Path, target_root: Path, files: list[str]) -> list[str]:
    drift: list[str] = []

    for name in files:
        source = source_root / name
        target = target_root / name
        if not target.is_file():
            drift.append(f"missing: {name}")
        elif sha256(source) != sha256(target):
            drift.append(f"differs: {name}")

    if target_root.is_dir():
        for path in sorted(target_root.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(target_root).as_posix()
            if relative.endswith(GENERATED_SUFFIXES):
                continue
            if relative not in files:
                drift.append(f"unexpected: {relative}")

    return drift


def main() -> int:
    _configure_stdout()

    parser = argparse.ArgumentParser(
        description="把 addons/gd_feedback 按 manifest 白名单安装/校验到这个宿主测试工程里。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--check", action="store_true", help="只校验，不复制")
    args = parser.parse_args()

    host_root = Path(__file__).resolve().parent.parent
    repo_root = host_root.parent.parent
    source_root = repo_root / "addons" / "gd_feedback"
    manifest = json.loads((source_root / "addon.manifest.json").read_text(encoding="utf-8"))
    target_root = host_root / "addons" / str(manifest["id"])
    files = list(manifest["files"])

    if not args.check:
        for name in files:
            destination = target_root / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source_root / name, destination)

    drift = compare(source_root, target_root, files)
    if not drift:
        action = "matches" if args.check else "installed"
        print(
            f"{GREEN}GD_FEEDBACK_HOST_SYNC PASS{RESET} ({action} {len(files)} shipped files at "
            f"addons/{manifest['id']}/)"
        )
        return 0

    print(f"{RED}GD_FEEDBACK_HOST_SYNC FAIL{RESET}")
    for item in drift:
        print(f"{RED}  - {item}{RESET}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
