from __future__ import annotations

import argparse
from io import BytesIO
import json
from pathlib import Path
import re

from PIL import Image

from export_2k26_court_texture import (
    choose_format,
    make_dds,
    oodle_decompress,
    read_tld_metadata,
    top_mip_bytes,
)


WOOD_FLOOR = re.compile(
    r"^floor_(?P<floor>[a-z0-9]+)_court_wood(?P<wood>\d*)_basecolor\.[0-9a-f]+\.mip0$",
    re.IGNORECASE,
)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build a small local NBA 2K26 court floor template library."
    )
    parser.add_argument("--game-root", required=True)
    parser.add_argument("--extracted-root", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--limit", type=int, default=8)
    parser.add_argument("--save-dds", action="store_true")
    args = parser.parse_args()

    game_root = Path(args.game_root)
    extracted_root = Path(args.extracted_root)
    output_root = Path(args.output)
    image_root = output_root / "images"
    dds_root = output_root / "dds"
    image_root.mkdir(parents=True, exist_ok=True)
    if args.save_dds:
        dds_root.mkdir(parents=True, exist_ok=True)

    candidates = find_candidates(extracted_root)
    if args.limit > 0:
        candidates = candidates[: args.limit]

    templates = []
    for index, mip0_path in enumerate(candidates, start=1):
        tld_path = mip0_path.with_suffix(".tld")
        width, height, chain_bytes = read_tld_metadata(tld_path)
        fourcc, raw_size = choose_format(width, height, chain_bytes, "auto")
        raw_texture = oodle_decompress(game_root, mip0_path.read_bytes(), raw_size)

        display_height = height // 2 if "court_wood" in mip0_path.name.lower() else height
        display_size = top_mip_bytes(width, display_height, 8 if fourcc == "DXT1" else 16)
        display_texture = raw_texture[:display_size]
        dds_data = make_dds(width, display_height, fourcc, display_texture)

        template_id = template_id_for(mip0_path)
        name = friendly_name(mip0_path)
        png_path = image_root / f"{template_id}.png"
        with Image.open(BytesIO(dds_data)) as image:
            image.convert("RGBA").save(png_path)

        dds_path = None
        if args.save_dds:
            dds_path = dds_root / f"{template_id}.dds"
            dds_path.write_bytes(dds_data)

        templates.append(
            {
                "id": template_id,
                "name": name,
                "path": relative_to_asset_root(png_path),
                "texturePath": relative_to_asset_root(dds_path) if dds_path else None,
                "sourceMip0": str(mip0_path),
                "sourceTld": str(tld_path),
                "width": width,
                "height": display_height,
                "format": fourcc,
            }
        )
        print(f"{index}/{len(candidates)} {name}")

    index_path = output_root / "nba2k26_floor_templates.json"
    index_path.write_text(
        json.dumps(
            {
                "name": "NBA 2K26 Floor Templates",
                "templates": templates,
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(f"wrote {index_path} ({len(templates)} templates)")


def find_candidates(extracted_root: Path) -> list[Path]:
    shared_root = extracted_root / "shared"
    candidates = [
        path
        for path in shared_root.rglob("*.mip0")
        if WOOD_FLOOR.match(path.name) and path.with_suffix(".tld").exists()
    ]
    unique: dict[str, Path] = {}
    for path in sorted(candidates, key=candidate_sort_key):
        unique.setdefault(friendly_name(path), path)
    return list(unique.values())


def candidate_sort_key(path: Path) -> tuple[int, int, str]:
    match = WOOD_FLOOR.match(path.name)
    if not match:
        return (9999, 99, path.name)
    floor = match.group("floor")
    wood = match.group("wood") or "1"
    number = int(floor) if floor.isdigit() else 9000
    return (number, int(wood), path.name)


def template_id_for(path: Path) -> str:
    name = path.name.rsplit(".", 2)[0]
    safe = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    return f"nba2k26-{safe}"


def friendly_name(path: Path) -> str:
    name = path.name.rsplit(".", 2)[0]
    name = re.sub(r"^floor_", "", name, flags=re.IGNORECASE)
    name = re.sub(r"_basecolor$", "", name, flags=re.IGNORECASE)
    words = name.replace("_", " ").split()
    titled = []
    for word in words:
        if word.isdigit():
            titled.append(f"Floor {int(word):03d}")
        elif re.fullmatch(r"wood\d+", word, flags=re.IGNORECASE):
            titled.append(word.capitalize())
        else:
            titled.append(word.title())
    return " ".join(titled)


def relative_to_asset_root(path: Path | None) -> str | None:
    if path is None:
        return None
    asset_root = Path.home() / "OneDrive" / "Documents" / "2kcourtmodder"
    try:
        return str(path.relative_to(asset_root))
    except ValueError:
        return str(path)


if __name__ == "__main__":
    main()
