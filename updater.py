"""Stage versioned releases and apply them with rollback on next launch."""
import json
import os
import shutil
import sys
import tempfile
import zipfile
from pathlib import Path
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parent
UPDATES = ROOT / "updates"
ALLOWED = {"electron", "court_creator"}
FILES = {"package.json", "updater.py", "Launch NBA 2K Court Creator.bat"}

def version(value):
    return tuple(int(part) for part in value.lstrip("v").split("."))

def fetch(url):
    with urlopen(Request(url, headers={"User-Agent": "CourtCreator-Updater"}), timeout=15) as response:
        return response.read()

def stage():
    if (ROOT / ".git").exists():
        return
    release = json.loads(fetch("https://api.github.com/repos/carrnate85-stack/nba2k-court-creator/releases/latest"))
    current = json.loads((ROOT / "package.json").read_text())
    if version(release["tag_name"]) <= version(current["version"]):
        return
    asset = next((item for item in release["assets"] if item["name"] == "court-creator-update.zip"), None)
    if not asset:
        raise RuntimeError("Release requires a full installation")
    UPDATES.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=UPDATES) as temporary:
        archive_path = Path(temporary) / "release.zip"
        archive_path.write_bytes(fetch(asset["browser_download_url"]))
        destination = Path(temporary) / "staged"
        destination.mkdir()
        with zipfile.ZipFile(archive_path) as archive:
            for member in archive.infolist():
                relative = Path(member.filename)
                if relative.is_absolute() or ".." in relative.parts:
                    raise ValueError("Invalid update path")
                if relative.parts and (relative.parts[0] in ALLOWED or relative.as_posix() in FILES):
                    archive.extract(member, destination)
        manifest = json.loads((destination / "package.json").read_text())
        if version(manifest["version"]) != version(release["tag_name"]):
            raise ValueError("Release version mismatch")
        if manifest.get("devDependencies") != current.get("devDependencies"):
            raise ValueError("This release requires a full installation")
        pending = UPDATES / "pending"
        if pending.exists():
            shutil.rmtree(pending)
        shutil.copytree(destination, pending)

def apply():
    if (ROOT / ".git").exists():
        return
    pending = UPDATES / "pending"
    if not pending.exists():
        return
    backup = UPDATES / "rollback"
    if backup.exists():
        shutil.rmtree(backup)
    backup.mkdir(parents=True)
    changed = []
    try:
        for source in pending.rglob("*"):
            if not source.is_file():
                continue
            relative = source.relative_to(pending)
            if relative.parts[0] not in ALLOWED and relative.as_posix() not in FILES:
                continue
            target = ROOT / relative
            existed = target.exists()
            if existed:
                saved = backup / relative
                saved.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(target, saved)
            changed.append((relative, existed))
            target.parent.mkdir(parents=True, exist_ok=True)
            staged = target.with_name(target.name + ".update-tmp")
            shutil.copy2(source, staged)
            os.replace(staged, target)
    except Exception:
        for relative, existed in reversed(changed):
            if existed:
                shutil.copy2(backup / relative, ROOT / relative)
            else:
                (ROOT / relative).unlink(missing_ok=True)
        raise
    shutil.rmtree(pending)

if __name__ == "__main__":
    try:
        apply() if "--apply" in sys.argv else stage()
    except Exception as error:
        UPDATES.mkdir(exist_ok=True)
        (UPDATES / "last-error.txt").write_text(str(error))
