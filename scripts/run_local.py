#!/usr/bin/env python3
"""Start the feedback service locally from .env.local.

Nothing reads ``.env.local`` by itself: ASP.NET Core reads appsettings plus environment variables, and
docker compose only reads the file you pass to ``--env-file``. This script maps the compose-style
variables in the env file onto ASP.NET Core configuration keys with the same defaults
``docker-compose.yml`` uses, exports them for the child process, and then either runs the app locally
or hands the file to compose.

Local runs go through the launchSettings ``http`` profile (http://localhost:5087, Development);
``--docker`` takes everything, including ``ASPNETCORE_ENVIRONMENT``, from the env file.

Exit codes
    0  the command ran (its own exit code is propagated for --docker and for the app)
    1  a required variable is missing or the configuration is invalid
    2  no env file

Examples
    python scripts/run_local.py --dry-run
    python scripts/run_local.py --docker      # 本地测试主路径：compose 起最新构建（127.0.0.1:3000）
    python scripts/run_local.py               # 不起容器：本机 dotnet run（localhost:5087）
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

CYAN = "\033[36m" if (sys.stdout.isatty() and os.environ.get("NO_COLOR") is None) else ""
GREEN = "\033[32m" if CYAN else ""
YELLOW = "\033[33m" if CYAN else ""
RED = "\033[31m" if CYAN else ""
RESET = "\033[0m" if CYAN else ""

MASKED_KEYS = ("Jwt__SigningKey", "Admin__SeedPassword", "ConnectionStrings__DefaultConnection")


def _configure_stdout() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass


def read_env_file(path: Path) -> dict[str, str]:
    """解析 KEY=VALUE；忽略注释与空行，去掉成对引号。"""
    values: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        separator = line.find("=")
        if separator <= 0:
            continue
        key = line[:separator].strip()
        value = line[separator + 1 :].strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
            value = value[1:-1]
        values[key] = value
    return values


def main() -> int:
    _configure_stdout()

    repo_root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(
        description="用 .env.local 在本机跑起反馈服务（或本机 docker compose 栈），供本地测试使用。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--env-file", default=str(repo_root / ".env.local"), help="默认 <repo>/.env.local")
    parser.add_argument("--docker", action="store_true", help="改为 docker compose --env-file <file> up -d --build")
    parser.add_argument("--dry-run", action="store_true", help="只打印解析后的配置，不启动任何东西")
    parser.add_argument("--no-restore", action="store_true", help="透传 --no-restore 给 dotnet run")
    args = parser.parse_args()

    env_path = Path(args.env_file).resolve()
    if not env_path.is_file():
        print(f"{RED}GD_FEEDBACK_LOCAL FAIL 找不到环境变量文件：{env_path}{RESET}")
        print(f"{YELLOW}提示：复制 .env.example 为 .env.local 后按需修改，或见 .env.local 里的用法说明。{RESET}")
        return 2

    values = read_env_file(env_path)

    # 与 docker compose 的 ${VAR:-default} 语义一致：空值也算"没给"，用默认值兜底。
    def value(key: str, default: str = "") -> str:
        raw = values.get(key, "")
        return raw if raw.strip() else default

    # 与 docker compose 的 ${VAR:?msg} 语义一致：必填项缺失就明确失败。
    def required(key: str, hint: str) -> str:
        found = value(key)
        if not found:
            print(f"{RED}GD_FEEDBACK_LOCAL FAIL 缺少必填项 {key}：{hint}{RESET}")
            raise SystemExit(1)
        return found

    postgres_password = required("POSTGRES_PASSWORD", "本地 postgres 的密码（compose 用它初始化数据卷）")
    jwt_signing_key = required("JWT_SIGNING_KEY", "JWT 签名密钥，至少 32 字符")
    admin_seed_password = required("ADMIN_SEED_PASSWORD", "首个管理员密码，至少 8 字符")

    mapping = {
        "ASPNETCORE_ENVIRONMENT": value("ASPNETCORE_ENVIRONMENT", "Development"),
        # 本机直跑用 localhost；容器内由 compose 自己拼 postgres 主机名。
        "ConnectionStrings__DefaultConnection": (
            "Host=localhost;Port=5432;Database=gamefeedback;Username=gamefeedback;"
            f"Password={postgres_password}"
        ),
        "Database__AutoMigrate": "true",
        # Steam 的凭据/AppID/identity 已搬进数据库，本地不再从环境变量注入；
        # 本机跑起来后登录管理端，在「Steam 凭据」与「游戏」页面里配置。
        "DataProtection__KeysPath": value("DATAPROTECTION_KEYS_PATH", str(repo_root / ".keys-local")),
        "Steam__DebugSkipTicketValidation": value("STEAM_DEBUG_SKIP", "false"),
        "Jwt__Issuer": value("JWT_ISSUER", "GameFeedback"),
        "Jwt__Audience": value("JWT_AUDIENCE", "GameFeedbackClient"),
        "Jwt__SigningKey": jwt_signing_key,
        "Admin__SeedEmail": value("ADMIN_SEED_EMAIL", "admin@example.com"),
        "Admin__SeedPassword": admin_seed_password,
        "Swagger__Enabled": value("SWAGGER_ENABLED", "false"),
        "ReverseProxy__KnownProxies__0": value("REVERSE_PROXY_IP", "127.0.0.1"),
    }

    # 提前复现应用的启动校验，省得看到一句难懂的就地崩溃。
    if (
        mapping["Steam__DebugSkipTicketValidation"] == "true"
        and mapping["ASPNETCORE_ENVIRONMENT"] != "Development"
    ):
        print(
            f"{RED}GD_FEEDBACK_LOCAL FAIL STEAM_DEBUG_SKIP=true 只能配 "
            f"ASPNETCORE_ENVIRONMENT=Development，否则应用会拒绝启动。{RESET}"
        )
        return 1

    print(f"{CYAN}== 本地测试配置（来源：{env_path}）=={RESET}")
    for key, display_value in mapping.items():
        shown = "***" if key in MASKED_KEYS else display_value
        print(f"  {key:<40} {shown}")

    if args.dry_run:
        print(f"{GREEN}GD_FEEDBACK_LOCAL PASS (dry run){RESET}")
        return 0

    if args.docker:
        if shutil.which("docker") is None:
            print(f"{RED}GD_FEEDBACK_LOCAL FAIL 找不到 docker 命令。{RESET}")
            return 1
        print()
        print(f"{CYAN}== docker compose --env-file {env_path} up -d --build =={RESET}")
        compose = ["docker", "compose", "--env-file", str(env_path)]
        result = subprocess.run(compose + ["up", "-d", "--build"])
        if result.returncode == 0:
            subprocess.run(compose + ["ps"])
            print()
            print(f"{GREEN}本地栈已启动，应用在 http://127.0.0.1:3000{RESET}")
            print(
                f"{GREEN}手动测试：打开 Godot 测试台 tests/godot-feedback-host/"
                f"（BaseUrl 默认就是这个地址），勾「调试登录」；先点「健康检查」确认 /health 通了。{RESET}"
            )
        return result.returncode

    if shutil.which("dotnet") is None:
        print(f"{RED}GD_FEEDBACK_LOCAL FAIL 找不到 dotnet 命令。{RESET}")
        return 1

    os.environ.update(mapping)

    print()
    print(
        f"{GREEN}本地地址：http://localhost:5087/health · Swagger http://localhost:5087/swagger · "
        f"后台 http://localhost:5087/admin{RESET}"
    )
    print(f"{GREEN}Godot 测试台：这次是\"不起容器\"的模式，BaseUrl 要改成 http://localhost:5087；{RESET}")
    print(
        f"{GREEN}若要按本地测试主路径（compose 起最新构建 + 测试台默认地址），改用 --docker"
        f"（应用在 http://127.0.0.1:3000）。{RESET}"
    )
    print(
        f"{YELLOW}数据库没起时先执行：docker compose up -d postgres"
        f"（密码要与本次的 POSTGRES_PASSWORD 一致）{RESET}"
    )
    print()

    command = ["dotnet", "run", "--project", str(repo_root / "src" / "GameFeedback")]
    if args.no_restore:
        command.append("--no-restore")
    return subprocess.run(command).returncode


if __name__ == "__main__":
    sys.exit(main())
