#!/usr/bin/env python3
"""Verify the GD Feedback addon end to end, offline.

Stages, each printing a stable marker:

  1. GD_FEEDBACK_IDENTITY / GD_FEEDBACK_MANIFEST   identity + allowlist (tools/package.py --verify-only)
  2. GD_FEEDBACK_ENGINE_FREE                       the core files must not reference Godot
  3. GD_FEEDBACK_ENGINE_FREE_TESTS                 plain .NET fixture: compile the core, run the harness
  4. GD_FEEDBACK_GODOT_HOST                        Godot.NET.Sdk fixture: compile every shipped .cs, Debug + Release
  5. GD_FEEDBACK_GODOT_PROBE                       optional in-engine probe (needs --godot-path)
  -> GD_FEEDBACK_VERIFY                            overall verdict

Stages 2-4 need no Godot editor, no network and no Godot game project: templates are expanded into a
temp folder and restored from the local NuGet cache only.

Exit codes
    0  pass
    1  fail

Examples
    python addons/gd_feedback/tests/verify.py
    python addons/gd_feedback/tests/verify.py --godot-path /usr/local/bin/godot-mono
    python addons/gd_feedback/tests/verify.py --skip-godot-host
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

CYAN = "\033[36m" if (sys.stdout.isatty() and os.environ.get("NO_COLOR") is None) else ""
GREEN = "\033[32m" if CYAN else ""
YELLOW = "\033[33m" if CYAN else ""
RED = "\033[31m" if CYAN else ""
RESET = "\033[0m" if CYAN else ""

CORE_FILES = ("FeedbackContracts.cs", "FeedbackAbstractions.cs", "FeedbackRuntime.cs")
GODOT_HOST_ASSEMBLY = Path(".godot") / "mono" / "temp" / "bin" / "Debug" / "GdFeedbackGodotHost.dll"


def _configure_stdout() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass


def run(command: list[str], env: dict[str, str], timeout: float | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
        timeout=timeout,
    )


def resolve_console_variant(godot_path: str) -> str:
    """Windows 上 godot-mono.exe 是 GUI 子系统、不产生 stdout；有 .console.exe 就用它。"""
    path = Path(godot_path)
    if path.suffix.lower() != ".exe":
        return godot_path
    console_variant = path.with_suffix("").with_suffix(".console.exe")
    return str(console_variant) if console_variant.is_file() else godot_path


def install_shipped_addon(project_directory: Path, addon_root: Path, manifest: dict) -> None:
    """按 manifest 白名单把插件"安装"进 fixture 工程，和真实宿主看到的东西一模一样。"""
    target = project_directory / "addons" / str(manifest["id"])
    for name in manifest["files"]:
        destination = target / str(name)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(addon_root / str(name), destination)


def resolve_work_directory(preferred: str | None, repo_root: Path) -> Path:
    """找一个真的能创建嵌套目录的地方放展开后的 fixture。

    首选系统临时目录；但受限环境（例如只允许写工作区的沙箱、某些 CI 容器）里子进程可能连 TEMP
    都写不了，所以真去试一次，再退回到仓库里已被 gitignore 的 artifacts/。
    """
    token = f"{os.getpid()}_{time.monotonic_ns()}"
    candidates: list[Path] = []
    if preferred:
        candidates.append(Path(preferred).expanduser() / f"gd_feedback_verify_{token}")
    candidates.append(Path(tempfile.gettempdir()) / f"gd_feedback_verify_{token}")
    candidates.append(repo_root / "artifacts" / f"verify-{token}")

    for candidate in candidates:
        try:
            (candidate / ".writable-probe").mkdir(parents=True, exist_ok=True)
        except OSError:
            continue
        return candidate

    tried = ", ".join(str(item) for item in candidates)
    raise SystemExit(f"GD_FEEDBACK_VERIFY FAIL 找不到可写的临时目录（试过：{tried}）")


class Verifier:
    def __init__(self) -> None:
        self.failures = 0

    def section(self, text: str) -> None:
        print()
        print(f"{CYAN}== {text} =={RESET}")

    def passed(self, marker: str, detail: str = "") -> None:
        print(f"{GREEN}{marker} PASS{' ' + detail if detail else ''}{RESET}")

    def failed(self, marker: str, detail: str = "") -> None:
        self.failures += 1
        print(f"{RED}{marker} FAIL {detail}{RESET}")


def main() -> int:
    _configure_stdout()

    addon_root = Path(__file__).resolve().parent.parent
    fixture_root = Path(__file__).resolve().parent / "fixtures" / "clean_host"
    manifest = json.loads((addon_root / "addon.manifest.json").read_text(encoding="utf-8"))

    parser = argparse.ArgumentParser(
        description="离线端到端验证 GD Feedback 插件（身份/白名单、引擎无关核心、干净宿主 fixture、可选引擎内探针）。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--godot-path", default=None, help="Godot 4.7 .NET 编辑器可执行文件；给了才跑第 5 阶段的引擎内探针")
    parser.add_argument("--nuget-cache", default=None, help="本地包目录，作为唯一的包源；默认 ~/.nuget/packages")
    parser.add_argument("--skip-godot-host", action="store_true", help="跳过第 4 阶段")
    parser.add_argument("--keep-temp", action="store_true", help="保留展开后的临时 fixture 以便排查")
    parser.add_argument("--work-directory", default=None, help="展开 fixture 的父目录；默认系统临时目录（不可写时回退到 <repo>/artifacts）")
    args = parser.parse_args()

    nuget_cache = Path(args.nuget_cache).expanduser() if args.nuget_cache else Path.home() / ".nuget" / "packages"
    if not nuget_cache.is_dir():
        print(f"{RED}GD_FEEDBACK_VERIFY FAIL NuGet cache not found: {nuget_cache}{RESET}")
        return 1

    env = dict(os.environ)
    env["DOTNET_CLI_UI_LANGUAGE"] = "en"
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"

    temp_root = resolve_work_directory(args.work_directory, addon_root.parent.parent)
    if tempfile.gettempdir() not in str(temp_root):
        print(f"{YELLOW}系统临时目录不可写，改用：{temp_root}{RESET}")
    verifier = Verifier()

    tokens = {
        "__ADDON_ROOT__": addon_root.as_posix(),
        "__NUGET_CACHE__": nuget_cache.as_posix(),
        "__GODOT_SDK__": str(manifest["godotVersion"]),
    }

    def expand(template_name: str, destination: Path) -> None:
        content = (fixture_root / template_name).read_text(encoding="utf-8")
        for key, replacement in tokens.items():
            content = content.replace(key, replacement)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(content, encoding="utf-8")

    try:
        # ------------------------------------------------------------ stage 1
        verifier.section("stage 1: identity and allowlist")
        package_result = subprocess.run(
            [sys.executable, str(addon_root / "tools" / "package.py"), "--verify-only"],
            env=env,
        )
        if package_result.returncode != 0:
            verifier.failed("GD_FEEDBACK_VERIFY", "(identity or allowlist check failed)")
            return 1

        # ------------------------------------------------------------ stage 2
        verifier.section("stage 2: the core must not reference Godot")
        violations = []
        for name in CORE_FILES:
            text = (addon_root / name).read_text(encoding="utf-8")
            if re.search(r"using\s+Godot\s*;", text) or re.search(r"\bGodot\.", text):
                violations.append(name)
        if not violations:
            verifier.passed("GD_FEEDBACK_ENGINE_FREE")
        else:
            verifier.failed("GD_FEEDBACK_ENGINE_FREE", f"({', '.join(violations)})")

        # ------------------------------------------------------------ stage 3
        verifier.section("stage 3: engine-free fixture + harness")
        engine_free = temp_root / "engine-free"
        engine_free.mkdir(parents=True, exist_ok=True)
        expand("EngineFree.csproj.template", engine_free / "GdFeedbackHarness.csproj")
        expand("NuGet.Config.template", engine_free / "NuGet.Config")
        shutil.copyfile(fixture_root / "Harness.cs", engine_free / "Harness.cs")

        project = engine_free / "GdFeedbackHarness.csproj"
        restore = run(["dotnet", "restore", str(project), "--source", str(nuget_cache)], env)
        build = run(
            ["dotnet", "build", str(project), "-c", "Release", "--no-restore", "-p:TreatWarningsAsErrors=true"],
            env,
        )
        build_output = build.stdout + build.stderr

        if restore.returncode != 0:
            verifier.failed("GD_FEEDBACK_ENGINE_FREE_TESTS", "(restore failed)")
            print(restore.stdout + restore.stderr)
        elif build.returncode != 0 or "0 Warning(s)" not in build_output:
            verifier.failed("GD_FEEDBACK_ENGINE_FREE_TESTS", "(strict build failed)")
            print(build_output)
        else:
            harness = run(
                ["dotnet", "run", "--project", str(project), "-c", "Release", "--no-build"],
                env,
            )
            harness_output = harness.stdout + harness.stderr
            for line in harness_output.splitlines():
                if line.startswith("HARNESS "):
                    print(f"    {line.rstrip()}")
            if harness.returncode == 0 and re.search(r"HARNESS SUMMARY checks=\d+ failed=0", harness_output):
                verifier.passed("GD_FEEDBACK_ENGINE_FREE_TESTS")
            else:
                verifier.failed("GD_FEEDBACK_ENGINE_FREE_TESTS", "(harness reported failures)")

        # ------------------------------------------------------------ stage 4
        verifier.section("stage 4: Godot host fixture")
        if args.skip_godot_host:
            print(f"{YELLOW}GD_FEEDBACK_GODOT_HOST SKIP (--skip-godot-host){RESET}")
        else:
            godot_dir = temp_root / "godot-host"
            godot_dir.mkdir(parents=True, exist_ok=True)
            install_shipped_addon(godot_dir, addon_root, manifest)
            expand("GodotHost.csproj.template", godot_dir / "GdFeedbackGodotHost.csproj")
            expand("NuGet.Config.template", godot_dir / "NuGet.Config")

            godot_project = godot_dir / "GdFeedbackGodotHost.csproj"
            godot_restore = run(["dotnet", "restore", str(godot_project), "--source", str(nuget_cache)], env)

            if godot_restore.returncode != 0:
                verifier.failed("GD_FEEDBACK_GODOT_HOST", "(restore failed; is Godot.NET.Sdk in the local cache?)")
                print(godot_restore.stdout + godot_restore.stderr)
            else:
                debug_build = run(
                    ["dotnet", "build", str(godot_project), "-c", "Debug", "--no-restore", "-p:TreatWarningsAsErrors=true"],
                    env,
                )
                release_build = run(
                    ["dotnet", "build", str(godot_project), "-c", "Release", "--no-restore", "-p:TreatWarningsAsErrors=true"],
                    env,
                )
                assembly = godot_dir / GODOT_HOST_ASSEMBLY
                plugin_compiled = assembly.is_file() and b"GdFeedbackPlugin" in assembly.read_bytes()

                debug_clean = debug_build.returncode == 0 and "0 Warning(s)" in (debug_build.stdout + debug_build.stderr)
                release_clean = release_build.returncode == 0 and "0 Warning(s)" in (
                    release_build.stdout + release_build.stderr
                )

                if debug_clean and release_clean and plugin_compiled:
                    verifier.passed("GD_FEEDBACK_GODOT_HOST")
                else:
                    verifier.failed(
                        "GD_FEEDBACK_GODOT_HOST",
                        "(strict Debug/Release build failed or the plugin type is missing)",
                    )
                    print(debug_build.stdout + debug_build.stderr)
                    print(release_build.stdout + release_build.stderr)

        # ------------------------------------------------------------ stage 5
        verifier.section("stage 5: in-engine probe (optional)")
        if not args.godot_path:
            print(f"{YELLOW}GD_FEEDBACK_GODOT_PROBE SKIP (pass --godot-path <godot-mono>){RESET}")
        elif not Path(args.godot_path).is_file():
            verifier.failed("GD_FEEDBACK_GODOT_PROBE", f"(Godot not found at {args.godot_path})")
        else:
            godot_executable = resolve_console_variant(args.godot_path)
            probe_dir = temp_root / "godot-probe"
            probe_dir.mkdir(parents=True, exist_ok=True)
            install_shipped_addon(probe_dir, addon_root, manifest)
            expand("GodotHost.csproj.template", probe_dir / "GdFeedbackGodotHost.csproj")
            expand("NuGet.Config.template", probe_dir / "NuGet.Config")
            expand("Project.godot.template", probe_dir / "project.godot")
            expand("Probe.tscn.template", probe_dir / "Probe.tscn")
            shutil.copyfile(fixture_root / "Probe.cs", probe_dir / "Probe.cs")

            probe_project = probe_dir / "GdFeedbackGodotHost.csproj"
            probe_restore = run(["dotnet", "restore", str(probe_project), "--source", str(nuget_cache)], env)
            probe_debug = run(["dotnet", "build", str(probe_project), "-c", "Debug", "--no-restore"], env)
            probe_release = run(["dotnet", "build", str(probe_project), "-c", "Release", "--no-restore"], env)

            if probe_restore.returncode != 0 or probe_debug.returncode != 0 or probe_release.returncode != 0:
                verifier.failed("GD_FEEDBACK_GODOT_PROBE", "(probe project failed to build)")
                print(probe_debug.stdout + probe_debug.stderr)
                print(probe_release.stdout + probe_release.stderr)
            else:
                try:
                    probe = run(
                        [godot_executable, "--headless", "--path", str(probe_dir), "res://Probe.tscn"],
                        env,
                        timeout=120,
                    )
                except subprocess.TimeoutExpired:
                    verifier.failed("GD_FEEDBACK_GODOT_PROBE", "(timed out after 120s)")
                else:
                    probe_log = probe.stdout + probe.stderr
                    # 以探针自己打印的标记为准，退出码只作为附加信息。
                    probe_ok = "GD_FEEDBACK_PROBE PASS" in probe_log and "GD_FEEDBACK_PROBE FAIL" not in probe_log
                    if probe_ok:
                        verifier.passed("GD_FEEDBACK_GODOT_PROBE", f"(exit {probe.returncode})")
                    else:
                        verifier.failed("GD_FEEDBACK_GODOT_PROBE", f"(exit {probe.returncode})")
                        print(probe_log.strip())
    finally:
        if args.keep_temp:
            print(f"{YELLOW}temp kept: {temp_root}{RESET}")
        else:
            try:
                shutil.rmtree(temp_root)
            except OSError as error:
                # 受限环境里删不掉是常事；明确告诉用户路径，别留下一个静默的坑。
                print(f"{YELLOW}临时目录未能完全清理（{type(error).__name__}），可手动删除：{temp_root}{RESET}")

    print()
    if verifier.failures == 0:
        print(f"{GREEN}GD_FEEDBACK_VERIFY PASS{RESET}")
        return 0
    print(f"{RED}GD_FEEDBACK_VERIFY FAIL ({verifier.failures} check(s) failed){RESET}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
