from __future__ import annotations

import json
from pathlib import Path
import shutil
import tempfile
from urllib.request import Request, urlopen
import zipfile


PROJECT_ROOT = Path(__file__).resolve().parent
CONFIG_PATH = PROJECT_ROOT / "data" / "update_config.json"
DEFAULT_REPO = "carrnate85-stack/nba2k-court-creator"
DEFAULT_BRANCH = "main"
PRESERVE_PATHS = {
    "data/court_presets.json",
    "custom_floors",
    "outputs",
    "templates",
}


def main() -> None:
    config = load_config()
    repo = str(config.get("repository", DEFAULT_REPO)).strip() or DEFAULT_REPO
    branch = str(config.get("branch", DEFAULT_BRANCH)).strip() or DEFAULT_BRANCH
    url = f"https://github.com/{repo}/archive/refs/heads/{branch}.zip"

    try:
        print(f"Checking GitHub for {repo} ({branch})...")
        with tempfile.TemporaryDirectory(prefix="nba2k-court-update-") as temp_dir:
            temp_path = Path(temp_dir)
            zip_path = temp_path / "update.zip"
            download(url, zip_path)
            with zipfile.ZipFile(zip_path) as archive:
                archive.extractall(temp_path)
            extracted_roots = [item for item in temp_path.iterdir() if item.is_dir()]
            if not extracted_roots:
                raise RuntimeError("The GitHub download did not contain an app folder.")
            source_root = extracted_roots[0]
            apply_update(source_root)
        print("Update complete.")
    except Exception as exc:
        print(f"Update skipped: {exc}")


def load_config() -> dict:
    if not CONFIG_PATH.exists():
        return {}
    try:
        return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def download(url: str, output_path: Path) -> None:
    request = Request(url, headers={"User-Agent": "NBA2KCourtCreatorUpdater/1.0"})
    with urlopen(request, timeout=60) as response:
        output_path.write_bytes(response.read())


def apply_update(source_root: Path) -> None:
    for source in source_root.rglob("*"):
        relative = source.relative_to(source_root).as_posix()
        if should_preserve(relative):
            continue
        destination = PROJECT_ROOT / relative
        if source.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)


def should_preserve(relative_path: str) -> bool:
    for preserved in PRESERVE_PATHS:
        if relative_path == preserved or relative_path.startswith(f"{preserved}/"):
            return True
    return False


if __name__ == "__main__":
    main()
