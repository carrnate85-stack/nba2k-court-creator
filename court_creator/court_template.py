from __future__ import annotations

from collections import OrderedDict
from dataclasses import asdict, dataclass
import hashlib
import json
from pathlib import Path
import struct
import threading


@dataclass(frozen=True)
class CourtLayer:
    id: str
    name: str
    kind: str
    parent_id: str | None
    psd_index: int
    depth: int
    visible: bool
    opacity: int
    blend_mode: str
    bbox: tuple[int, int, int, int]
    divider_type: int | None = None


@dataclass(frozen=True)
class CourtLayerDocument:
    path: str
    width: int
    height: int
    channels: int
    depth: int
    color_mode: int
    layers: tuple[CourtLayer, ...]


_PREVIEW_LAYER_CACHE: OrderedDict[tuple, tuple[object, tuple[int, int]]] = OrderedDict()
_PREVIEW_LAYER_CACHE_LIMIT = 64
_CHANNEL_OFFSET_CACHE: dict[
    tuple[str, int, int], tuple[list[dict], int, int, int, int]
] = {}
_PREVIEW_CACHE_LOCK = threading.Lock()


def parse_court_psd_layers(path: Path) -> CourtLayerDocument:
    psd_path = Path(path)
    with psd_path.open("rb") as handle:
        signature = handle.read(4)
        if signature != b"8BPS":
            raise ValueError("This is not a Photoshop PSD/PSB file.")

        version = _read_u16(handle)
        if version not in {1, 2}:
            raise ValueError(f"Unsupported Photoshop file version: {version}.")

        handle.read(6)
        channels = _read_u16(handle)
        height = _read_u32(handle)
        width = _read_u32(handle)
        bit_depth = _read_u16(handle)
        color_mode = _read_u16(handle)

        color_mode_length = _read_u32(handle)
        handle.seek(color_mode_length, 1)
        image_resource_length = _read_u32(handle)
        handle.seek(image_resource_length, 1)

        layer_mask_length = _read_section_length(handle, version)
        layer_mask_end = handle.tell() + layer_mask_length
        if layer_mask_length <= 0:
            return _document_without_layers(
                psd_path, width, height, channels, bit_depth, color_mode
            )

        layer_info_length = _read_section_length(handle, version)
        layer_info_end = handle.tell() + layer_info_length
        if layer_info_length <= 0:
            handle.seek(layer_mask_end)
            return _document_without_layers(
                psd_path, width, height, channels, bit_depth, color_mode
            )

        layer_count = abs(_read_i16(handle))
        raw_layers = [_read_layer_record(handle, version, index) for index in range(layer_count)]
        handle.seek(layer_info_end)
        handle.seek(layer_mask_end)

    return CourtLayerDocument(
        path=str(psd_path),
        width=width,
        height=height,
        channels=channels,
        depth=bit_depth,
        color_mode=color_mode,
        layers=tuple(_build_layer_tree(raw_layers)),
    )


def create_court_preview_png(
    psd_path: Path,
    output_path: Path,
    *,
    max_size: tuple[int, int] | None = (2048, 1024),
) -> None:
    try:
        from PIL import Image
    except ImportError as exc:
        raise RuntimeError("Court preview export requires Pillow.") from exc

    with Image.open(psd_path) as image:
        preview = image.convert("RGBA")
        if max_size is not None:
            preview.thumbnail(max_size)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        preview.save(output_path)


def create_visible_court_preview_png(
    psd_path: Path,
    document: CourtLayerDocument,
    visibility: dict[str, bool],
    output_path: Path,
    *,
    color_overrides: dict[str, tuple[int, int, int]] | None = None,
    custom_floor_images: list[dict] | None = None,
    max_size: tuple[int, int] | None = (2048, 1024),
) -> None:
    try:
        from PIL import Image
    except ImportError as exc:
        raise RuntimeError("Court preview export requires Pillow.") from exc

    psd_path = Path(psd_path)
    path_key = _preview_cache_path_key(psd_path)
    raw_layers, width, height, bit_depth, color_mode = _cached_layers_with_channel_offsets(
        psd_path, path_key
    )
    if bit_depth != 8 or color_mode != 3:
        create_court_preview_png(psd_path, output_path, max_size=max_size)
        return

    if max_size is None:
        scale = 1.0
        preview_size = (width, height)
    else:
        scale = min(max_size[0] / width, max_size[1] / height)
        preview_size = (max(1, round(width * scale)), max(1, round(height * scale)))

    canvas = Image.new("RGBA", preview_size, (0, 0, 0, 0))
    layers_by_id = {layer.id: layer for layer in document.layers}
    raw_by_index = {raw["psd_index"]: raw for raw in raw_layers}
    color_overrides = color_overrides or {}
    custom_floor_images = custom_floor_images or []

    with Path(psd_path).open("rb") as handle:
        for layer in document.layers:
            if _is_court_floor_group_name(layer.name):
                for floor in custom_floor_images:
                    _composite_custom_floor(canvas, floor, scale)
            if layer.kind == "group" or not _is_layer_visible(layer, layers_by_id, visibility):
                continue
            raw = raw_by_index.get(layer.psd_index)
            if raw is None:
                continue
            preview_layer = _read_preview_layer_image(handle, raw, path_key, scale)
            if preview_layer is None:
                continue
            layer_image, position = preview_layer
            override = _color_override_for(layer, layers_by_id, color_overrides)
            if override is not None:
                layer_image = _apply_layer_color(layer_image, override)
            if layer.opacity < 255:
                layer_image = layer_image.copy()
                alpha = layer_image.getchannel("A").point(
                    lambda value, opacity=layer.opacity: value * opacity // 255
                )
                layer_image.putalpha(alpha)
            canvas.alpha_composite(layer_image, position)

    if canvas.getbbox() is None:
        create_court_preview_png(psd_path, output_path, max_size=max_size)
        return

    background = Image.new("RGBA", canvas.size, (32, 36, 43, 255))
    image = Image.alpha_composite(background, canvas)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    image.save(output_path)


def warm_visible_preview_layers(
    psd_path: Path,
    document: CourtLayerDocument,
    layer_ids: set[str],
    *,
    max_size: tuple[int, int] | None = (2048, 1024),
) -> None:
    psd_path = Path(psd_path)
    path_key = _preview_cache_path_key(psd_path)
    raw_layers, width, height, bit_depth, color_mode = _cached_layers_with_channel_offsets(
        psd_path, path_key
    )
    if bit_depth != 8 or color_mode != 3:
        return
    if max_size is None:
        scale = 1.0
    else:
        scale = min(max_size[0] / width, max_size[1] / height)

    wanted_indices = {
        layer.psd_index
        for layer in document.layers
        if layer.id in layer_ids and layer.kind != "group"
    }
    raw_by_index = {raw["psd_index"]: raw for raw in raw_layers}
    with psd_path.open("rb") as handle:
        for index in wanted_indices:
            raw = raw_by_index.get(index)
            if raw is not None:
                _read_preview_layer_image(handle, raw, path_key, scale)


def sample_template_layer_color(
    psd_path: Path,
    document: CourtLayerDocument,
    layer_id: str,
    *,
    max_size: tuple[int, int] | None = (2048, 1024),
) -> tuple[int, int, int] | None:
    psd_path = Path(psd_path)
    layer = next((item for item in document.layers if item.id == layer_id), None)
    if layer is None or layer.kind != "layer":
        return None

    path_key = _preview_cache_path_key(psd_path)
    raw_layers, width, height, bit_depth, color_mode = _cached_layers_with_channel_offsets(
        psd_path, path_key
    )
    if bit_depth != 8 or color_mode != 3:
        return None
    if max_size is None:
        scale = 1.0
    else:
        scale = min(max_size[0] / width, max_size[1] / height)

    raw_by_index = {raw["psd_index"]: raw for raw in raw_layers}
    raw = raw_by_index.get(layer.psd_index)
    if raw is None:
        return None
    with psd_path.open("rb") as handle:
        preview_layer = _read_preview_layer_image(handle, raw, path_key, scale)
    if preview_layer is None:
        return None
    image, _position = preview_layer
    return _average_visible_color(image)


def save_court_layer_state(
    path: Path,
    document: CourtLayerDocument,
    visibility: dict[str, bool],
    selected_layer_id: str | None,
    color_overrides: dict[str, tuple[int, int, int]] | None = None,
    name_overrides: dict[str, str] | None = None,
) -> None:
    data = {
        "template_path": document.path,
        "width": document.width,
        "height": document.height,
        "layers": [asdict(layer) for layer in document.layers],
        "visibility": visibility,
        "selected_layer_id": selected_layer_id,
        "color_overrides": color_overrides or {},
        "name_overrides": name_overrides or {},
    }
    Path(path).write_text(json.dumps(data, indent=2), encoding="utf-8")


def load_court_layer_state(
    path: Path,
) -> tuple[
    CourtLayerDocument,
    dict[str, bool],
    str | None,
    dict[str, tuple[int, int, int]],
    dict[str, str],
]:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    layers = tuple(
        CourtLayer(
            id=str(layer["id"]),
            name=str(layer["name"]),
            kind=str(layer["kind"]),
            parent_id=layer.get("parent_id"),
            psd_index=int(layer["psd_index"]),
            depth=int(layer.get("depth", 0)),
            visible=bool(layer.get("visible", True)),
            opacity=int(layer.get("opacity", 255)),
            blend_mode=str(layer.get("blend_mode", "norm")),
            bbox=tuple(layer.get("bbox", (0, 0, 0, 0))),
            divider_type=layer.get("divider_type"),
        )
        for layer in data.get("layers", [])
    )
    document = CourtLayerDocument(
        path=str(data.get("template_path", "")),
        width=int(data.get("width", 0)),
        height=int(data.get("height", 0)),
        channels=0,
        depth=8,
        color_mode=3,
        layers=layers,
    )
    visibility = {
        str(layer_id): bool(value)
        for layer_id, value in data.get("visibility", {}).items()
    }
    color_overrides = {
        str(layer_id): tuple(int(channel) for channel in value[:3])
        for layer_id, value in data.get("color_overrides", {}).items()
        if isinstance(value, list | tuple) and len(value) >= 3
    }
    name_overrides = {
        str(layer_id): str(value)
        for layer_id, value in data.get("name_overrides", {}).items()
        if str(value).strip()
    }
    return document, visibility, data.get("selected_layer_id"), color_overrides, name_overrides


def _document_without_layers(
    path: Path,
    width: int,
    height: int,
    channels: int,
    bit_depth: int,
    color_mode: int,
) -> CourtLayerDocument:
    return CourtLayerDocument(
        path=str(path),
        width=width,
        height=height,
        channels=channels,
        depth=bit_depth,
        color_mode=color_mode,
        layers=(),
    )


def _build_layer_tree(raw_layers: list[dict]) -> list[CourtLayer]:
    children: dict[str | None, list[CourtLayer]] = {None: []}
    stack: list[str] = []

    for raw in reversed(raw_layers):
        divider_type = raw["divider_type"]
        if divider_type == 3:
            if stack:
                stack.pop()
            continue

        layer_id = f"layer_{raw['psd_index']:03d}"
        parent_id = stack[-1] if stack else None
        kind = "group" if divider_type in {1, 2} else "layer"
        layer = CourtLayer(
            id=layer_id,
            name=raw["name"],
            kind=kind,
            parent_id=parent_id,
            psd_index=raw["psd_index"],
            depth=len(stack),
            visible=raw["visible"],
            opacity=raw["opacity"],
            blend_mode=raw["blend_mode"],
            bbox=raw["bbox"],
            divider_type=divider_type,
        )
        children.setdefault(parent_id, []).insert(0, layer)
        children.setdefault(layer_id, [])
        if kind == "group":
            stack.append(layer_id)

    ordered: list[CourtLayer] = []

    def add_branch(parent_id: str | None) -> None:
        for layer in children.get(parent_id, []):
            ordered.append(layer)
            if layer.kind == "group":
                add_branch(layer.id)

    add_branch(None)
    return ordered


def _read_layer_record(handle, version: int, index: int) -> dict:
    top = _read_i32(handle)
    left = _read_i32(handle)
    bottom = _read_i32(handle)
    right = _read_i32(handle)
    channel_count = _read_u16(handle)
    channels = []
    for _ in range(channel_count):
        channel_id = _read_i16(handle)
        length = _read_section_length(handle, version)
        channels.append({"id": channel_id, "length": length})

    handle.read(4)
    blend_mode = handle.read(4).decode("latin1", errors="replace")
    opacity = handle.read(1)[0]
    handle.read(1)
    flags = handle.read(1)[0]
    handle.read(1)

    extra_length = _read_section_length(handle, version)
    extra_end = handle.tell() + extra_length

    mask_length = _read_u32(handle)
    handle.seek(mask_length, 1)
    blending_ranges_length = _read_u32(handle)
    handle.seek(blending_ranges_length, 1)

    name = _read_pascal_string(handle)
    divider_type = None

    while handle.tell() + 12 <= extra_end:
        signature = handle.read(4)
        key = handle.read(4).decode("latin1", errors="replace")
        if signature not in {b"8BIM", b"8B64"}:
            break
        block_length = _read_u32(handle)
        data = handle.read(block_length)
        if block_length % 2:
            handle.read(1)
        if key == "luni":
            unicode_name = _decode_unicode_layer_name(data)
            if unicode_name:
                name = unicode_name
        elif key in {"lsct", "lsdk"} and len(data) >= 4:
            divider_type = struct.unpack(">I", data[:4])[0]

    handle.seek(extra_end)
    return {
        "psd_index": index,
        "name": name,
        "divider_type": divider_type,
        "visible": not bool(flags & 2),
        "opacity": opacity,
        "blend_mode": blend_mode,
        "bbox": (left, top, max(0, right - left), max(0, bottom - top)),
        "channels": channels,
    }


def _read_layers_with_channel_offsets(
    path: Path,
) -> tuple[list[dict], int, int, int, int]:
    with Path(path).open("rb") as handle:
        signature = handle.read(4)
        if signature != b"8BPS":
            raise ValueError("This is not a Photoshop PSD/PSB file.")

        version = _read_u16(handle)
        handle.read(6)
        _read_u16(handle)
        height = _read_u32(handle)
        width = _read_u32(handle)
        bit_depth = _read_u16(handle)
        color_mode = _read_u16(handle)

        color_mode_length = _read_u32(handle)
        handle.seek(color_mode_length, 1)
        image_resource_length = _read_u32(handle)
        handle.seek(image_resource_length, 1)

        layer_mask_length = _read_section_length(handle, version)
        layer_mask_end = handle.tell() + layer_mask_length
        if layer_mask_length <= 0:
            return [], width, height, bit_depth, color_mode

        layer_info_length = _read_section_length(handle, version)
        layer_info_end = handle.tell() + layer_info_length
        if layer_info_length <= 0:
            handle.seek(layer_mask_end)
            return [], width, height, bit_depth, color_mode

        layer_count = abs(_read_i16(handle))
        raw_layers = [
            _read_layer_record(handle, version, index) for index in range(layer_count)
        ]
        for raw in raw_layers:
            for channel in raw["channels"]:
                channel["offset"] = handle.tell()
                handle.seek(channel["length"], 1)
        handle.seek(layer_info_end)
        handle.seek(layer_mask_end)
    return raw_layers, width, height, bit_depth, color_mode


def _cached_layers_with_channel_offsets(
    path: Path,
    path_key: tuple[str, int, int],
) -> tuple[list[dict], int, int, int, int]:
    with _PREVIEW_CACHE_LOCK:
        cached = _CHANNEL_OFFSET_CACHE.get(path_key)
        if cached is not None:
            return cached
    result = _read_layers_with_channel_offsets(path)
    with _PREVIEW_CACHE_LOCK:
        _CHANNEL_OFFSET_CACHE[path_key] = result
    return result


def _is_layer_visible(
    layer: CourtLayer,
    layers_by_id: dict[str, CourtLayer],
    visibility: dict[str, bool],
) -> bool:
    if not visibility.get(layer.id, layer.visible):
        return False
    parent_id = layer.parent_id
    while parent_id:
        parent = layers_by_id.get(parent_id)
        if parent is None:
            break
        if not visibility.get(parent.id, parent.visible):
            return False
        parent_id = parent.parent_id
    return True


def _color_override_for(
    layer: CourtLayer,
    layers_by_id: dict[str, CourtLayer],
    color_overrides: dict[str, tuple[int, int, int]],
) -> tuple[int, int, int] | None:
    if layer.id in color_overrides:
        return color_overrides[layer.id]
    parent_id = layer.parent_id
    while parent_id:
        if parent_id in color_overrides:
            return color_overrides[parent_id]
        parent = layers_by_id.get(parent_id)
        if parent is None:
            break
        parent_id = parent.parent_id
    return None


def _apply_layer_color(image, color: tuple[int, int, int]):
    try:
        from PIL import Image
    except ImportError as exc:
        raise RuntimeError("Court preview export requires Pillow.") from exc

    color_layer = Image.new("RGBA", image.size, (*color, 0))
    color_layer.putalpha(image.getchannel("A"))
    return color_layer


def _composite_custom_floor(
    canvas,
    floor: dict,
    scale: float,
) -> None:
    try:
        from PIL import Image
    except ImportError as exc:
        raise RuntimeError("Court preview export requires Pillow.") from exc

    path = Path(str(floor.get("path", "")))
    bbox = tuple(floor.get("bbox", (0, 0, 0, 0)))
    if len(bbox) != 4 or not path.exists():
        return
    left, top, width, height = (int(value) for value in bbox)
    if width <= 0 or height <= 0:
        return

    with Image.open(path) as opened:
        image = opened.convert("RGBA")
    image = image.resize(
        (max(1, round(width * scale)), max(1, round(height * scale))),
        Image.Resampling.LANCZOS,
    )
    canvas.alpha_composite(image, (round(left * scale), round(top * scale)))


def _is_court_floor_group_name(name: str) -> bool:
    normalized = " ".join(name.casefold().replace("_", " ").replace("-", " ").split())
    return normalized in {"court floors", "court floor", "floor options", "floors"}


def _average_visible_color(image) -> tuple[int, int, int] | None:
    image = image.convert("RGBA")
    alpha = image.getchannel("A")
    bbox = alpha.point(lambda value: 255 if value > 16 else 0).getbbox()
    if bbox is None:
        return None
    cropped = image.crop(bbox)
    if max(cropped.size) > 256:
        cropped.thumbnail((256, 256))

    red_total = 0
    green_total = 0
    blue_total = 0
    alpha_total = 0
    for red, green, blue, alpha_value in cropped.getdata():
        if alpha_value <= 16:
            continue
        red_total += red * alpha_value
        green_total += green * alpha_value
        blue_total += blue * alpha_value
        alpha_total += alpha_value
    if alpha_total == 0:
        return None
    return (
        round(red_total / alpha_total),
        round(green_total / alpha_total),
        round(blue_total / alpha_total),
    )


def _read_layer_image(handle, raw: dict):
    try:
        from PIL import Image
    except ImportError as exc:
        raise RuntimeError("Court preview export requires Pillow.") from exc

    left, top, width, height = raw["bbox"]
    if width <= 0 or height <= 0:
        return None

    channels: dict[str, bytes] = {}
    channel_names = {0: "R", 1: "G", 2: "B", -1: "A", 65535: "A"}
    for channel in raw["channels"]:
        name = channel_names.get(channel["id"])
        if name is None:
            continue
        handle.seek(channel["offset"])
        data = handle.read(channel["length"])
        decoded = _decode_channel(data, width, height)
        if decoded is not None:
            channels[name] = decoded

    pixel_count = width * height
    red = channels.get("R", b"\x00" * pixel_count)
    green = channels.get("G", b"\x00" * pixel_count)
    blue = channels.get("B", b"\x00" * pixel_count)
    alpha = channels.get("A", b"\xff" * pixel_count)
    return Image.merge(
        "RGBA",
        (
            Image.frombytes("L", (width, height), red),
            Image.frombytes("L", (width, height), green),
            Image.frombytes("L", (width, height), blue),
            Image.frombytes("L", (width, height), alpha),
        ),
    )


def _read_preview_layer_image(
    handle,
    raw: dict,
    path_key: tuple[str, int, int],
    scale: float,
):
    try:
        from PIL import Image
    except ImportError as exc:
        raise RuntimeError("Court preview export requires Pillow.") from exc

    left, top, width, height = raw["bbox"]
    if width <= 0 or height <= 0:
        return None

    cache_key = (
        path_key,
        raw["psd_index"],
        round(scale, 8),
    )
    with _PREVIEW_CACHE_LOCK:
        cached = _PREVIEW_LAYER_CACHE.get(cache_key)
        if cached is not None:
            _PREVIEW_LAYER_CACHE.move_to_end(cache_key)
            image, position = cached
            return image.copy(), position

    cache_path = _preview_layer_cache_path(path_key, raw["psd_index"], scale)
    if cache_path.exists():
        try:
            with Image.open(cache_path) as cached_image:
                image = cached_image.convert("RGBA")
            position = (round(left * scale), round(top * scale))
            _remember_preview_layer(cache_key, image, position)
            return image.copy(), position
        except OSError:
            pass

    image = _read_layer_image(handle, raw)
    if image is None:
        return None

    if scale != 1.0:
        preview_width = max(1, round(width * scale))
        preview_height = max(1, round(height * scale))
        image = image.resize((preview_width, preview_height), Image.Resampling.LANCZOS)
    position = (round(left * scale), round(top * scale))

    _remember_preview_layer(cache_key, image, position)
    try:
        cache_path.parent.mkdir(parents=True, exist_ok=True)
        image.save(cache_path)
    except OSError:
        pass
    return image, position


def _preview_cache_path_key(path: Path) -> tuple[str, int, int]:
    stat = Path(path).stat()
    return (str(Path(path).resolve()), stat.st_mtime_ns, stat.st_size)


def _remember_preview_layer(
    cache_key: tuple,
    image,
    position: tuple[int, int],
) -> None:
    with _PREVIEW_CACHE_LOCK:
        _PREVIEW_LAYER_CACHE[cache_key] = (image.copy(), position)
        _PREVIEW_LAYER_CACHE.move_to_end(cache_key)
        while len(_PREVIEW_LAYER_CACHE) > _PREVIEW_LAYER_CACHE_LIMIT:
            _PREVIEW_LAYER_CACHE.popitem(last=False)


def _preview_layer_cache_path(
    path_key: tuple[str, int, int],
    psd_index: int,
    scale: float,
) -> Path:
    cache_id = hashlib.sha1(
        f"{path_key[0]}|{path_key[1]}|{path_key[2]}|{psd_index}|{scale:.8f}".encode(
            "utf-8"
        )
    ).hexdigest()
    return Path.cwd() / "outputs" / "preview_layer_cache" / f"{cache_id}.png"


def _decode_channel(data: bytes, width: int, height: int) -> bytes | None:
    if len(data) < 2:
        return None
    compression = struct.unpack(">H", data[:2])[0]
    body = data[2:]
    expected = width * height
    if compression == 0:
        return body[:expected].ljust(expected, b"\x00")
    if compression != 1:
        return None

    byte_counts_length = height * 2
    if len(body) < byte_counts_length:
        return None
    counts = [
        struct.unpack(">H", body[row * 2 : row * 2 + 2])[0]
        for row in range(height)
    ]
    compressed = memoryview(body)[byte_counts_length:]
    position = 0
    rows = []
    for count in counts:
        row_data = _decode_packbits(compressed[position : position + count], width)
        rows.append(row_data)
        position += count
    return b"".join(rows)[:expected].ljust(expected, b"\x00")


def _decode_packbits(data, expected_length: int) -> bytes:
    output = bytearray()
    index = 0
    data_length = len(data)
    while index < data_length and len(output) < expected_length:
        header = data[index]
        index += 1
        if header <= 127:
            count = header + 1
            output.extend(data[index : index + count])
            index += count
        elif header >= 129:
            count = 257 - header
            if index >= data_length:
                break
            output.extend(bytes([data[index]]) * count)
            index += 1
    return bytes(output[:expected_length]).ljust(expected_length, b"\x00")


def _decode_unicode_layer_name(data: bytes) -> str | None:
    if len(data) < 4:
        return None
    character_count = struct.unpack(">I", data[:4])[0]
    raw = data[4 : 4 + character_count * 2]
    if not raw:
        return None
    return raw.decode("utf-16-be", errors="replace")


def _read_pascal_string(handle) -> str:
    size_raw = handle.read(1)
    if not size_raw:
        return ""
    size = size_raw[0]
    data = handle.read(size)
    padding = (4 - ((size + 1) % 4)) % 4
    if padding:
        handle.read(padding)
    return data.decode("macroman", errors="replace")


def _read_section_length(handle, version: int) -> int:
    if version == 2:
        return struct.unpack(">Q", handle.read(8))[0]
    return _read_u32(handle)


def _read_u16(handle) -> int:
    return struct.unpack(">H", handle.read(2))[0]


def _read_i16(handle) -> int:
    return struct.unpack(">h", handle.read(2))[0]


def _read_u32(handle) -> int:
    return struct.unpack(">I", handle.read(4))[0]


def _read_i32(handle) -> int:
    return struct.unpack(">i", handle.read(4))[0]
