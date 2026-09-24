#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Smoke-test the player API against a running instance.

Sends real HTTP requests to a locally running instance (default
http://localhost:5087) and asserts status codes / response shapes for every
player endpoint, plus the security-relevant negatives: anonymous access,
cross-player ownership, SteamID spoofing through the request body, enum
bypass, and (optionally) rate limiting.

Defaults to the Development debug login (Steam:DebugSkipTicketValidation=true
plus debugSteamId).  Fresh random SteamID64 values are generated on every run,
so repeated runs do not exhaust the per-player / per-IP rate-limit budgets.

This script needs a *running server*: it only sends requests and never starts
the app itself.  Start it first, e.g.

    docker compose up -d postgres
    dotnet run --project src/GameFeedback

It is part of the cross-platform tooling, so it uses the Python standard
library only (no pip install of anything) and works on Linux, macOS and
Windows with Python 3.9+.

Usage:
    python scripts/smoke_player_api.py
    python scripts/smoke_player_api.py --base-url http://localhost:5087 --include-rate-limit-probe
    python scripts/smoke_player_api.py --ticket <steam-web-api-ticket>

Options (same meaning as the PowerShell parameter names):
    --base-url                 Base URL of the running instance
                               (default http://localhost:5087, see launchSettings.json).
    --ticket                   Optional real Steam Web API ticket.  When supplied the real
                               verification path is used instead of the debug login;
                               cross-player ownership checks are skipped, because one
                               ticket only proves one player.
    --steam-id-a               Optional fixed SteamID64 for player A (17 digits).
                               Default: random per run.
    --steam-id-b               Optional fixed SteamID64 for player B (17 digits).
                               Default: random per run.
    --include-rate-limit-probe Also probe POST /api/feedback rate limiting with a third,
                               fresh player (5 per 10 min).

Exit codes:
    0 = all checks passed
    1 = at least one check failed
    2 = server unreachable

Output: one line per check, "[PASS] <name> -- <detail>" / "[FAIL] <name> -- <detail>",
followed by "SUMMARY: {passed}/{total} checks passed" and a re-list of the failed
checks.  ANSI colours are used only on a TTY and are disabled when NO_COLOR is set.

中文摘要：对本地运行的实例发真实 HTTP 请求，逐个断言玩家端点的状态码与响应结构，
并覆盖安全相关的负例（匿名访问、跨玩家所有权、请求体伪造 SteamID、枚举绕过、
可选的限流探测）。仅使用标准库，退出码 0/1/2 分别表示全部通过、存在失败、服务不可达。

This is the cross-platform implementation of the repository's API smoke test (it replaced a
PowerShell script so the project can run on Linux/macOS/Windows).
"""

import argparse
import base64
import json
import os
import random
import re
import sys
import urllib.error
import urllib.request

DEFAULT_BASE_URL = "http://localhost:5087"
DEFAULT_GAME_APP_ID = "unset"

# Set from --base-url in run(); read by call_api().
BASE_URL = DEFAULT_BASE_URL

# Set from --app-id in run(). The player API only exists under /g/{appId}/api/...,
# so every /api path is rewritten through this prefix in call_api().
GAME_APP_ID = DEFAULT_GAME_APP_ID

# The admin gate answers with a redirect chain that ends on the login page; the check
# asserts this suffix on the *final* URL (after redirects), not on a raw 302.
ADMIN_LOGIN_RE = re.compile(r"/admin/login$")

# Timeout for a single HTTP request.  Generous, because the very first request of a
# debug run may still be waiting for the app to warm up.
REQUEST_TIMEOUT_SECONDS = 30.0

# Claims the player token is allowed to carry; anything else is an unexpected leak.
# "game" is the game binding added by the multi-game upgrade: the token is only valid
# under /g/{appId} for the game whose numeric id it names.
ALLOWED_JWT_CLAIMS = ("sub", "game", "jti", "iss", "aud", "exp", "nbf", "iat")


# --------------------------------------------------------------------------- output

class _Ansi:
    RESET = "\033[0m"
    RED = "\033[31m"
    GREEN = "\033[32m"
    YELLOW = "\033[33m"
    CYAN = "\033[36m"


USE_COLOR = False


def _enable_ansi_on_windows():
    """Turn on VT processing so ANSI escapes render in legacy Windows consoles."""
    if os.name != "nt":
        return
    try:
        import ctypes

        kernel32 = ctypes.windll.kernel32
        handle = kernel32.GetStdHandle(-11)
        mode = ctypes.c_uint32()
        if kernel32.GetConsoleMode(handle, ctypes.byref(mode)):
            kernel32.SetConsoleMode(handle, mode.value | 0x0004)
    except Exception:
        pass


def init_output():
    global USE_COLOR
    # Windows consoles with legacy code pages must never crash the script.
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    if os.environ.get("NO_COLOR") is not None:
        USE_COLOR = False
        return
    try:
        is_tty = bool(sys.stdout.isatty())
    except Exception:
        is_tty = False
    USE_COLOR = is_tty
    if USE_COLOR:
        _enable_ansi_on_windows()


def write(text="", color=None):
    if USE_COLOR and color:
        print(color + text + _Ansi.RESET)
    else:
        print(text)


def write_head(text):
    write("")
    write("== {0} ==".format(text), _Ansi.CYAN)


def brief(text):
    """Single-line, length-capped rendering of a response body for error details."""
    if text is None:
        return ""
    if not isinstance(text, str):
        text = str(text)
    if not text.strip():
        return ""
    flat = " ".join(text.split())
    if len(flat) > 240:
        flat = flat[:240] + "..."
    return flat


# ------------------------------------------------------------------- responses/json

class Response(object):
    __slots__ = ("status", "text", "json", "location", "url")

    def __init__(self, status, text, parsed, location, url):
        self.status = status
        self.text = text
        self.json = parsed
        self.location = location
        self.url = url


def parse_json_safe(text):
    """Never raise on a non-JSON or empty body: error pages and redirects are not JSON."""
    if not text or not text.strip():
        return None
    try:
        return json.loads(text)
    except Exception:
        return None


def sget(obj, *keys):
    """Nested dictionary lookup; returns None as soon as a level is missing."""
    current = obj
    for key in keys:
        if not isinstance(current, dict):
            return None
        current = current.get(key)
    return current


def show(value):
    return "" if value is None else str(value)


def items(value):
    return value if isinstance(value, list) else []


def is_positive_int(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def _decode_body(response):
    try:
        raw = response.read()
    except Exception:
        return ""
    if not raw:
        return ""
    charset = "utf-8"
    try:
        content_type = response.headers.get("Content-Type", "") or ""
        for part in content_type.split(";"):
            part = part.strip()
            if part.lower().startswith("charset="):
                charset = part.split("=", 1)[1].strip().strip('"') or "utf-8"
    except Exception:
        charset = "utf-8"
    try:
        return raw.decode(charset, errors="replace")
    except LookupError:
        return raw.decode("utf-8", errors="replace")


def call_api(method, path, headers=None, body=None, prefix_game=True):
    """Perform one API call; HTTP error statuses come back as data, transport errors as status 0.

    Player API paths are rewritten to /g/{GAME_APP_ID}/api/... so the individual checks below can
    keep spelling the logical route. Pass prefix_game=False when deliberately probing the
    removed root paths.
    """
    if prefix_game and path.startswith("/api/"):
        path = "/g/{0}{1}".format(GAME_APP_ID, path)
    url = BASE_URL + path
    request_headers = dict(headers or {})
    data = None
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        request_headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=request_headers, method=method)
    try:
        # urllib follows redirects by default; response.geturl() is the final URL, which is
        # what the /admin check relies on (the Identity cookie challenge ends at /admin/login).
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            text = _decode_body(response)
            return Response(
                status=response.status,
                text=text,
                parsed=parse_json_safe(text),
                location=response.headers.get("Location", "") or "",
                url=response.geturl(),
            )
    except urllib.error.HTTPError as error:
        text = _decode_body(error)
        return Response(
            status=error.code,
            text=text,
            parsed=parse_json_safe(text),
            location=error.headers.get("Location", "") or "",
            url=error.geturl(),
        )
    except Exception:
        # URLError / socket.timeout / connection reset / TLS failure -> unreachable.
        return Response(status=0, text="", parsed=None, location="", url="")


# --------------------------------------------------------------------------- checks

RESULTS = []


def add_result(name, passed, detail):
    passed = bool(passed)
    RESULTS.append((name, passed, detail))
    tag = "PASS" if passed else "FAIL"
    color = _Ansi.GREEN if passed else _Ansi.RED
    write("[{0}] {1} -- {2}".format(tag, name, detail), color)


def assert_status(name, response, expected, extra=None):
    expected_list = list(expected) if isinstance(expected, (list, tuple)) else [expected]
    passed = response.status in expected_list
    detail = "status={0}, expected={1}".format(
        response.status, "/".join(str(item) for item in expected_list)
    )
    if extra:
        detail += "; " + extra
    if not passed:
        detail += "; body=" + brief(response.text)
    add_result(name, passed, detail)


def assert_true(name, condition, detail):
    add_result(name, condition, detail)


# ------------------------------------------------------------------------ identities

def new_steam_id():
    digits = "".join(random.choice("0123456789") for _ in range(9))
    return "76561198" + digits


def jwt_payload(token):
    if not token or not str(token).strip():
        return None
    parts = str(token).split(".")
    if len(parts) < 2:
        return None
    payload = parts[1].replace("-", "+").replace("_", "/")
    remainder = len(payload) % 4
    if remainder == 2:
        payload += "=="
    elif remainder == 3:
        payload += "="
    try:
        return json.loads(base64.b64decode(payload).decode("utf-8"))
    except Exception:
        return None


def login(steam_id, ticket):
    body = {"ticket": ticket} if ticket else {"debugSteamId": steam_id}
    return call_api("POST", "/api/auth/steam", body=body)


# ------------------------------------------------------------------------------ main

def run(args):
    global BASE_URL, GAME_APP_ID
    BASE_URL = args.base_url.rstrip("/")
    GAME_APP_ID = args.app_id.strip().strip("/")

    # 玩家 API 只在 /g/{appId}/api/... 下提供，没有根路径也没有默认游戏：
    # 没给对 AppID 的话，下面每条检查都会撞在同一个 404 game_not_found 上，
    # 与其让人对着十几条失败猜，不如在这里说清楚。
    if not GAME_APP_ID.isdigit():
        write(
            "--app-id must be the numeric Steam AppID of the game to probe "
            "(the player API lives under /g/<appId>); got '{0}'".format(GAME_APP_ID),
            _Ansi.RED,
        )
        return 2

    steam_id_a = args.steam_id_a or new_steam_id()
    steam_id_b = args.steam_id_b or new_steam_id()

    use_debug_login = not (args.ticket or "").strip()
    mode = "debug login (debugSteamId)" if use_debug_login else "real ticket"

    write("Target : {0}".format(BASE_URL))
    write("Game   : /g/{0}".format(GAME_APP_ID))
    write("Mode   : {0}".format(mode))
    write("PlayerA: {0}".format(steam_id_a))
    write("PlayerB: {0}".format(steam_id_b))

    # ---------------------------------------------------------------- health
    write_head("health")
    health = call_api("GET", "/health")
    if health.status == 0:
        write("Cannot reach {0}. Start the app first:".format(BASE_URL), _Ansi.RED)
        write("  docker compose up -d postgres", _Ansi.YELLOW)
        write("  dotnet run --project src/GameFeedback", _Ansi.YELLOW)
        return 2
    assert_status("GET /health", health, 200)
    assert_true(
        "health payload",
        sget(health.json, "status") == "ok",
        "status={0}".format(show(sget(health.json, "status"))),
    )

    # ------------------------------------------------- anonymous access (401)
    write_head("anonymous access must be rejected")
    anon_create = call_api(
        "POST", "/api/feedback", body={"type": "Bug", "title": "anon", "content": "anon"}
    )
    assert_status("POST /api/feedback without token", anon_create, 401)
    anon_mine = call_api("GET", "/api/feedback/mine")
    assert_status("GET /api/feedback/mine without token", anon_mine, 401)
    anon_detail = call_api("GET", "/api/feedback/1")
    assert_status("GET /api/feedback/1 without token", anon_detail, 401)
    anon_comment = call_api("POST", "/api/feedback/1/comments", body={"content": "anon"})
    assert_status("POST /api/feedback/1/comments without token", anon_comment, 401)

    # ---------------------------------------------------------------- login
    write_head("steam login")
    login_a = login(steam_id_a, args.ticket)
    if login_a.status != 200:
        assert_status("POST /api/auth/steam (player A)", login_a, 200)
        write("")
        write("Login failed. Check:", _Ansi.YELLOW)
        write(
            "  400 -> Steam:DebugSkipTicketValidation is off, or the SteamID64 is malformed (needs 17 digits)",
            _Ansi.YELLOW,
        )
        write(
            "  401 -> real ticket path: the ticket was rejected by Steam, or Steam is unreachable (fail closed)",
            _Ansi.YELLOW,
        )
        write(
            "  429 -> login rate limit hit (10/min per client IP); wait a minute or restart the app",
            _Ansi.YELLOW,
        )
        write(
            "  401 steam_unavailable -> this game has no Steam AppID or credential yet;"
            " configure it in the admin UI (Game / Steam credential pages)",
            _Ansi.YELLOW,
        )
        write(
            "  404 game_not_found -> this instance has no game with AppID '{0}'; pass --app-id".format(GAME_APP_ID),
            _Ansi.YELLOW,
        )
        return 1
    assert_status("POST /api/auth/steam (player A)", login_a, 200)
    token_a = sget(login_a.json, "accessToken")
    assert_true(
        "login A returns accessToken",
        bool(token_a) and bool(str(token_a).strip()),
        "length={0}".format(len(str(token_a)) if token_a else 0),
    )
    assert_true(
        "login A returns own player.steamId",
        sget(login_a.json, "player", "steamId") == steam_id_a,
        "player.steamId={0}".format(show(sget(login_a.json, "player", "steamId"))),
    )
    assert_true(
        "login A response leaks no ticket",
        '"ticket"' not in login_a.text,
        "no ticket echoed in response body",
    )
    auth_a = {"Authorization": "Bearer {0}".format(token_a)}

    payload = jwt_payload(token_a)
    assert_true(
        "JWT sub == SteamID64",
        sget(payload, "sub") == steam_id_a,
        "sub={0}".format(show(sget(payload, "sub"))),
    )
    claim_names = list(payload.keys()) if isinstance(payload, dict) else []
    assert_true(
        "JWT carries only the expected claims (sub/game/jti/iss/aud/exp/nbf)",
        all(claim in ALLOWED_JWT_CLAIMS for claim in claim_names),
        "claims={0}".format(",".join(claim_names)),
    )
    # 令牌绑定游戏：路径里的 AppID 与声明必须指向同一个游戏，否则跨游戏重放就成立了。
    assert_true(
        "JWT carries a numeric game claim",
        str(sget(payload, "game") or "").isdigit(),
        "game={0}".format(show(sget(payload, "game"))),
    )

    # Player B always logs in through the debug body here; only the debug mode asserts it.
    login_b = login(steam_id_b, None)
    can_check_ownership = False
    token_b = None
    if use_debug_login:
        assert_status("POST /api/auth/steam (player B)", login_b, 200)
        token_b = sget(login_b.json, "accessToken")
        can_check_ownership = bool(token_b) and bool(str(token_b).strip())
    else:
        add_result(
            "POST /api/auth/steam (player B)",
            True,
            "SKIP (single ticket supplied, cross-player checks skipped)",
        )
    auth_b = {"Authorization": "Bearer {0}".format(token_b)} if can_check_ownership else None

    # --------------------------------------------------------- create feedback
    write_head("create feedback")
    create = call_api(
        "POST",
        "/api/feedback",
        headers=auth_a,
        body={
            "type": "Bug",
            "title": "smoke: arena_01 crash on load",
            "content": "Reproducible 100%: load arena_01 in 1v1 and the client crashes.",
            "gameVersion": "1.2.3",
            "buildNumber": "456",
            "operatingSystem": "Windows 11",
            "gpu": "RTX 4070",
            "locale": "zh-CN",
            "map": "arena_01",
            "character": "mage",
        },
    )
    assert_status("POST /api/feedback", create, 201)
    feedback_id = sget(create.json, "id")
    assert_true(
        "created feedback has id/type/status",
        is_positive_int(feedback_id)
        and sget(create.json, "type") == "Bug"
        and sget(create.json, "status") == "Open",
        "id={0} type={1} status={2}".format(
            show(feedback_id), show(sget(create.json, "type")), show(sget(create.json, "status"))
        ),
    )
    assert_true(
        "created feedback echoes metadata",
        sget(create.json, "map") == "arena_01" and sget(create.json, "buildNumber") == "456",
        "map={0} build={1}".format(
            show(sget(create.json, "map")), show(sget(create.json, "buildNumber"))
        ),
    )
    assert_true(
        "201 Location points at the new feedback",
        create.location == "/g/{0}/api/feedback/{1}".format(GAME_APP_ID, feedback_id),
        "Location={0}".format(create.location),
    )

    # --------------------------------------------- SteamID spoofing is ignored
    write_head("request body cannot choose the owner")
    spoof = call_api(
        "POST",
        "/api/feedback",
        headers=auth_a,
        body={
            "type": "Suggestion",
            "title": "smoke: spoof attempt",
            "content": "body claims to belong to player B",
            "steamId": steam_id_b,
            "playerId": 999999,
        },
    )
    if spoof.status == 201:
        spoof_id = sget(spoof.json, "id")
        if can_check_ownership:
            spoof_read = call_api("GET", "/api/feedback/{0}".format(spoof_id), headers=auth_b)
            assert_status("spoofed steamId is ignored (B cannot read it)", spoof_read, 404)
            spoof_mine = call_api("GET", "/api/feedback/mine", headers=auth_b)
            assert_true(
                "spoofed feedback is not in B's list",
                len([item for item in items(spoof_mine.json) if sget(item, "id") == spoof_id]) == 0,
                "B mine count={0}".format(len(items(spoof_mine.json))),
            )
        else:
            add_result("spoofed steamId is ignored", True, "SKIP (needs player B token)")
    else:
        add_result(
            "spoofed steamId is ignored",
            False,
            "unexpected status={0}; body={1}".format(spoof.status, brief(spoof.text)),
        )

    # -------------------------------------------------------------- validation
    write_head("validation")
    for bad_type in ("1", "Bug,Suggestion"):
        bad = call_api(
            "POST",
            "/api/feedback",
            headers=auth_a,
            body={"type": bad_type, "title": "bad type", "content": "bad type"},
        )
        assert_status(
            "POST /api/feedback type='{0}' rejected".format(bad_type), bad, 400
        )

    oversized_title = "x" * 201
    # Player B is used here so player A keeps write-quota headroom (5 per 10 min).
    oversized_headers = auth_b if can_check_ownership else auth_a
    oversized = call_api(
        "POST",
        "/api/feedback",
        headers=oversized_headers,
        body={"type": "Bug", "title": oversized_title, "content": "title too long"},
    )
    assert_status("POST /api/feedback title>200 rejected", oversized, 400)

    empty_comment = call_api(
        "POST",
        "/api/feedback/{0}/comments".format(feedback_id),
        headers=auth_a,
        body={"content": "   "},
    )
    assert_status("empty comment rejected", empty_comment, 400)

    mine_after = call_api("GET", "/api/feedback/mine", headers=auth_a)
    assert_true(
        "rejected feedback was not persisted",
        len(
            [
                item
                for item in items(mine_after.json)
                if str(sget(item, "title") or "").startswith("bad type")
            ]
        )
        == 0,
        "A mine count={0}".format(len(items(mine_after.json))),
    )

    # --------------------------------------------------------------- read paths
    write_head("list and detail")
    mine = call_api("GET", "/api/feedback/mine", headers=auth_a)
    assert_status("GET /api/feedback/mine", mine, 200)
    mine_items = items(mine.json)
    assert_true(
        "own feedback appears in mine",
        len([item for item in mine_items if sget(item, "id") == feedback_id]) == 1,
        "items={0}, newest id={1}".format(
            len(mine_items), show(sget(mine_items[0], "id") if mine_items else None)
        ),
    )

    detail = call_api("GET", "/api/feedback/{0}".format(feedback_id), headers=auth_a)
    assert_status("GET /api/feedback/{id} (owner)", detail, 200)
    assert_true(
        "detail starts with no comments",
        len(items(sget(detail.json, "comments"))) == 0,
        "comments={0}".format(len(items(sget(detail.json, "comments")))),
    )

    missing = call_api("GET", "/api/feedback/2147483600", headers=auth_a)
    assert_status("GET missing feedback -> 404", missing, 404)

    if can_check_ownership:
        foreign_read = call_api(
            "GET", "/api/feedback/{0}".format(feedback_id), headers=auth_b
        )
        assert_status("GET other player feedback -> 404", foreign_read, 404)
        foreign_comment = call_api(
            "POST",
            "/api/feedback/{0}/comments".format(feedback_id),
            headers=auth_b,
            body={"content": "not my feedback"},
        )
        assert_status("comment on other player feedback -> 404", foreign_comment, 404)
    else:
        add_result("GET other player feedback -> 404", True, "SKIP (needs player B token)")
        add_result(
            "comment on other player feedback -> 404", True, "SKIP (needs player B token)"
        )

    # --------------------------------------------------------------- comments
    write_head("player comment")
    comment = call_api(
        "POST",
        "/api/feedback/{0}/comments".format(feedback_id),
        headers=auth_a,
        body={"content": "smoke: still broken after a driver update"},
    )
    assert_status("POST /api/feedback/{id}/comments (owner)", comment, 201)
    assert_true(
        "comment authorType is Player",
        sget(comment.json, "authorType") == "Player",
        "authorType={0}".format(show(sget(comment.json, "authorType"))),
    )

    detail_after = call_api(
        "GET", "/api/feedback/{0}".format(feedback_id), headers=auth_a
    )
    assert_true(
        "detail shows the new comment",
        len(items(sget(detail_after.json, "comments"))) == 1,
        "comments={0}".format(len(items(sget(detail_after.json, "comments")))),
    )

    # ------------------------------------------------- identity separation
    write_head("identity separation")
    # The admin gate challenges with the Identity cookie scheme, so a browser-less request
    # ends up on /admin/login: assert on the final URI instead of a raw 302.
    admin_no_cookie = call_api("GET", "/admin")
    assert_true(
        "GET /admin without cookie lands on the login page",
        bool(ADMIN_LOGIN_RE.search(admin_no_cookie.url or "")),
        "final={0} status={1}".format(admin_no_cookie.url, admin_no_cookie.status),
    )

    if can_check_ownership:
        admin_with_player_jwt = call_api("GET", "/admin", headers=auth_a)
        assert_true(
            "player JWT does not open /admin",
            bool(ADMIN_LOGIN_RE.search(admin_with_player_jwt.url or "")),
            "final={0} status={1}".format(
                admin_with_player_jwt.url, admin_with_player_jwt.status
            ),
        )
    else:
        add_result("player JWT does not open /admin", True, "SKIP (needs player A token)")

    login_page = call_api("GET", "/admin/login")
    assert_status("GET /admin/login is public", login_page, 200)

    # ---------------------------------------------------------------- swagger
    write_head("openapi document")
    openapi = call_api("GET", "/openapi/v1.json")
    if openapi.status == 200:
        paths = sget(openapi.json, "paths")
        path_names = list(paths.keys()) if isinstance(paths, dict) else []
        assert_true(
            "openapi documents the 5 player routes",
            all(
                route in path_names
                for route in (
                    "/api/auth/steam",
                    "/api/feedback",
                    "/api/feedback/mine",
                    "/api/feedback/{id}",
                    "/api/feedback/{id}/comments",
                )
            ),
            "paths={0}".format(",".join(path_names)),
        )
        bearer_scheme = sget(openapi.json, "components", "securitySchemes", "Bearer", "scheme")
        assert_true(
            "openapi registers the Bearer scheme",
            bearer_scheme == "bearer",
            "scheme={0}".format(show(bearer_scheme)),
        )
    else:
        assert_status("GET /openapi/v1.json", openapi, 200)
        write(
            "OpenAPI is only mapped in Development or with Swagger:Enabled=true.",
            _Ansi.YELLOW,
        )

    # ------------------------------------------------------------ rate limiting
    if args.include_rate_limit_probe:
        write_head("rate limiting probe (5 creates / 10 min / player)")
        player_c = new_steam_id()
        login_c = login(player_c, None)
        if login_c.status == 200:
            auth_c = {"Authorization": "Bearer {0}".format(sget(login_c.json, "accessToken"))}
            statuses = []
            for index in range(1, 7):
                probe = call_api(
                    "POST",
                    "/api/feedback",
                    headers=auth_c,
                    body={
                        "type": "Other",
                        "title": "probe {0}".format(index),
                        "content": "rate limit probe",
                    },
                )
                statuses.append(probe.status)
            assert_true(
                "POST /api/feedback returns 429 after 5 per 10 min",
                429 in statuses,
                "statuses={0}".format(",".join(str(status) for status in statuses)),
            )
        else:
            add_result(
                "rate limit probe login",
                False,
                "status={0} body={1}".format(login_c.status, brief(login_c.text)),
            )
    else:
        write_head("rate limiting probe")
        add_result(
            "POST /api/feedback rate limit",
            True,
            "SKIP (pass --include-rate-limit-probe to run; consumes 6 requests)",
        )

    # ------------------------------------------------------------------ summary
    write("")
    failed = [item for item in RESULTS if not item[1]]
    passed = len(RESULTS) - len(failed)
    color = _Ansi.GREEN if not failed else _Ansi.RED
    write("SUMMARY: {0}/{1} checks passed".format(passed, len(RESULTS)), color)
    if failed:
        for name, _passed, detail in failed:
            write("  FAIL {0} -- {1}".format(name, detail), _Ansi.RED)
        return 1
    return 0


def build_parser():
    parser = argparse.ArgumentParser(
        prog="smoke_player_api.py",
        description=(
            "Smoke-test the player API of a running GameFeedback instance "
            "(exit 0 = all passed, 1 = failures, 2 = server unreachable)."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "examples:\n"
            "  python scripts/smoke_player_api.py\n"
            "  python scripts/smoke_player_api.py --include-rate-limit-probe\n"
            "  python scripts/smoke_player_api.py --ticket <steam-web-api-ticket>\n"
        ),
    )
    parser.add_argument(
        "--base-url",
        default=DEFAULT_BASE_URL,
        help="base URL of the running instance (default: %(default)s)",
    )
    parser.add_argument(
        "--app-id",
        default=DEFAULT_GAME_APP_ID,
        help="Steam AppID of the game to probe (the player API lives under /g/<appId>); required",
    )
    parser.add_argument(
        "--ticket",
        help="real Steam Web API ticket; enables the real verification path instead of the debug login",
    )
    parser.add_argument(
        "--steam-id-a",
        help="fixed SteamID64 for player A (17 digits); default: random per run",
    )
    parser.add_argument(
        "--steam-id-b",
        help="fixed SteamID64 for player B (17 digits); default: random per run",
    )
    parser.add_argument(
        "--include-rate-limit-probe",
        action="store_true",
        help="also probe POST /api/feedback rate limiting with a third, fresh player",
    )
    return parser


def main(argv=None):
    init_output()
    args = build_parser().parse_args(argv)
    try:
        return run(args)
    except KeyboardInterrupt:
        write("")
        write("Interrupted.", _Ansi.YELLOW)
        return 1


if __name__ == "__main__":
    sys.exit(main())
