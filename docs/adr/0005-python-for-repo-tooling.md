# ADR-0005: Python (Standard Library Only) for Repository Tooling

- Status: Accepted
- Date: 2026-09-24

## Context

This repository ships a handful of hand-run scripts: an API smoke test, a local-run helper that consumes `.env.local`, the addon's packaging script and its end-to-end verification gate, and the Godot host project's install/verify tool. They began as PowerShell scripts, which was the shortest path on the original Windows workstation.

That choice does not survive contact with other systems. PowerShell means `pwsh` (or Windows PowerShell) has to exist, and it does not on a plain Linux container image — the addon verifiers could not be run at all in one recorded review for exactly that reason. The scripts also leaned on Windows-flavoured details: `%USERPROFILE%`, `.ps1` path separators, `Invoke-WebRequest` semantics, `Compress-Archive`, and process APIs that behave differently off Windows. The purpose of these scripts is not to be Windows tooling; it is to be runnable by whoever is testing the system, on whatever they are sitting in front of.

Two alternatives were considered. Keeping PowerShell and shipping a container or a bootstrap that installs `pwsh` adds a heavyweight dependency to something that should be a one-liner. Keeping both implementations side by side invites exactly the drift the addon's manifest allowlist exists to prevent.

## Decision

Write repository tooling in Python, standard library only, so the same command works on Windows, macOS and Linux with nothing installed beyond a Python 3.9+ interpreter:

- `scripts/run_local.py` — start the stack locally from `.env.local`
- `scripts/smoke_player_api.py` — end-to-end checks against a running server
- `addons/gd_feedback/tools/package.py` — addon identity, allowlist and archive
- `addons/gd_feedback/tests/verify.py` — the addon's release gate
- `tests/godot-feedback-host/tools/sync_addon.py` — install/verify the addon copy inside the host project

No third-party packages, no virtualenv, no packaging step, no `requirements.txt`. The scripts keep the contracts the PowerShell versions had, because they are referenced from the docs and from the release discipline:

- the docs spell the commands as `python <script>`; on a system where only `python3` exists (Debian/Ubuntu, some CI images), use `python3` — the scripts carry a `python3` shebang and never depend on the interpreter's file name;
- the documented exit codes (`0` pass, `1` a check failed, `2` unreachable or missing input) stay as they are;
- the stable markers (`GD_FEEDBACK_VERIFY PASS`, `GD_FEEDBACK_MANIFEST PASS`, `HARNESS SUMMARY`, `SUMMARY: n/m checks passed`) stay as they are;
- paths resolve from the script's own `__file__`, never from the current directory, so a script can be run from anywhere;
- subprocesses are launched with argument lists and no shell, so quoting and wildcards behave identically on every platform;
- stdout/stderr are reconfigured to UTF-8 with `errors="replace"` where the platform allows, so Chinese output cannot crash a legacy console code page;
- any place that needs a writable working directory tries candidates for real (system temp first, then the gitignored `artifacts/`) instead of assuming TEMP is usable — a restricted sandbox that denies writes under TEMP is otherwise indistinguishable from a broken build;
- the PowerShell scripts are deleted rather than kept alongside, and every reference in `README.md`, `docs/testing.md`, `docs/adr/0004-godot-feedback-client-addon.md` and the addon/host READMEs points at the Python entry points.

## Consequences

- The verification gate now runs on any machine with Python and .NET, including the Linux containers used for containerised work; that was the point.
- Reviewers who know PowerShell need to read Python for the tooling. The scripts are written to be boringly readable — no classes beyond a small result counter, no dependencies, one file per tool.
- Python becomes an implicit development prerequisite for running the gates. It is not needed to build or run the service itself, and nothing in `docker-compose.yml` or the Dockerfile depends on it.
- The `*.py` scripts are covered by the same conventions as the rest of the repo: 4-space indentation, LF, forward-slash paths in the manifest, and the addon's own `.editorconfig` keeps applying to the addon's shipped files.
