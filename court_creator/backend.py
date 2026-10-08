from __future__ import annotations

from dataclasses import asdict
import argparse
import json
from pathlib import Path
import re
import struct
import sys
import tempfile

from .export_io import ensure_new_export
from .json_io import MAX_METADATA_BYTES, read_document, read_request
from .custom_floor_store import CustomFloorStore

from .court_template import (
    CourtLayer,
    CourtLayerDocument,
    create_court_preview_png,
    create_visible_court_preview_png,
    parse_court_psd_layers,
    sample_template_layer_color,
)
from .court_import import (
    build_iff,
    cached_2k27_base,
    inspect_iff,
    open_iff,
    package_png_into_iff,
    prepare_2k27_base,
    read_iff_scene,
    render_texture,
)


PROJECT_ROOT = Path(__file__).resolve().parent.parent
OUTPUT_DIR = PROJECT_ROOT / "outputs"
PREVIEW_CACHE = OUTPUT_DIR / "court_template_preview.png"
TEAM_PALETTES_PATH = PROJECT_ROOT / "data" / "team_palettes.json"
PRESETS_PATH = PROJECT_ROOT / "data" / "court_presets.json"
LOCAL_ASSET_ROOT = PROJECT_ROOT
ASSET_ROOT = PROJECT_ROOT / "assets"
LEGACY_ASSET_ROOT = Path.home() / "OneDrive" / "Documents" / "2kcourtmodder"
LEGACY_PROJECT_ROOT = Path.home() / "NBA 2K Court Creator"
LEGACY_ONEDRIVE_PROJECT_ROOT = Path.home() / "OneDrive" / "Documents" / "NBA 2K Court Creator"
CUSTOM_FLOORS_DIR = LOCAL_ASSET_ROOT / "custom_floors"
CUSTOM_FLOORS_META = CUSTOM_FLOORS_DIR / "custom_floors.json"
FLOOR_TEMPLATE_META_GLOB = "court_floor_templates/**/nba2k*_floor_templates.json"
IMPORT_PREVIEW = OUTPUT_DIR / "court_import_preview.png"
NBA2K27_EXPORT_BASE = PROJECT_ROOT / "data" / "generated" / "nba2k27-full-court-base.iff"
DEFAULT_IMPORT_BACKGROUND = ASSET_ROOT / "court_floor_templates" / "nba2k27" / "images" / "nba2k27-floor-000-court-wood1-basecolor.png"
BROKEN_FLOOR_TEMPLATE_IDS = {
    "nba2k26-floor-300-court-wood1-basecolor",
    "nba2k27-floor-300-court-wood1-basecolor",
}
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
NBA_ARENA_IDS = {
    "000",
    "001",
    "002",
    "003",
    "004",
    "005",
    "006",
    "008",
    "009",
    "010",
    "011",
    "012",
    "013",
    "014",
    "015",
    "016",
    "017",
    "018",
    "019",
    "020",
    "021",
    "022",
    "023",
    "024",
    "025",
    "026",
    "027",
    "028",
    "029",
    "031",
}
WNBA_ARENA_IDS = {
    "300",
    "301",
    "302",
    "303",
    "304",
    "305",
    "306",
    "307",
    "308",
    "309",
    "310",
    "311",
    "315",
    "316",
    "317",
}
HISTORIC_ARENA_IDS = {
    "551",
    "552",
    "553",
    "554",
    "555",
    "556",
    "557",
    "558",
    "559",
    "560",
    "562",
    "564",
    "570",
    "571",
    "572",
    "574",
    "576",
    "583",
    "586",
    "588",
    "589",
    "590",
    "591",
    "592",
    "593",
    "594",
    "595",
    "620",
    "621",
    "622",
    "623",
    "624",
    "625",
    "626",
    "627",
    "628",
    "629",
    "630",
    "631",
    "632",
    "633",
    "634",
    "635",
    "636",
    "637",
    "638",
    "639",
    "641",
    "642",
    "644",
    "645",
    "646",
    "647",
    "648",
    "649",
    "924",
}
EVENT_ARENA_IDS = {
    "700",
    "701",
    "728",
    "729",
    "800",
    "852",
    "853",
    "854",
    "855",
    "856",
    "857",
    "858",
    "859",
    "860",
    "861",
    "906",
}
PROJECT_COURT_TEMPLATE_PSD = (
    LOCAL_ASSET_ROOT / "templates" / "NBA 2K25 Court Template By RedLite2K.psd"
)
ASSET_COURT_TEMPLATE_PSD = (
    ASSET_ROOT / "templates" / "NBA 2K25 Court Template By RedLite2K.psd"
)
LEGACY_COURT_TEMPLATE_PSD = (
    LEGACY_ASSET_ROOT / "templates" / "NBA 2K25 Court Template By RedLite2K.psd"
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
    requested_template = resolve_asset_path(str(template_path)) if template_path else None
    template_path = (
        requested_template
        if requested_template is not None and requested_template.is_file()
        else default_template_path()
    )
    document = parse_court_psd_layers(template_path)
    hidden_builtin_floor_ids = built_in_court_floor_layer_ids(document.layers)
    visible_layers = [
        layer for layer in document.layers if layer.id not in hidden_builtin_floor_ids
    ]
    ensure_preview(template_path)
    custom_floor_layers, custom_floor_images = load_custom_floor_layers(document)
    template_floor_layers, template_floor_images, floor_library_name = load_floor_template_layers(
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
        "floorLibraryName": floor_library_name,
        "floorLibraryCount": len(template_floor_images),
        "templateFallback": requested_template is not None and requested_template != template_path,
    }


def stock_document(geometry: dict) -> CourtLayerDocument:
    bounds = tuple(geometry["gameUv"]["hardwoodBounds"])
    def layer(identifier, name, parent=None, kind="layer", visible=True, index=0):
        return CourtLayer(identifier, name, kind, parent, index, 1 if parent else 0,
                          visible, 255, "norm", bounds)
    layers = [layer("stock-floors", "Court Floors", kind="group"),
              layer("stock-floor-bounds", "Hardwood bounds", "stock-floors", visible=False),
              layer("stock-paints", "Paint Colors", kind="group"),
              layer("stock-lines", "Lines", kind="group"),
              layer("stock-outside", "Outside Color")]
    for group, entries in (("stock-paints", geometry["paints"]), ("stock-lines", geometry["layers"])):
        layers.extend(layer(item["id"], item["name"], group, visible=item["visible"], index=index)
                      for index, item in enumerate(entries))
    return CourtLayerDocument("game-uv", 8192, 4096, 4, 8, 3, tuple(layers))


def load_stock_state() -> dict:
    from .experimental_lines import geometry_revision, load_geometry, prepare_geometry
    geometry = load_geometry(PROJECT_ROOT) or prepare_geometry(PROJECT_ROOT)
    document = stock_document(geometry)
    custom_layers, custom_images = load_custom_floor_layers(document)
    floor_layers, floor_images, library = load_floor_template_layers(document, start_index=len(custom_layers))
    visible_layers = [item for item in document.layers if item.id != "stock-floor-bounds"]
    colors = {item["id"]: item["color"] for item in [*geometry["paints"], *geometry["layers"]]}
    colors["stock-outside"] = "#19583F"
    images = [dict(item, path=str(resolve_asset_path(item["path"])),
                   bbox=geometry["gameUv"]["hardwoodBounds"]) for item in [*custom_images, *floor_images]]
    return {"ok": True, "buildMode": "game-uv", "geometry": geometry, "geometryRevision": geometry_revision(geometry),
            "projectRoot": str(PROJECT_ROOT), "templatePath": "", "previewPath": "",
            "document": {"path": "game-uv", "width": 8192, "height": 4096,
                         "layers": [dict(asdict(item), color=colors.get(item.id)) for item in visible_layers]},
            "visibility": {item.id: item.visible for item in [*visible_layers, *custom_layers, *floor_layers]},
            "customFloorLayers": [asdict(item) for item in [*custom_layers, *floor_layers]],
            "customFloorImages": images, "teamPalettes": load_team_palettes(), "presets": [],
            "floorLibraryName": library, "floorLibraryCount": len(floor_images)}


def _export_sources(request: dict) -> tuple[Path, ...]:
    from .experimental_lines import geometry_path
    values = [request.get("_projectPath"), request.get("templatePath"), (request.get("floor") or {}).get("path"),
              (request.get("twoPointFloor") or {}).get("path")]
    values.extend(item.get("path") for key in ("logoImages", "customFloorImages") for item in request.get(key, []))
    return (NBA2K27_EXPORT_BASE, PROJECT_ROOT / "tools" / "texconv.exe", geometry_path(PROJECT_ROOT),
            *(resolve_asset_path(str(value)) for value in values if value))


def render_preview(request_path: Path, *, geometry: dict | None = None) -> dict:
    request = request_path if isinstance(request_path, dict) else read_request(request_path)
    if request.get("experimental") or request.get("buildMode") == "game-uv":
        from .experimental_lines import render_experimental, request_geometry
        geometry = request_geometry(PROJECT_ROOT, request) if geometry is None else geometry
        output_path = Path(request.get("outputPath") or OUTPUT_DIR / "experimental_preview.png")
        protected_sources = _export_sources(request)
        ensure_new_export(output_path, *protected_sources)
        floor = dict(request.get("floor") or {})
        floor_path = resolve_asset_path(str(floor.get("path", "")))
        if not floor_path.is_file() and floor.get("id"):
            resolved = next((item for item in load_stock_state()["customFloorImages"] if item["id"] == floor["id"]), floor)
            floor = {**resolved, **({"sourceRevision": floor["sourceRevision"]} if "sourceRevision" in floor else {})}
            floor_path = resolve_asset_path(str(floor.get("path", "")))
        floor["path"] = str(floor_path)
        two_point_floor = request.get("twoPointFloor")
        if two_point_floor is not None:
            if not isinstance(two_point_floor, dict): raise ValueError("Invalid two-point hardwood selection.")
            two_point_floor = dict(two_point_floor, path=str(resolve_asset_path(str(two_point_floor.get("path", "")))))
        logos = [dict(item, path=str(resolve_asset_path(str(item.get("path", "")))))
                 for item in request.get("logoImages", [])]
        render_experimental(PROJECT_ROOT, {**request, "floor": floor, "twoPointFloor": two_point_floor, "logoImages": logos}, output_path,
                            preview=not request.get("exportFullResolution"), geometry=geometry, protected_sources=protected_sources)
        return {"ok": True, "previewPath": str(output_path)}
    template_path = resolve_asset_path(str(request.get("templatePath") or default_template_path()))
    document = parse_court_psd_layers(template_path)
    output_path = Path(request.get("outputPath") or PREVIEW_CACHE)
    protected_sources = (template_path, *_export_sources(request))
    ensure_new_export(output_path, *protected_sources)
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
        max_size=None if request.get("exportFullResolution") else (2048, 1024),
        protected_sources=protected_sources,
    )
    return {"ok": True, "previewPath": str(output_path)}


def experimental_state(prepare: bool = False) -> dict:
    from .experimental_lines import geometry_revision, prepare_geometry
    if prepare:
        prepare_geometry(PROJECT_ROOT)
    state = load_stock_state()
    geometry = state["geometry"]
    floors = [dict(item, path=str(resolve_asset_path(item["path"])))
              for item in state["customFloorImages"] if item.get("isTemplate")]
    return {"ok": True, "geometry": geometry, "geometryRevision": geometry_revision(geometry), "floors": floors}


def prepare_logo_editor(request_path: Path) -> dict:
    from .experimental_lines import editor_guides, load_geometry, prepare_geometry
    from tools.court_logo_web import clean_items
    request = read_request(request_path)
    native = request.get("buildMode") == "game-uv"
    document = stock_document(load_geometry(PROJECT_ROOT) or prepare_geometry(PROJECT_ROOT)) if native else parse_court_psd_layers(resolve_asset_path(str(request.get("templatePath") or default_template_path())))
    background = Path(request["backgroundOutput"])
    render_preview({**request, "logoImages": [], "outputPath": str(background)})
    guides = None
    guide_status = "Stock guides unavailable"
    try:
        geometry = load_geometry(PROJECT_ROOT) or prepare_geometry(PROJECT_ROOT)
        guides = editor_guides(geometry, "game-uv" if native else "template", None if native else request.get("guideBounds"))
        guide_status = guides["alignment"]
    except (ValueError, RuntimeError, FileNotFoundError) as error:
        guide_status = f"Stock guides unavailable: {error}"
    items = clean_items(request.get("logoImages", []))
    for item in items:
        item["path"] = str(resolve_asset_path(item["path"]))
    return {"projectRoot": str(PROJECT_ROOT), "width": document.width, "height": document.height,
            "backgroundPath": str(background), "items": items, "selectedId": request.get("selectedId"),
            "guides": guides, "guideStatus": guide_status,
            "allowedLogoPaths": sorted({item["path"] for item in items})}


def sample_color(layer_id: str) -> dict:
    template_path = default_template_path()
    document = parse_court_psd_layers(template_path)
    color = sample_template_layer_color(template_path, document, layer_id)
    return {"ok": True, "color": list(color) if color else None}


def add_custom_floor(source: Path, *, native: bool = False) -> dict:
    store = CustomFloorStore(PROJECT_ROOT, CUSTOM_FLOORS_DIR, CUSTOM_FLOORS_META)
    store.snapshot()
    with store.locked():
        metadata, revision = store.snapshot()
        return _add_custom_floor(source, native, store, metadata, revision)


def _add_custom_floor(source, native, store, metadata, revision):
    if native:
        from .experimental_lines import load_geometry, prepare_geometry
        document = stock_document(load_geometry(PROJECT_ROOT) or prepare_geometry(PROJECT_ROOT))
    else:
        document = parse_court_psd_layers(default_template_path())
    custom_floor_layers, _custom_floor_images = load_custom_floor_layers(document)
    floor_group = court_floor_group(document.layers)
    floor_bbox = court_floor_bbox(document.layers, floor_group)
    if floor_group is None or floor_bbox is None:
        raise RuntimeError("Could not find the Court Floors group.")

    source = Path(source)
    if not source.exists():
        raise RuntimeError("Custom floor image was not found.")
    stem = safe_stem(source.stem)
    suffix = source.suffix.lower() or ".png"
    copied = store.copy_image(source, stem, suffix, reserved=store.entry_paths(metadata))
    destination = copied.path
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
        "previewPath": str(destination),
        "bbox": floor_bbox,
    }
    try:
        save_custom_floor_metadata([*metadata.get("floors", []), image], metadata=metadata, store=store, expected_revision=revision, sources=(source, destination))
    except Exception:
        try:
            if not store.discard(copied):
                print("The uncommitted custom floor was changed or referenced externally and was retained.", file=sys.stderr)
        except (OSError, ValueError) as cleanup_error:
            print(f"Could not remove the uncommitted custom floor: {cleanup_error}", file=sys.stderr)
        raise
    if native:
        image = dict(image, path=str(destination))
    return {"ok": True, "layer": asdict(layer), "image": image}


def inspect_import_iff(source: Path, *, target: bool = False, selected: str | None = None) -> dict:
    return {"ok": True, **inspect_iff(source, target=target, selected=selected)}


def import_base_status() -> dict:
    cached = cached_2k27_base(NBA2K27_EXPORT_BASE)
    return {"ok": True, "prepared": bool(cached), "base": cached}


def prepare_import_base(game_root: Path | None = None) -> dict:
    base = prepare_2k27_base(NBA2K27_EXPORT_BASE, game_root)
    return {"ok": True, "prepared": True, "base": base}


def _ready_import_base() -> dict:
    return prepare_2k27_base(NBA2K27_EXPORT_BASE)


def _import_arguments(request_path: Path) -> tuple[dict, Path, str, list[int], Path]:
    request = read_request(request_path)
    if "sourceRevision" in request and (not isinstance(request["sourceRevision"], str) or re.fullmatch(r"[0-9a-f]{64}", request["sourceRevision"]) is None):
        raise ValueError("Invalid source court revision.")
    if "backgroundRevision" in request:
        from .asset_io import asset_revision
        asset_revision({"sourceRevision": request["backgroundRevision"]})
    source = Path(request["sourcePath"])
    texture = str(request["textureName"])
    bounds = [int(value) for value in request["bounds"]]
    if len(bounds) != 4:
        raise ValueError("Four court edges are required.")
    background = resolve_asset_path(str(request.get("backgroundPath") or DEFAULT_IMPORT_BACKGROUND))
    if not background.is_file():
        raise FileNotFoundError("The selected 2K27 hardwood texture is missing.")
    if request.get("outputPath"):
        from .experimental_lines import geometry_path
        ensure_new_export(Path(request["outputPath"]), source, background, Path(request_path), NBA2K27_EXPORT_BASE,
                          geometry_path(PROJECT_ROOT), PROJECT_ROOT / "tools" / "texconv.exe")
    return request, source, texture, bounds, background


def _import_geometry(request: dict) -> dict | None:
    from .experimental_lines import load_geometry, request_geometry
    return request_geometry(PROJECT_ROOT, request) if "geometryRevision" in request else load_geometry(PROJECT_ROOT)


def _import_export_sources(request_path: Path) -> tuple[Path, ...]:
    from .experimental_lines import geometry_path
    return (Path(request_path), NBA2K27_EXPORT_BASE, geometry_path(PROJECT_ROOT), PROJECT_ROOT / "tools" / "texconv.exe")


def preview_import(request_path: Path) -> dict:
    request, source, texture, bounds, background = _import_arguments(request_path)
    geometry = _import_geometry(request)
    output = Path(request.get("outputPath") or IMPORT_PREVIEW)
    receipt = {}
    render_texture(source, texture, bounds, background, output, preview=True, geometry=geometry,
                   protected_sources=_import_export_sources(request_path), source_revision=request.get("sourceRevision"),
                   background_revision=request.get("backgroundRevision"), publication_receipt=receipt)
    return {"ok": True, "previewPath": str(output), "previewReceipt": receipt}


def export_import_png(request_path: Path) -> dict:
    request, source, texture, bounds, background = _import_arguments(request_path)
    geometry = _import_geometry(request)
    output = render_texture(source, texture, bounds, background, Path(request["outputPath"]), geometry=geometry,
                            protected_sources=_import_export_sources(request_path), source_revision=request.get("sourceRevision"),
                            background_revision=request.get("backgroundRevision"))
    return {"ok": True, "outputPath": str(output)}


def export_import_iff(request_path: Path) -> dict:
    request, source, texture, bounds, background = _import_arguments(request_path)
    geometry = _import_geometry(request)
    texconv = PROJECT_ROOT / "tools" / "texconv.exe"
    base = _ready_import_base()
    from .experimental_lines import native_export_scene
    with open_iff(NBA2K27_EXPORT_BASE) as archive:
        scene_override = native_export_scene(read_iff_scene(archive))
    output = build_iff(
        source,
        texture,
        bounds,
        background,
        NBA2K27_EXPORT_BASE,
        base["selected"],
        Path(request["outputPath"]),
        texconv,
        scene_override=scene_override,
        native_layout=True,
        geometry=geometry,
        protected_sources=_import_export_sources(request_path),
        source_revision=request.get("sourceRevision"),
        background_revision=request.get("backgroundRevision"),
    )
    return {"ok": True, "outputPath": str(output)}


def export_current_iff(request_path: Path) -> dict:
    request = read_request(request_path)
    from .experimental_lines import request_geometry
    geometry = request_geometry(PROJECT_ROOT, request) if request.get("experimental") or request.get("buildMode") == "game-uv" else None
    output_path = Path(request["outputPath"])
    protected_sources = _export_sources(request)
    ensure_new_export(output_path, Path(request_path), *protected_sources)
    base = _ready_import_base()
    texconv = PROJECT_ROOT / "tools" / "texconv.exe"
    scene_override = None
    if (request.get("experimental") or request.get("buildMode") == "game-uv") and request.get("mappingMode", "game-uv") == "game-uv":
        from .experimental_lines import native_export_scene
        with open_iff(NBA2K27_EXPORT_BASE) as archive:
            scene_override = native_export_scene(read_iff_scene(archive))
    with tempfile.TemporaryDirectory(prefix="court-export-") as folder:
        png_path = Path(folder) / "court.png"
        render_request = {**request, "outputPath": str(png_path), "exportFullResolution": True}
        render_preview(render_request, geometry=geometry)
        output = package_png_into_iff(
            png_path,
            NBA2K27_EXPORT_BASE,
            base["selected"],
            output_path,
            texconv,
            scene_override=scene_override,
            native_layout=scene_override is not None,
            geometry=geometry,
            protected_sources=protected_sources,
        )
    return {"ok": True, "outputPath": str(output)}


def default_template_path() -> Path:
    candidates = [PROJECT_COURT_TEMPLATE_PSD, ASSET_COURT_TEMPLATE_PSD]
    if not ASSET_ROOT.exists():
        candidates.append(LEGACY_COURT_TEMPLATE_PSD)
    candidates.append(DOWNLOAD_COURT_TEMPLATE_PSD)
    for path in candidates:
        if path.exists():
            return path
    raise RuntimeError("Could not find the court PSD template.")


def ensure_preview(template_path: Path) -> None:
    if PREVIEW_CACHE.exists():
        return
    create_court_preview_png(template_path, PREVIEW_CACHE)


def load_team_palettes() -> list:
    data = read_json(TEAM_PALETTES_PATH, {})
    entries = data if isinstance(data, list) else data.get("palettes", []) if isinstance(data, dict) else []
    if not isinstance(entries, list):
        return []
    palettes = []
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        palette = dict(entry)
        for key in ("league", "team", "source", "paletteNote"):
            if key in palette and not isinstance(palette[key], str):
                palette.pop(key)
        colors = entry.get("colors", [])
        palette["colors"] = []
        for color in colors if isinstance(colors, list) else []:
            if not isinstance(color, dict) or not isinstance(color.get("hex"), str) or re.fullmatch(r"#?[0-9a-fA-F]{6}", color["hex"].strip()) is None:
                continue
            value = dict(color)
            if "name" in value and not isinstance(value["name"], str):
                value.pop("name")
            palette["colors"].append(value)
        palettes.append(palette)
    return palettes


def load_presets() -> list:
    data = read_json(PRESETS_PATH, {})
    presets = data.get("presets", []) if isinstance(data, dict) else []
    presets = [item if isinstance(item, dict) else None for item in presets[:5]] if isinstance(presets, list) else []
    while len(presets) < 5:
        presets.append(None)
    return presets[:5]


def _floor_entries(data, key):
    entries = data.get(key, []) if isinstance(data, dict) else []
    return [item for item in entries if isinstance(item, dict) and isinstance(item.get("path"), str)
            and item["path"] and "\0" not in item["path"]] if isinstance(entries, list) else []


def load_custom_floor_layers(document) -> tuple[list[CourtLayer], list[dict]]:
    layers: list[CourtLayer] = []
    images: list[dict] = []
    floor_group = court_floor_group(document.layers)
    fallback_bbox = court_floor_bbox(document.layers, floor_group)
    if floor_group is None or fallback_bbox is None or not CUSTOM_FLOORS_META.exists():
        return layers, images
    data = read_json(CUSTOM_FLOORS_META, {})
    if not isinstance(data, dict):
        return layers, images
    for index, item in enumerate(_floor_entries(data, "floors")):
        path = resolve_asset_path(item["path"])
        if not path.is_file():
            continue
        bbox = item.get("bbox", fallback_bbox)
        try:
            bbox = tuple(int(value) for value in bbox) if isinstance(bbox, (list, tuple)) and len(bbox) == 4 else fallback_bbox
            if bbox[2] <= 0 or bbox[3] <= 0:
                bbox = fallback_bbox
        except (TypeError, ValueError, OverflowError):
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
                "previewPath": str(path),
                "bbox": layer.bbox,
            }
        )
    return layers, images


def load_floor_template_layers(
    document,
    *,
    start_index: int = 0,
) -> tuple[list[CourtLayer], list[dict], str]:
    layers: list[CourtLayer] = []
    images: list[dict] = []
    floor_group = court_floor_group(document.layers)
    fallback_bbox = court_floor_bbox(document.layers, floor_group)
    if floor_group is None or fallback_bbox is None:
        return layers, images, "No game court library"

    template_index = 0
    category_groups: dict[str, CourtLayer] = {}
    library_root = ASSET_ROOT if ASSET_ROOT.exists() else LEGACY_ASSET_ROOT
    meta_paths = list(library_root.glob(FLOOR_TEMPLATE_META_GLOB))

    def library_version(path: Path) -> int:
        match = re.search(r"nba2k(\d+)_floor_templates", path.name.casefold())
        return int(match.group(1)) if match else 0

    library_data: list[dict] = []
    newest_version = 0
    for version in sorted({library_version(path) for path in meta_paths}, reverse=True):
        candidates = []
        for meta_path in meta_paths:
            if library_version(meta_path) != version:
                continue
            data = read_json(meta_path, {})
            entries = _floor_entries(data, "templates")
            if entries:
                candidates.append({**data, "templates": entries})
        if candidates:
            newest_version = version
            library_data = candidates
            break
    library_name = f"NBA 2K{newest_version} Courts" if newest_version else "No game court library"
    for data in library_data:
        library_name = str(data.get("name") or library_name).replace(" Floor Templates", " Courts")
        for item in data["templates"]:
            if str(item.get("id") or "") in BROKEN_FLOOR_TEMPLATE_IDS:
                continue
            path = resolve_asset_path(item["path"])
            if not path.is_file():
                continue
            category = category_for_floor_template(item)
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
            thumbnail = item.get("thumbnailPath")
            thumbnail_path = resolve_asset_path(thumbnail) if isinstance(thumbnail, str) and "\0" not in thumbnail else path
            preview_path = thumbnail_path if thumbnail and thumbnail_path.is_file() else path
            images.append(
                {
                    "id": layer.id,
                    "name": layer.name,
                    "path": str(path.relative_to(library_root))
                    if path.is_relative_to(library_root)
                    else str(path),
                    "previewPath": str(preview_path),
                    "bbox": layer.bbox,
                    "isTemplate": True,
                    "category": category,
                    "sourceMip0": item.get("sourceMip0"),
                    "sourceTld": item.get("sourceTld"),
                }
            )
            template_index += 1
    return layers, images, library_name


def read_json(path: Path, fallback):
    try:
        return read_document(path)
    except (OSError, ValueError):
        return fallback


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
    floor_id = floor_id_for_template(item)
    if floor_id in WNBA_ARENA_IDS:
        return "WNBA"
    if floor_id in HISTORIC_ARENA_IDS:
        return "Historic NBA"
    if floor_id in EVENT_ARENA_IDS:
        return "All-Star & Events"
    if floor_id is not None and floor_id.startswith("4"):
        return "Unknown"
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
    if floor_id in NBA_ARENA_IDS or re.search(r"floor[_ -]\d+[_ -]court[_ -]wood", token):
        return "NBA"
    fallback = str(item.get("category") or "").strip()
    if fallback and fallback != "Numbered Courts":
        return fallback
    if fallback == "Numbered Courts":
        return "NBA"
    return "Special"


def floor_id_for_template(item: dict) -> str | None:
    token = " ".join(
        str(value or "")
        for value in (item.get("id"), item.get("name"), item.get("sourceMip0"))
    ).casefold()
    for pattern in (
        r"floor[_ -](\d{3})(?:\D|$)",
        r"\((\d{3})\)",
        r"\b(\d{3})\s+court\s+wood",
    ):
        match = re.search(pattern, token)
        if match:
            return match.group(1)
    return None


def category_rank(category: str) -> int:
    order = {
        "NBA": 0,
        "Unknown": 5,
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


def save_custom_floor_metadata(images: list, *, metadata: dict | None = None, store=None, expected_revision=None, sources=()) -> None:
    payload = json.dumps({**(metadata or {}), "floors": images}, indent=2, ensure_ascii=False, allow_nan=False).encode("utf-8")
    if len(payload) > MAX_METADATA_BYTES:
        raise ValueError("The updated custom-floor catalog exceeds the 8 MiB size limit. Its file was left unchanged.")
    if store is not None:
        store.publish(payload, expected_revision, sources=sources)
    else:
        store = CustomFloorStore(PROJECT_ROOT, CUSTOM_FLOORS_DIR, CUSTOM_FLOORS_META)
        store.snapshot()
        with store.locked():
            _metadata, revision = store.snapshot()
            store.publish(payload, revision, sources=sources)


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
        for old_root, new_root in (
            (LEGACY_ASSET_ROOT, ASSET_ROOT if ASSET_ROOT.exists() else LEGACY_ASSET_ROOT),
            (LEGACY_PROJECT_ROOT, PROJECT_ROOT),
            (LEGACY_ONEDRIVE_PROJECT_ROOT, PROJECT_ROOT),
        ):
            if path.is_relative_to(old_root):
                return new_root / path.relative_to(old_root)
        return path
    local_path = PROJECT_ROOT / path
    if local_path.exists():
        return local_path
    return (ASSET_ROOT if ASSET_ROOT.exists() else LEGACY_ASSET_ROOT) / path


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
