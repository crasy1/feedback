#!/usr/bin/env python3
"""Verify the GD Feedback addon's release identity and file allowlist, and optionally package it.

The addon is shipped as an exact allowlist declared in ``addon.manifest.json``. This script is the single
place that proves the package matches its own metadata:

  * ``plugin.cfg`` has the five keys Godot's plugin loader requires, and no ``language`` key
  * the version agrees across ``plugin.cfg``, ``addon.manifest.json`` and ``README.md``
  * the allowlist is sorted (ordinal), unique, forward-slash only, and free of tests/tools/fixtures/demo
  * every listed file exists, and every file in the addon folder is either listed or deliberately excluded
  * the built archive contains exactly the allowlisted entries

Exit codes
    0  all checks passed
    1  at least one check failed

Examples
    python addons/gd_feedback/tools/package.py --verify-only
    python addons/gd_feedback/tools/package.py --output-directory dist
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import zipfile
from pathlib import Path

CYAN = "\033[36m" if (sys.stdout.isatty() and os.environ.get("NO_COLOR") is None) else ""
GREEN = "\033[32m" if CYAN else ""
YELLOW = "\033[33m" if CYAN else ""
RED = "\033[31m" if CYAN else ""
RESET = "\033[0m" if CYAN else ""

PLUGIN_ENTRY = re.compile(r'^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*"(.*)"\s*$')
NON_SHIPPING_SEGMENT = re.compile(r"(^|/)(tests?|tools?|fixtures?|demo)(/|$)")
EXCLUDED_TOP_LEVEL = {"tests", "tools"}
GENERATED_SUFFIXES = (".uid", ".import")


def _configure_stdout() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass


class Checks:
    def __init__(self) -> None:
        self.failures = 0

    def check(self, name: str, condition: bool, detail: str = "") -> bool:
        if condition:
            print(f"{GREEN}[PASS]{RESET} {name}")
        else:
            self.failures += 1
            print(f"{RED}[FAIL]{RESET} {name} {detail}")
        return condition


def read_plugin_config(path: Path) -> dict[str, str]:
    keys: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = PLUGIN_ENTRY.match(line)
        if match:
            keys[match.group(1)] = match.group(2)
    return keys


def unlisted_files(addon_root: Path, files: list[str]) -> list[str]:
    unlisted: list[str] = []
    for path in sorted(addon_root.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(addon_root).as_posix()
        top = relative.split("/")[0]
        if top in EXCLUDED_TOP_LEVEL or relative == "addon.manifest.json":
            continue
        if relative.endswith(GENERATED_SUFFIXES) or top == ".godot":
            continue
        if relative not in files:
            unlisted.append(relative)
    return unlisted


def main() -> int:
    _configure_stdout()

    addon_root = Path(__file__).resolve().parent.parent
    repo_root = addon_root.parent.parent
    addon_name = addon_root.name
    manifest_path = addon_root / "addon.manifest.json"
    plugin_config_path = addon_root / "plugin.cfg"
    readme_path = addon_root / "README.md"

    parser = argparse.ArgumentParser(
        description="校验 GD Feedback 插件的发布身份与文件白名单，并可选打包成归档。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--output-directory", default=None, help="归档输出目录，默认 <repo>/artifacts")
    parser.add_argument("--verify-only", action="store_true", help="只校验，不生成归档")
    args = parser.parse_args()

    checks = Checks()
    print(f"{CYAN}== GD Feedback package check ({addon_root}) =={RESET}")

    # ------------------------------------------------------------------ identity

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    plugin_keys = read_plugin_config(plugin_config_path)

    for required_key in ("name", "description", "author", "version", "script"):
        checks.check(
            f"plugin.cfg declares {required_key}",
            required_key in plugin_keys,
            "(missing)",
        )
    checks.check("plugin.cfg declares no language key", "language" not in plugin_keys, "(language must not be present)")
    checks.check(
        "plugin.cfg script points at C#",
        plugin_keys.get("script", "").endswith(".cs"),
        f"(got '{plugin_keys.get('script', '')}')",
    )
    checks.check(
        "plugin.cfg version matches the manifest",
        plugin_keys.get("version") == manifest.get("version"),
        f"({plugin_keys.get('version')} vs {manifest.get('version')})",
    )

    readme_head = readme_path.read_text(encoding="utf-8").splitlines()[0] if readme_path.is_file() else ""
    checks.check(
        "README title carries the version",
        str(manifest.get("version", "")) in readme_head,
        f"({readme_head})",
    )

    checks.check("manifest schemaVersion is 1", manifest.get("schemaVersion") == 1, f"({manifest.get('schemaVersion')})")
    checks.check("manifest id matches the folder name", manifest.get("id") == addon_name, f"({manifest.get('id')} vs {addon_name})")
    checks.check(
        "manifest archiveRoot matches the folder name",
        manifest.get("archiveRoot") == f"addons/{addon_name}/",
        f"({manifest.get('archiveRoot')})",
    )
    checks.check(
        "manifest pins a Godot patch version",
        bool(re.fullmatch(r"\d+\.\d+(\.\d+)?", str(manifest.get("godotVersion", "")))),
        f"({manifest.get('godotVersion')})",
    )
    checks.check(
        "manifest pins a .NET target framework",
        bool(re.fullmatch(r"net\d+\.\d+", str(manifest.get("targetFramework", "")))),
        f"({manifest.get('targetFramework')})",
    )
    checks.check(
        "manifest declares no addon dependencies",
        isinstance(manifest.get("dependencies"), dict) and not manifest["dependencies"],
        "(dependencies must stay empty)",
    )

    if checks.failures == 0:
        print(f"{GREEN}GD_FEEDBACK_IDENTITY PASS{RESET}")
    else:
        print(f"{RED}GD_FEEDBACK_IDENTITY FAIL{RESET}")
        return 1

    # ------------------------------------------------------------------ allowlist

    files = [str(name) for name in manifest.get("files", [])]
    checks.check("allowlist is sorted (ordinal)", files == sorted(files), "(not sorted)")
    checks.check("allowlist has no duplicates", len(files) == len(set(files)), "(duplicates present)")
    checks.check("allowlist uses forward slashes only", not any("\\" in name for name in files), "(backslash found)")
    checks.check(
        "allowlist excludes addon.manifest.json",
        "addon.manifest.json" not in files,
        "(manifest must not list itself)",
    )
    checks.check(
        "allowlist excludes tests/tools/fixtures/demo",
        not any(NON_SHIPPING_SEGMENT.search(name) for name in files),
        "(non-shipping path found)",
    )

    missing = [name for name in files if not (addon_root / name).is_file()]
    checks.check("every allowlisted file exists", not missing, f"({', '.join(missing)})")

    unlisted = unlisted_files(addon_root, files)
    checks.check("no addon file is missing from the allowlist", not unlisted, f"({', '.join(unlisted)})")

    if checks.failures != 0:
        print(f"{RED}GD_FEEDBACK_MANIFEST FAIL{RESET}")
        return 1
    print(f"{GREEN}GD_FEEDBACK_MANIFEST PASS{RESET}")

    if args.verify_only:
        print(f"{YELLOW}GD_FEEDBACK_PACKAGE SKIP (--verify-only){RESET}")
        return 0

    # ------------------------------------------------------------------ archive

    output_directory = Path(args.output_directory) if args.output_directory else repo_root / "artifacts"
    output_directory.mkdir(parents=True, exist_ok=True)
    archive_path = output_directory / f"{addon_name}-{manifest['version']}.zip"

    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in files:
            archive.write(addon_root / name, arcname=f"{manifest['archiveRoot']}{name}")

    with zipfile.ZipFile(archive_path) as archive:
        entries = sorted(info.filename for info in archive.infolist() if not info.is_dir())

    expected = sorted(f"{manifest['archiveRoot']}{name}" for name in files)
    checks.check(
        "archive contains exactly the allowlist",
        entries == expected,
        f"(expected {len(expected)} entries, got {len(entries)})",
    )

    if checks.failures == 0:
        print(f"{GREEN}GD_FEEDBACK_PACKAGE PASS {archive_path}{RESET}")
        return 0

    print(f"{RED}GD_FEEDBACK_PACKAGE FAIL{RESET}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
