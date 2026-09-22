"""Build lightweight court-browser thumbnails from extracted floor textures."""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import re

from PIL import Image, ImageDraw, ImageEnhance, ImageOps


PROJECT_ROOT = Path(__file__).resolve().parent.parent


def normalized(value: str) -> str:
    return " ".join(re.sub(r"[^a-z0-9]+", " ", value.casefold()).split())


def load_palettes(path: Path) -> list[dict]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []
    return data.get("palettes", data if isinstance(data, list) else [])


def team_colors(name: str, palettes: list[dict]) -> tuple[str, str]:
    target = normalized(name)
    matches: list[tuple[int, dict]] = []
    for palette in palettes:
        team = normalized(str(palette.get("team", "")))
        if not team:
            continue
        aliases = {team, team.split()[-1]}
        if team.endswith("trail blazers"):
            aliases.add("trail blazers")
        if any(alias in target for alias in aliases):
            matches.append((max(len(alias) for alias in aliases if alias in target), palette))
    palette = max(matches, default=(0, {}), key=lambda item: item[0])[1]
    colors = [str(item.get("hex", "")) for item in palette.get("colors", [])]
    colors = [value for value in colors if re.fullmatch(r"#[0-9a-fA-F]{6}", value)]
    usable = [value for value in colors if sum(int(value[index:index + 2], 16) for index in (1, 3, 5)) < 675]
    primary = (usable or colors or ["#19583F"])[0]
    secondary = next((value for value in usable[1:] if value.casefold() != primary.casefold()), "#F2F5F3")
    return primary, secondary


def rgb(hex_value: str) -> tuple[int, int, int]:
    return tuple(int(hex_value[index:index + 2], 16) for index in (1, 3, 5))


def darken(hex_value: str, factor: float = 0.72) -> tuple[int, int, int]:
    return tuple(round(value * factor) for value in rgb(hex_value))


def draw_thumbnail(source: Path, destination: Path, name: str, palettes: list[dict], width: int) -> None:
    height = width // 2
    primary, secondary = team_colors(name, palettes)
    canvas = Image.new("RGB", (width, height), darken(primary))
    court_box = (
        round(width * 0.08),
        round(height * 0.12),
        round(width * 0.92),
        round(height * 0.88),
    )
    court_width = court_box[2] - court_box[0]
    court_height = court_box[3] - court_box[1]
    with Image.open(source) as opened:
        floor = ImageOps.fit(opened.convert("RGB"), (court_width, court_height), method=Image.Resampling.LANCZOS)
    floor = ImageEnhance.Contrast(floor).enhance(1.04)
    canvas.paste(floor, court_box)

    draw = ImageDraw.Draw(canvas, "RGBA")
    line = (250, 252, 251, 235)
    accent = (*rgb(primary), 205)
    secondary_fill = (*rgb(secondary), 62)
    x0, y0, x1, y1 = court_box
    mid_x = (x0 + x1) // 2
    mid_y = (y0 + y1) // 2
    key_width = round(court_width * 0.18)
    key_height = round(court_height * 0.43)
    key_top = mid_y - key_height // 2
    key_bottom = mid_y + key_height // 2
    stroke = max(1, width // 180)

    draw.rectangle((x0, y0, x1, y1), outline=line, width=stroke)
    draw.line((mid_x, y0, mid_x, y1), fill=line, width=stroke)
    center_radius = round(court_height * 0.14)
    draw.ellipse((mid_x - center_radius, mid_y - center_radius, mid_x + center_radius, mid_y + center_radius), outline=line, width=stroke)
    for side in (-1, 1):
        baseline = x0 if side < 0 else x1
        inner = baseline + key_width if side < 0 else baseline - key_width
        box = (baseline, key_top, inner, key_bottom) if side < 0 else (inner, key_top, baseline, key_bottom)
        draw.rectangle(box, fill=accent, outline=line, width=stroke)
        free_radius = round(key_height * 0.28)
        draw.ellipse((inner - free_radius, mid_y - free_radius, inner + free_radius, mid_y + free_radius), fill=secondary_fill, outline=line, width=stroke)
        arc_depth = round(court_width * 0.27)
        arc_box = (
            baseline - arc_depth if side > 0 else baseline,
            y0 + round(court_height * 0.08),
            baseline if side > 0 else baseline + arc_depth,
            y1 - round(court_height * 0.08),
        )
        draw.arc(arc_box, 90 if side < 0 else 270, 270 if side < 0 else 450, fill=line, width=stroke)

    destination.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(destination, "JPEG", quality=82, optimize=True)


def main() -> None:
    parser = argparse.ArgumentParser(description="Build court-browser thumbnails.")
    parser.add_argument("--index", required=True)
    parser.add_argument("--width", type=int, default=360)
    parser.add_argument("--workers", type=int, default=min(8, os.cpu_count() or 4))
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--palettes", default=str(PROJECT_ROOT / "data" / "team_palettes.json"))
    args = parser.parse_args()

    index_path = Path(args.index)
    data = json.loads(index_path.read_text(encoding="utf-8"))
    palettes = load_palettes(Path(args.palettes))
    asset_root = index_path.parent.parent.parent
    thumbnail_root = index_path.parent / "thumbnails"

    def build(item: dict) -> tuple[str, str | None]:
        source = Path(str(item.get("path", "")))
        if not source.is_absolute():
            source = asset_root / source
        destination = thumbnail_root / f"{source.stem}.jpg"
        if args.force or not destination.exists() or destination.stat().st_mtime_ns < source.stat().st_mtime_ns:
            draw_thumbnail(source, destination, str(item.get("name") or source.stem), palettes, args.width)
        relative = destination.relative_to(asset_root)
        return str(item.get("id", "")), str(relative)

    templates = [item for item in data.get("templates", []) if item.get("path")]
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as executor:
        thumbnail_paths = dict(executor.map(build, templates))
    for item in templates:
        item["thumbnailPath"] = thumbnail_paths.get(str(item.get("id", "")))
    index_path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    print(f"wrote {len(templates)} thumbnails to {thumbnail_root}")


if __name__ == "__main__":
    main()
