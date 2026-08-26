from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path
import re
import shutil


ARENA_INT = re.compile(r"^levels/arena_.*_int\.iff$", re.IGNORECASE)
ARENA_FLOOR = re.compile(r"^levels/arena_.*_int_floor\.iff$", re.IGNORECASE)
SHARED_COURT_TEXTURE = re.compile(
    r"^shared/.*/(floor_.*court.*|floor_.*apron.*|__floor00__.*|t_court_overlay_lines\..*)\.(mip0|tld)$",
    re.IGNORECASE,
)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-root", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--include-arena-int", action="store_true")
    parser.add_argument("--only-arena-int", action="store_true")
    args = parser.parse_args()

    game_root = Path(args.game_root)
    output_root = Path(args.output)
    manifest_path = game_root / "manifest"
    if not manifest_path.exists():
        raise SystemExit(f"Manifest not found: {manifest_path}")

    entries = list(read_manifest(manifest_path))
    court_entries = [entry for entry in entries if classify(entry["path"]) is not None]
    if args.only_arena_int:
        extract_entries = [entry for entry in court_entries if classify(entry["path"]) == "arena_int"]
    else:
        extract_entries = [
            entry
            for entry in court_entries
            if classify(entry["path"]) in {"arena_floor", "shared_court_texture"}
            or (args.include_arena_int and classify(entry["path"]) == "arena_int")
        ]

    output_root.mkdir(parents=True, exist_ok=True)
    write_csv(output_root / "court_manifest_entries.csv", court_entries)
    write_csv(output_root / "extracted_manifest_entries.csv", extract_entries)
    extracted_root = output_root / "extracted"
    extracted_count = extract(game_root, extracted_root, extract_entries)

    summary = {
        "game_root": str(game_root),
        "manifest": str(manifest_path),
        "output": str(output_root),
        "court_manifest_entries": len(court_entries),
        "extracted_entries": extracted_count,
        "extracted_bytes": sum(entry["size"] for entry in extract_entries),
        "include_arena_int": args.include_arena_int,
        "only_arena_int": args.only_arena_int,
        "classes": summarize(court_entries),
    }
    (output_root / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary))


def read_manifest(path: Path):
    with path.open("r", encoding="utf-8", errors="replace", newline="") as handle:
        for line_number, line in enumerate(handle, start=1):
            parts = line.rstrip("\n").split(",")
            if len(parts) != 4:
                continue
            try:
                offset = int(parts[2])
                size = int(parts[3])
            except ValueError:
                continue
            yield {
                "line": line_number,
                "path": parts[0],
                "chunk": parts[1],
                "offset": offset,
                "size": size,
                "class": classify(parts[0]),
            }


def classify(path: str) -> str | None:
    if ARENA_FLOOR.search(path):
        return "arena_floor"
    if ARENA_INT.search(path):
        return "arena_int"
    if SHARED_COURT_TEXTURE.search(path):
        return "shared_court_texture"
    return None


def summarize(entries: list[dict]) -> dict[str, dict[str, int]]:
    summary: dict[str, dict[str, int]] = {}
    for entry in entries:
        item = summary.setdefault(entry["class"], {"files": 0, "bytes": 0})
        item["files"] += 1
        item["bytes"] += entry["size"]
    return summary


def write_csv(path: Path, entries: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=["class", "path", "chunk", "offset", "size", "line"])
        writer.writeheader()
        writer.writerows(entries)


def extract(game_root: Path, output_root: Path, entries: list[dict]) -> int:
    output_root.mkdir(parents=True, exist_ok=True)
    handles: dict[str, object] = {}
    try:
        for index, entry in enumerate(entries, start=1):
            chunk = entry["chunk"]
            source = game_root / chunk
            if not source.exists():
                raise FileNotFoundError(source)
            handle = handles.get(chunk)
            if handle is None:
                handle = source.open("rb")
                handles[chunk] = handle
            destination = output_root / Path(entry["path"])
            destination.parent.mkdir(parents=True, exist_ok=True)
            handle.seek(entry["offset"])
            with destination.open("wb") as out:
                copy_exact(handle, out, entry["size"])
            if index % 250 == 0:
                print(f"extracted {index}/{len(entries)}")
        return len(entries)
    finally:
        for handle in handles.values():
            handle.close()


def copy_exact(source, destination, size: int) -> None:
    remaining = size
    while remaining:
        chunk = source.read(min(1024 * 1024, remaining))
        if not chunk:
            raise EOFError("Unexpected end of package while extracting manifest entry.")
        destination.write(chunk)
        remaining -= len(chunk)


if __name__ == "__main__":
    main()
