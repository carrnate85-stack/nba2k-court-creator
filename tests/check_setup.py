"""Audit a healthy local setup without installs, builds, or opening the app."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parent.parent


def snapshot():
    files = [ROOT / "requirements.txt"]
    for folder in ("runtime/python", "desktop"):
        files.extend(path for path in (ROOT / folder).rglob("*") if path.is_file())
    print("Hashing " + str(len(files)) + " runtime/build files...", flush=True)
    result = {}
    for path in files:
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        metadata = path.stat()
        result[str(path.relative_to(ROOT))] = (metadata.st_size, metadata.st_mtime_ns, digest.hexdigest())
    return result


def execute(command):
    result = subprocess.run(command, cwd=ROOT, capture_output=True, encoding="utf-8", errors="strict",
                            timeout=60, check=False, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if result.returncode:
        raise RuntimeError("Setup audit command failed: " + (result.stderr + "\n" + result.stdout).strip())
    return result.stdout


def main():
    if os.name != "nt":
        raise SystemExit("This audit checks the Windows setup wrapper.")
    python = ROOT / "runtime/python/python.exe"
    if not python.is_file():
        python = ROOT / "runtime/python/Scripts/python.exe"
    helper = ROOT / "tools/setup_court_creator.py"
    check = [str(python), "-I", "-B", str(helper), "--project-root", str(ROOT), "--check-only", "--json"]
    initial = json.loads(execute(check))
    if not initial.get("ready"):
        raise SystemExit("The audit requires a healthy installation; repairs will not be attempted.")
    # cmd.exe requires native quote syntax, not subprocess.list2cmdline escaping.
    wrapper = f'"{os.environ.get("ComSpec", "cmd.exe")}" /d /s /c ""{ROOT / "Setup Court Creator.bat"}" --check-only --json"'
    json.loads(execute(wrapper))
    before = snapshot()
    report = json.loads(execute(wrapper))
    if not report.get("ready") or report.get("scope") != "runtime-and-build-only":
        raise AssertionError("The setup wrapper did not return healthy structured diagnostics.")
    execute([str(python), "-I", "-B", str(helper), "--project-root", str(ROOT)])
    after = snapshot()
    if before != after:
        changed = sorted(name for name in before.keys() | after.keys() if before.get(name) != after.get(name))
        raise AssertionError("Healthy setup changed files: " + ", ".join(changed))
    result = {"ready": True, "scope": report["scope"], "filesAudited": len(before),
              "bytesAudited": sum(item[0] for item in before.values()), "bytesAndModificationTimesUnchanged": True,
              "batchCheckOnlyPassed": True, "healthySetupPassed": True, "appLaunchRequested": False,
              "pythonVersion": report["python"]["version"], "dotnetPath": report["dotnet"]["path"]}
    output = ROOT / "outputs/setup-audit.json"
    output.parent.mkdir(exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
