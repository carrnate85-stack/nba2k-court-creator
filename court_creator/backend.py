from __future__ import annotations

from dataclasses import asdict
import argparse
import json
from pathlib import Path
import re
import shutil
import struct
import sys

from .court_template import (
    CourtLayer,
    create_court_preview_png,
    create_visible_court_preview_png,
    parse_court_psd_layers,
    sample_template_layer_color,
)


PROJECT_ROOT = Path(__file__).resolve().parent.parent
OUTPUT_DIR = PROJECT_ROOT / "outputs"
PREVIEW_CACHE = OUTPUT_DIR / "court_template_preview.png"
TEAM_PALETTES_PATH = PROJECT_ROOT / "data" / "team_palettes.json"
PRESETS_PATH = PROJECT_ROOT / "data" / "court_presets.json"
LOCAL_ASSET_ROOT = PROJECT_ROOT
ONEDRIVE_ASSET_ROOT = Path.home() / "OneDrive" / "Documents" / "2kcourtmodder"
CUSTOM_FLOORS_DIR = LOCAL_ASSET_ROOT / "custom_floors"
CUSTOM_FLOORS_META = CUSTOM_FLOORS_DIR / "custom_floors.json"
FLOOR_TEMPLATE_META_GLOB = "court_floor_templates/**/nba2k26_floor_templates.json"
COLLEGE_FLOOR_KEYS = {
    "arizonawildcats",
    "baylorbears",
    "dukebluedevils",
    "floridagators",
    "houstoncougars",
    "kansasjayhawks",
    "kentuckywildcats",
    "louisvillecardinals",
    "michiganstatespartans",
    "michiganwolverines",
    "ohiostatebuckeyes",
    "purdueboilermakers",
    "texaslonghorns",
    "uclabruins",
    "uconnhuskies",
    "unctarheels",
}
HISTORIC_NBA_FLOOR_KEYS = {
    "bobcats2011",
    "bucks2015",
    "bulls2016",
    "cavaliers2011",
    "cavaliers2016",
    "clippers2015",
    "clippers2022",
    "grizzlies2016",
    "jazz2016",
    "kings2016",
    "knicks2016",
    "nets2012",
    "nuggets2016",
    "pacers2005",
    "pistons2016",
    "raptors2016",
    "rockets2003",
    "rockets2016",
    "spurs1998",
    "suns2016",
    "thunder2016",
    "timberwolves2011",
    "wizards2014",
}
INTERNATIONAL_FLOOR_KEYS = {"barcelona", "madrid", "paris"}
MODE_FLOOR_KEYS = {
    "aau",
    "clutchtime",
    "gleagueignite",
    "matchmaking",
    "myteam",
    "scrimmage",
    "summerleaguegeneric",
}
PROJECT_COURT_TEMPLATE_PSD = (
    LOCAL_ASSET_ROOT / "templates" / "NBA 2K25 Court Template By RedLite2K.psd"
)
ONEDRIVE_COURT_TEMPLATE_PSD = (
    ONEDRIVE_ASSET_ROOT / "templates" / "NBA 2K25 Court Template By RedLite2K.psd"
)
DOWNLOAD_COURT_TEMPLATE_PSD = (
    Path.home()
    / "Downloads"
    / "NBA 2K26 -  Court Template - Jayderoza"
    / "NBA 2K26 -  Court Template - Jayderoza"
    / "NBA 2K25 Court Template By RedLite2K.psd"
)


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    load_parser = subparsers.add_parser("load")
    load_parser.add_argument("--template")
    render_parser = subparsers.add_parser("render")
    render_parser.add_argument("--request", required=True)
    sample_parser = subparsers.add_parser("sample-color")
    sample_parser.add_argument("--layer-id", required=True)
    add_floor_parser = subparsers.add_parser("add-floor")
    add_floor_parser.add_argument("--source", required=True)
    args = parser.parse_args()

    try:
        if args.command == "load":
            write_response(load_state(Path(args.template) if args.template else None))
        elif args.command == "render":
            write_response(render_preview(Path(args.request)))
        elif args.command == "sample-color":
            write_response(sample_color(args.layer_id))
        elif args.command == "add-floor":
            write_response(add_custom_floor(Path(args.source)))
    except Exception as exc:
        write_response({"ok": False, "error": str(exc)})
        sys.exit(1)


def load_state(template_path: Path | None = None) -> dict:
    template_path = template_path or default_template_path()
    document = parse_court_psd_layers(template_path)
    hidden_builtin_floor_ids = built_in_court_floor_layer_ids(document.layers)
    visible_layers = [
        layer for layer in document.layers if layer.id not in hidden_builtin_floor_ids
    ]
    ensure_preview(template_path)
    custom_floor_layers, custom_floor_images = load_custom_floor_layers(document)
    template_floor_layers, template_floor_images = load_floor_template_layers(
        document, start_index=len(custom_floor_layers)
    )
    return {
        "ok": True,
        "projectRoot": str(PROJECT_ROOT),
        "templatePath": str(template_path),
        "previewPath": str(PREVIEW_CACHE),
        "document": {
            "path": str(template_path),
            "width": document.width,
            "height": document.height,
            "layers": [asdict(layer) for layer in visible_layers],
        },
        "visibility": {
            **{layer.id: layer.visible for layer in visible_layers},
            **{layer_id: False for layer_id in hidden_builtin_floor_ids},
        },
        "customFloorLayers": [
            asdict(layer) for layer in [*custom_floor_layers, *template_floor_layers]
        ],
        "customFloorImages": [*custom_floor_images, *template_floor_images],
        "teamPalettes": load_team_palettes(),
        "presets": load_presets(),
    }


def render_preview(request_path: Path) -> dict:
    request = json.loads(request_path.read_text(encoding="utf-8"))
    template_path = Path(request.get("templatePath") or default_template_path())
    document = parse_court_psd_layers(template_path)
    output_path = Path(request.get("outputPath") or PREVIEW_CACHE)
    visibility = {str(key): bool(value) for key, value in request.get("visibility", {}).items()}
    for layer_id in built_in_court_floor_layer_ids(document.layers):
        visibility[layer_id] = False
    color_overrides = normalize_color_overrides(request.get("colorOverrides", {}))
    custom_floor_images = []
    for item in request.get("customFloorImages", []):
        if not item.get("visible"):
            continue
        image = dict(item)
        image["path"] = str(resolve_asset_path(str(item.get("path", ""))))
        custom_floor_images.append(image)
    logo_images = []
    for item in request.get("logoImages", []):
        if not item.get("visible"):
            continue
        image = dict(item)
        image["path"] = str(resolve_asset_path(str(item.get("path", ""))))
        logo_images.append(image)
    create_visible_court_preview_png(
        template_path,
        document,
        visibility,
        output_path,
        color_overrides=color_overrides,
        custom_floor_images=custom_floor_images,
        logo_images=logo_images,
    )
    return {"ok": True, "previewPath": str(output_path)}


def sample_color(layer_id: str) -> dict:
    template_path = default_template_path()
    document = parse_court_psd_layers(template_path)
    color = sample_template_layer_color(template_path, document, layer_id)
    return {"ok": True, "color": list(color) if color else None}


def add_custom_floor(source: Path) -> dict:
    template_path = default_template_path()
    document = parse_court_psd_layers(template_path)
    custom_floor_layers, custom_floor_images = load_custom_floor_layers(document)
    floor_group = court_floor_group(document.layers)
    floor_bbox = court_floor_bbox(document.layers, floor_group)
    if floor_group is None or floor_bbox is None:
        raise RuntimeError("Could not find the Court Floors group.")

    source = Path(source)
    if not source.exists():
        raise RuntimeError("Custom floor image was not found.")
    CUSTOM_FLOORS_DIR.mkdir(parents=True, exist_ok=True)
    stem = safe_stem(source.stem)
    suffix = source.suffix.lower() or ".png"
    destination = CUSTOM_FLOORS_DIR / f"{stem}{suffix}"
    counter = 2
    while destination.exists():
        destination = CUSTOM_FLOORS_DIR / f"{stem}-{counter}{suffix}"
        counter += 1
    shutil.copy2(source, destination)
    layer = CourtLayer(
        id=f"custom_floor_{destination.stem}",
        name=destination.stem.replace("-", " ").replace("_", " ").title(),
        kind="layer",
        parent_id=floor_group.id,
        psd_index=10000 + len(custom_floor_layers),
        depth=1,
        visible=False,
        opacity=255,
        blend_mode="norm",
        bbox=floor_bbox,
    )
    image = {
        "id": layer.id,
        "name": layer.name,
        "path": str(destination.relative_to(PROJECT_ROOT)),
        "bbox": floor_bbox,
    }
    custom_floor_images.append(image)
    save_custom_floor_metadata(custom_floor_images)
    return {"ok": True, "layer": asdict(layer), "image": image}


def default_template_path() -> Path:
    for path in (
        PROJECT_COURT_TEMPLATE_PSD,
        ONEDRIVE_COURT_TEMPLATE_PSD,
        DOWNLOAD_COURT_TEMPLATE_PSD,
    ):
        if path.exists():
            return path
    raise RuntimeError("Could not find the court PSD template.")


def ensure_preview(template_path: Path) -> None:
    if PREVIEW_CACHE.exists():
        return
    create_court_preview_png(template_path, PREVIEW_CACHE)


def load_team_palettes() -> list:
    if not TEAM_PALETTES_PATH.exists():
        return []
    data = json.loads(TEAM_PALETTES_PATH.read_text(encoding="utf-8"))
    return data.get("palettes", data if isinstance(data, list) else [])


def load_presets() -> list:
    if not PRESETS_PATH.exists():
        return [None, None, None, None, None]
    data = json.loads(PRESETS_PATH.read_text(encoding="utf-8"))
    presets = data.get("presets", []) if isinstance(data, dict) else []
    while len(presets) < 5:
        presets.append(None)
    return presets[:5]


def load_custom_floor_layers(document) -> tuple[list[CourtLayer], list[dict]]:
    layers: list[CourtLayer] = []
    images: list[dict] = []
    floor_group = court_floor_group(document.layers)
    fallback_bbox = court_floor_bbox(document.layers, floor_group)
    if floor_group is None or fallback_bbox is None or not CUSTOM_FLOORS_META.exists():
        return layers, images
    data = json.loads(CUSTOM_FLOORS_META.read_text(encoding="utf-8"))
    for index, item in enumerate(data.get("floors", [])):
        path = resolve_asset_path(str(item.get("path", "")))
        if not path.exists():
            continue
        bbox = tuple(item.get("bbox", fallback_bbox))
        if len(bbox) != 4:
            bbox = fallback_bbox
        layer = CourtLayer(
            id=str(item.get("id") or f"custom_floor_{path.stem}"),
            name=str(item.get("name") or path.stem),
            kind="layer",
            parent_id=floor_group.id,
            psd_index=10000 + index,
            depth=1,
            visible=False,
            opacity=255,
            blend_mode="norm",
            bbox=tuple(int(value) for value in bbox),
        )
        layers.append(layer)
        images.append(
            {
                "id": layer.id,
                "name": layer.name,
                "path": str(path.relative_to(PROJECT_ROOT)) if path.is_relative_to(PROJECT_ROOT) else str(path),
                "bbox": layer.bbox,
            }
        )
    return layers, images


def load_floor_template_layers(
    document,
    *,
    start_index: int = 0,
) -> tuple[list[CourtLayer], list[dict]]:
    layers: list[CourtLayer] = []
    images: list[dict] = []
    floor_group = court_floor_group(document.layers)
    fallback_bbox = court_floor_bbox(document.layers, floor_group)
    if floor_group is None or fallback_bbox is None:
        return layers, images

    template_index = 0
    category_groups: dict[str, CourtLayer] = {}
    for meta_path in ONEDRIVE_ASSET_ROOT.glob(FLOOR_TEMPLATE_META_GLOB):
        data = json.loads(meta_path.read_text(encoding="utf-8"))
        for item in data.get("templates", []):
            path = resolve_asset_path(str(item.get("path", "")))
            if not path.exists():
                continue
            category = str(item.get("category") or category_for_floor_template(item))
            default_visible = template_index == 0
            if category not in category_groups:
                group = CourtLayer(
                    id=f"floor_template_category_{safe_stem(category)}",
                    name=category,
                    kind="group",
                    parent_id=floor_group.id,
                    psd_index=10900 + category_rank(category),
                    depth=1,
                    visible=default_visible,
                    opacity=255,
                    blend_mode="pass",
                    bbox=fallback_bbox,
                )
                category_groups[category] = group
                layers.append(group)
            layer_id = str(item.get("id") or f"floor_template_{template_index}")
            layer = CourtLayer(
                id=layer_id,
                name=str(item.get("name") or path.stem),
                kind="layer",
                parent_id=category_groups[category].id,
                psd_index=11000 + category_rank(category) * 1000 + start_index + template_index,
                depth=2,
                visible=default_visible,
                opacity=255,
                blend_mode="norm",
                bbox=fallback_bbox,
            )
            layers.append(layer)
            images.append(
                {
                    "id": layer.id,
                    "name": layer.name,
                    "path": str(path.relative_to(ONEDRIVE_ASSET_ROOT))
                    if path.is_relative_to(ONEDRIVE_ASSET_ROOT)
                    else str(path),
                    "bbox": layer.bbox,
                    "isTemplate": True,
                    "category": category,
                    "sourceMip0": item.get("sourceMip0"),
                    "sourceTld": item.get("sourceTld"),
                }
            )
            template_index += 1
    return layers, images


def category_for_floor_template(item: dict) -> str:
    token = " ".join(
        str(value or "")
        for value in (item.get("id"), item.get("name"), item.get("sourceMip0"))
    ).casefold()
    if "_city_" in token or " city " in token:
        return "City Edition"
    if "_statement_" in token or " statement " in token:
        return "Statement Edition"
    if "_classic_" in token or " classic " in token:
        return "Classic Edition"
    if "wnba" in token:
        return "WNBA"
    if "allstar" in token or "_event_" in token or " event " in token:
        return "All-Star & Events"
    if any(name in token for name in COLLEGE_FLOOR_KEYS):
        return "College"
    if any(name in token for name in HISTORIC_NBA_FLOOR_KEYS):
        return "Historic NBA"
    if any(name in token for name in INTERNATIONAL_FLOOR_KEYS):
        return "International"
    if any(name in token for name in MODE_FLOOR_KEYS):
        return "Modes & Generic"
    if re.search(r"floor[_ -]\d+[_ -]court[_ -]wood", token):
        return "Numbered Courts"
    return "Special"


def category_rank(category: str) -> int:
    order = {
        "Numbered Courts": 0,
        "City Edition": 10,
        "Statement Edition": 20,
        "Classic Edition": 30,
        "Historic NBA": 40,
        "College": 50,
        "WNBA": 60,
        "All-Star & Events": 70,
        "International": 80,
        "Modes & Generic": 90,
        "Special": 100,
    }
    return order.get(category, 999)


def save_custom_floor_metadata(images: list[dict]) -> None:
    CUSTOM_FLOORS_DIR.mkdir(parents=True, exist_ok=True)
    CUSTOM_FLOORS_META.write_text(json.dumps({"floors": images}, indent=2), encoding="utf-8")


def court_floor_group(layers) -> CourtLayer | None:
    return next(
        (
            layer
            for layer in layers
            if layer.kind == "group"
            and normalize_name(layer.name)
            in {"court floors", "court floor", "floor options", "floors"}
        ),
        None,
    )


def court_floor_bbox(layers, floor_group: CourtLayer | None) -> tuple[int, int, int, int] | None:
    if floor_group is None:
        return None
    for layer in layers:
        if layer.parent_id == floor_group.id and layer.kind == "layer" and layer.bbox[2] > 0 and layer.bbox[3] > 0:
            return layer.bbox
    return None


def built_in_court_floor_layer_ids(layers) -> set[str]:
    floor_group = court_floor_group(layers)
    if floor_group is None:
        return set()
    return {
        layer.id
        for layer in layers
        if layer.parent_id == floor_group.id
        and layer.kind == "layer"
        and normalize_name(layer.name).startswith("full floor")
    }


def normalize_color_overrides(color_overrides: object) -> dict[str, tuple[int, int, int]]:
    if not isinstance(color_overrides, dict):
        return {}
    normalized: dict[str, tuple[int, int, int]] = {}
    for layer_id, value in color_overrides.items():
        if not isinstance(value, list | tuple) or len(value) < 3:
            continue
        normalized[str(layer_id)] = tuple(max(0, min(255, int(channel))) for channel in value[:3])
    return normalized


def resolve_asset_path(value: str) -> Path:
    path = Path(value)
    if path.is_absolute():
        return path
    local_path = PROJECT_ROOT / path
    if local_path.exists():
        return local_path
    onedrive_path = ONEDRIVE_ASSET_ROOT / path
    if onedrive_path.exists():
        return onedrive_path
    return local_path


def safe_stem(value: str) -> str:
    safe = "".join(character if character.isalnum() or character in "-_" else "-" for character in value.strip())
    safe = safe.strip("-_").lower()
    return safe or "custom-floor"


def normalize_name(name: str) -> str:
    return " ".join(name.casefold().replace("_", " ").replace("-", " ").split())


def write_response(data: dict) -> None:
    print(json.dumps(data))


if __name__ == "__main__":
    main()
