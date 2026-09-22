"""Rewrite saved Court Creator paths after moving the app and its local assets."""

from __future__ import annotations

import json
import os
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parent.parent
HOME = Path.home()
PATH_MOVES = (
    (HOME / "OneDrive" / "Documents" / "2kcourtmodder", PROJECT_ROOT / "assets"),
    (HOME / "OneDrive" / "Documents" / "NBA 2K Court Creator", PROJECT_ROOT),
    (HOME / "NBA 2K Court Creator", PROJECT_ROOT),
)


def relocate(value):
    if isinstance(value, dict):
        return {key: relocate(item) for key, item in value.items()}
    if isinstance(value, list):
        return [relocate(item) for item in value]
    if not isinstance(value, str):
        return value
    for old_root, new_root in PATH_MOVES:
        old = str(old_root)
        if value.casefold() == old.casefold():
            return str(new_root)
        if value.casefold().startswith((old + os.sep).casefold()):
            return str(new_root / value[len(old) + 1:])
    return value


def rewrite_json(path: Path) -> bool:
    original = json.loads(path.read_text(encoding="utf-8"))
    revised = relocate(original)
    if revised == original:
        return False
    temporary = path.with_name(path.name + ".relocating")
    try:
        temporary.write_text(json.dumps(revised, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
    return True


def main() -> None:
    folders = (
        PROJECT_ROOT / "assets",
        PROJECT_ROOT / "custom_floors",
        PROJECT_ROOT / "logos",
        PROJECT_ROOT / "projects",
    )
    paths = [path for folder in folders if folder.exists() for path in folder.rglob("*.json")]
    paths.extend((PROJECT_ROOT / "data").glob("court_*.json"))
    paths.extend((HOME / "AppData" / "Roaming" / "nba2k-court-creator").glob("recovery*.json"))
    for path in paths:
        if rewrite_json(path):
            print(f"updated {path}")


if __name__ == "__main__":
    main()
