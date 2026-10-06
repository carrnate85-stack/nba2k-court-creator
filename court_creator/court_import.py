"""Inspect legacy court IFFs and align their artwork to a modern floor texture."""

from __future__ import annotations

from io import BytesIO
from contextlib import closing, contextmanager
from collections import OrderedDict
import csv
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import struct
import tempfile
import threading
import time
import zipfile
from zipfile import BadZipFile, ZipFile, is_zipfile

from PIL import Image, ImageDraw, ImageFilter

from .export_io import cleanup_staging, ensure_new_export, owned_staging, staged_export, staging_identity
from .asset_io import MAX_ASSET_BYTES, validate_asset_image, verified_asset_stream
from .json_io import parse_document, read_document, read_document_snapshot
from .tool_process import run_tool
from .preview_receipt import stream_receipt, validate_receipt


OUTPUT_SIZE = (8192, 4096)
# Court boundary measured on a modern full-court texture, in normalized coordinates.
COURT_EDGES = (0.1317, 0.1094, 0.8683, 0.8906)
NBA2K27_BASE_ENTRY = "levels/floor_myteam2k26_int_floor.iff"
BASE_VERSION = b"CourtCreator-FullCourt-v2"
LAYOUT_PREFIX = b"CourtCreator-Layout-v1\n"
GAME_SETTINGS_PATH = Path(__file__).resolve().parent.parent / "data" / "game_installation.json"
MAX_GAME_SETTINGS_BYTES = 1024 * 1024
MAX_EXTRACTION_JOURNAL_BYTES = 64 * 1024
MAX_RECOVERY_FILE_BYTES = 512 * 1024 * 1024
MAX_BASE_ARCHIVE_BYTES = 512 * 1024 * 1024
MAX_STEAM_LIBRARIES_BYTES = 1024 * 1024
MAX_STEAM_LIBRARIES_TOKENS = 65536
MAX_STEAM_LIBRARIES_DEPTH = 32
_VDF_TOKEN = re.compile(r'\s+|//[^\r\n]*|"(?:\\.|[^"\\])*"|[{}]|[^\s{}"]+')
MAX_DDS_BYTES = 256 * 1024 * 1024
MAX_IFF_DIRECTORY_BYTES = 8 * 1024 * 1024
MAX_IFF_ENTRIES = 20000
MAX_SCENE_BYTES = 16 * 1024 * 1024
DXGI_FORMATS = {71: "BC1_UNORM", 72: "BC1_UNORM_SRGB", 77: "BC3_UNORM", 78: "BC3_UNORM_SRGB", 98: "BC7_UNORM", 99: "BC7_UNORM_SRGB"}
_BASE_LOCK = threading.Lock()
_GAME_SETTINGS_LOCK = threading.Lock()
_DDS_IMAGE_CACHE_BYTES = 128 * 1024 * 1024
_DDS_IMAGE_CACHE_LIMIT = 2
_DDS_IMAGE_CACHE: OrderedDict[str, Image.Image] = OrderedDict()
_DDS_IMAGE_CACHE_LOCK = threading.Lock()


class _BoundedArchiveReader:
    def __init__(self, stream):
        self.stream = stream
        self.inspecting_directory = True

    def __getattr__(self, name):
        return getattr(self.stream, name)

    def read(self, size=-1):
        if self.inspecting_directory and (size < 0 or size > MAX_IFF_DIRECTORY_BYTES):
            raise ValueError("This IFF's ZIP directory exceeds the 8 MB inspection limit.")
        data = self.stream.read(size)
        if self.inspecting_directory and data.startswith(zipfile.stringCentralDir):
            # ZipFile allocates all entry objects after this read. Count the same
            # fixed headers first, including archives with a false end-record count.
            cursor = count = 0
            while cursor + zipfile.sizeCentralDir <= len(data):
                header = struct.unpack_from(zipfile.structCentralDir, data, cursor)
                if header[zipfile._CD_SIGNATURE] != zipfile.stringCentralDir:
                    break
                count += 1
                if count > MAX_IFF_ENTRIES:
                    raise ValueError("This IFF has too many entries to inspect safely.")
                cursor += (zipfile.sizeCentralDir + header[zipfile._CD_FILENAME_LENGTH]
                           + header[zipfile._CD_EXTRA_FIELD_LENGTH] + header[zipfile._CD_COMMENT_LENGTH])
        return data


@contextmanager
def _read_iff_archive(stream):
    reader = _BoundedArchiveReader(stream)
    try:
        archive = ZipFile(reader)
    except (BadZipFile, UnicodeError, NotImplementedError) as error:
        raise ValueError("Choose a readable ZIP-style NBA 2K court .iff file.") from error
    with archive:
        reader.inspecting_directory = False
        _unique_entries(archive)
        yield archive


@contextmanager
def open_iff(path: Path):
    with Path(path).open("rb", buffering=0) as stream, _read_iff_archive(stream) as archive:
        yield archive


def read_iff_scene(archive: ZipFile) -> bytes:
    info = archive.getinfo("level_floor.SCNE")
    if info.file_size > MAX_SCENE_BYTES:
        raise ValueError("The floor scene exceeds the 16 MB size limit.")
    with archive.open(info) as stream:
        data = stream.read(MAX_SCENE_BYTES + 1)
    if len(data) != info.file_size or len(data) > MAX_SCENE_BYTES:
        raise ValueError("The floor scene is incomplete or exceeds the size limit.")
    return data


def _copy_iff_entry(original: ZipFile, destination: ZipFile, item, replacement: bytes | None = None) -> None:
    copied = copy.copy(item)
    if replacement is not None:
        destination.writestr(copied, replacement)
    else:
        with original.open(item) as source, destination.open(
                copied, "w", force_zip64=item.file_size >= zipfile.ZIP64_LIMIT) as target:
            shutil.copyfileobj(source, target, length=1024 * 1024)


def _entry_matches(archive: ZipFile, name: str, expected: bytes) -> bool:
    if archive.getinfo(name).file_size != len(expected):
        return False
    view = memoryview(expected)
    with archive.open(name) as stream:
        for offset in range(0, len(view), 1024 * 1024):
            chunk = view[offset:offset + 1024 * 1024]
            if stream.read(len(chunk)) != chunk:
                return False
        return not stream.read(1)


def target_court_bounds(output_size: tuple[int, int] = OUTPUT_SIZE, *, geometry: dict | None = None) -> list[int]:
    from .experimental_lines import load_geometry

    if geometry is None:
        geometry = load_geometry(Path(__file__).resolve().parent.parent)
    if geometry is None:
        return [round(value * scale) for value, scale in zip(
            COURT_EDGES, (*output_size, *output_size))]
    corners = [anchor for anchor in geometry["guides"]["game-uv"]["anchors"]
               if anchor["id"].startswith("court-corner-")]
    if len(corners) != 4 or geometry["size"] != list(OUTPUT_SIZE):
        raise ValueError("The stock court geometry has no supported conversion boundary.")
    bounds = [round(min(corner["x"] for corner in corners)),
              round(min(corner["y"] for corner in corners)),
              round(max(corner["x"] for corner in corners)),
              round(max(corner["y"] for corner in corners))]
    if not (0 <= bounds[0] < bounds[2] <= OUTPUT_SIZE[0]
            and 0 <= bounds[1] < bounds[3] <= OUTPUT_SIZE[1]):
        raise ValueError("The stock conversion boundary falls outside the court texture.")
    return [round(value * scale / original) for value, scale, original in zip(
        bounds, (*output_size, *output_size), (*OUTPUT_SIZE, *OUTPUT_SIZE))]


def _recorded_layout(comment: bytes, texture: dict) -> list[int] | None:
    if not comment.startswith(LAYOUT_PREFIX) or len(comment) > 4096:
        return None
    try:
        layout = json.loads(comment[len(LAYOUT_PREFIX):])
        bounds = layout["courtBounds"]
        width, height = texture["width"], texture["height"]
        if (layout["mapping"] != "game-uv" or layout["texture"] != texture["name"]
                or layout["size"] != [width, height] or not isinstance(bounds, list)
                or len(bounds) != 4 or any(type(value) is not int for value in bounds)
                or not (0 <= bounds[0] < bounds[2] <= width and 0 <= bounds[1] < bounds[3] <= height)):
            return None
        return bounds
    except (ValueError, TypeError, KeyError):
        return None


def _dds_info(archive: ZipFile, name: str) -> dict | None:
    info = archive.getinfo(name)
    if info.file_size < 128 or info.file_size > MAX_DDS_BYTES:
        return None
    with archive.open(info) as stream:
        header = stream.read(148)
    return _dds_header_info(header, info.file_size, name)


def _dds_header_info(header: bytes, byte_count: int, name: str, *, exact_payload: bool = False) -> dict | None:
    if byte_count < 128 or byte_count > MAX_DDS_BYTES:
        return None
    if header[:4] != b"DDS " or len(header) < 128:
        return None
    if struct.unpack_from("<I", header, 4)[0] != 124 or struct.unpack_from("<I", header, 76)[0] != 32:
        return None
    height, width = struct.unpack_from("<II", header, 12)
    if not (16 <= width <= 16384 and 16 <= height <= 16384):
        return None
    fourcc = header[84:88]
    depth, mipmaps = struct.unpack_from("<II", header, 24)
    if depth > 1 or struct.unpack_from("<I", header, 112)[0] & (0xFE00 | 0x200000) or mipmaps > max(width, height).bit_length():
        return None
    if exact_payload and (not struct.unpack_from("<I", header, 80)[0] & 4
                          or struct.unpack_from("<I", header, 8)[0] & 0x800000):
        return None
    block_bytes = {b"DXT1": 8, b"DXT3": 16, b"DXT5": 16, b"ATI1": 8, b"BC4U": 8,
                   b"BC4S": 8, b"ATI2": 16, b"BC5U": 16, b"BC5S": 16}.get(fourcc)
    header_bytes = 128
    if fourcc == b"DX10":
        if len(header) < 148:
            return None
        dxgi, dimension, misc, arrays, _alpha_mode = struct.unpack_from("<5I", header, 128)
        if dimension != 3 or misc & 4 or arrays != 1:
            return None
        format_name = DXGI_FORMATS.get(dxgi, "Other DX10")
        block_bytes = 8 if 70 <= dxgi <= 72 or 79 <= dxgi <= 81 else 16 if 73 <= dxgi <= 78 or 82 <= dxgi <= 84 or 94 <= dxgi <= 99 else None
        header_bytes = 148
    else:
        format_name = {b"DXT1": "BC1_UNORM", b"DXT5": "BC3_UNORM"}.get(fourcc, "Other")
    if block_bytes is not None:
        required = header_bytes + sum(max(1, (max(1, width >> level) + 3) // 4)
                                      * max(1, (max(1, height >> level) + 3) // 4) * block_bytes
                                      for level in range(max(1, mipmaps)))
        if byte_count < required or exact_payload and byte_count != required:
            return None
    else:
        flags, bit_count = struct.unpack_from("<I", header, 80)[0], struct.unpack_from("<I", header, 88)[0]
        bytes_per_pixel = (4 if fourcc == b"DX10" and dxgi in (27, 28, 29)
                           else bit_count // 8 if flags & (0x40 | 0x20000) and bit_count in (8, 16, 24, 32)
                           else 1 if flags & 0x20 else None)
        if bytes_per_pixel is not None:
            required = header_bytes + (1024 if flags & 0x20 else 0) + sum(max(1, width >> level) * max(1, height >> level) * bytes_per_pixel for level in range(max(1, mipmaps)))
            if byte_count < required or exact_payload and byte_count != required:
                return None
        elif exact_payload:
            return None
    return {"name": name, "width": width, "height": height, "format": format_name,
            "mipmaps": mipmaps, "bytes": byte_count}


def _read_encoded_dds(path: Path, selected: dict) -> bytes:
    try:
        info = _ordinary_file_info(path, "DDS encoder output")
        if info is None or not 128 <= info.st_size <= MAX_DDS_BYTES:
            raise RuntimeError("The DDS encoder output is incomplete or exceeds the 256 MB size limit.")
        token = _recovery_info_token(info)
        with path.open("rb", buffering=0) as stream:
            if _recovery_info_token(os.fstat(stream.fileno())) != token:
                raise RuntimeError("The DDS encoder output changed while being opened.")
            header = stream.read(148)
            descriptor = _dds_header_info(header, info.st_size, path.name, exact_payload=True)
            if descriptor is None:
                raise RuntimeError("The DDS encoder did not produce a complete, valid single-surface 2D texture.")
            if ((descriptor["width"], descriptor["height"]) != OUTPUT_SIZE
                    or descriptor["format"] != selected["format"]
                    or descriptor["mipmaps"] != (selected["mipmaps"] or 1)):
                raise RuntimeError("The converted DDS did not retain the base texture's size, format, and mipmaps.")
            stream.seek(0)
            data = stream.read(info.st_size + 1)
            final = os.fstat(stream.fileno())
        current = _ordinary_file_info(path, "DDS encoder output")
        if (current is None or _recovery_info_token(current) != token
                or _recovery_info_token(final) != token or len(data) != info.st_size
                or data[:len(header)] != header):
            raise RuntimeError("The DDS encoder output changed during validation.")
        return data
    except ValueError as error:
        raise RuntimeError(str(error)) from error


def _unique_entries(archive: ZipFile) -> None:
    entries = archive.infolist()
    if len(entries) > MAX_IFF_ENTRIES:
        raise ValueError("This IFF has too many entries to inspect safely.")
    if len({item.filename for item in entries}) != len(entries):
        raise ValueError("This IFF contains duplicate entry names. Use an archive with unambiguous texture names.")


def _read_dds_data(archive: ZipFile, name: str, expected_revision: str | None = None) -> tuple[bytes, dict, str]:
    _unique_entries(archive)
    try:
        descriptor = _dds_info(archive, name)
    except KeyError as error:
        raise ValueError("The selected DDS texture is no longer in this IFF. Reimport the source court.") from error
    if descriptor is None:
        raise ValueError("The selected DDS has an invalid, oversized or unsupported 2D texture header.")
    with archive.open(name) as stream:
        data = stream.read(MAX_DDS_BYTES + 1)
    if len(data) != descriptor["bytes"] or len(data) > MAX_DDS_BYTES:
        raise ValueError("The selected DDS texture is incomplete or exceeds the size limit.")
    digest = hashlib.sha256(archive.comment + b"\0" + name.encode("utf-8") + b"\0")
    digest.update(data)
    revision = digest.hexdigest()
    if expected_revision is not None:
        if not isinstance(expected_revision, str) or re.fullmatch(r"[0-9a-f]{64}", expected_revision) is None:
            raise ValueError("Invalid source court revision.")
        if expected_revision != revision:
            raise ValueError("The source court changed after its preview was prepared. Reimport the source court before converting.")
    return data, descriptor, revision


def _decode_dds(data: bytes, descriptor: dict) -> Image.Image:
    buffer = BytesIO(data)
    image = None
    try:
        image = Image.open(buffer)
        if image.format != "DDS" or image.size != (descriptor["width"], descriptor["height"]):
            raise ValueError("The DDS decoder returned dimensions that do not match its validated header.")
        image.load()
        if image.mode == "RGBA":
            return image
        result = image.convert("RGBA")
        image.close()
        return result
    except (OSError, SyntaxError, Image.DecompressionBombError) as error:
        if image is not None:
            image.close()
        raise ValueError("The selected DDS is damaged or its compression is not supported by the image decoder.") from error
    except Exception:
        if image is not None:
            image.close()
        raise
    finally:
        buffer.close()


def inspect_iff(path: Path, *, target: bool = False, selected: str | None = None) -> dict:
    path = Path(path)
    if not path.is_file():
        raise ValueError("Choose a ZIP-style NBA 2K court .iff file.")
    with open_iff(path) as archive:
        _unique_entries(archive)
        textures = [item for name in archive.namelist() if name.lower().endswith(".dds")
                    if (item := _dds_info(archive, name)) is not None]
        layout_comment = archive.comment
    if target:
        textures = [item for item in textures if item["width"] == 8192 and item["height"] == 4096
                    and item["format"] in DXGI_FORMATS.values()]
        if not textures:
            raise ValueError("This IFF has no replaceable 8192 x 4096 court DDS. Choose a known-working 2K27 floor IFF.")
    elif not textures:
        raise ValueError("No readable DDS textures were found in this IFF.")
    textures.sort(key=lambda item: (
        item["width"] / item["height"] == 2,
        "court" in item["name"].lower() or "big" in item["name"].lower(),
        item["width"] * item["height"],
    ), reverse=True)
    chosen = next((item for item in textures if item["name"] == selected), None) if selected else textures[0]
    if chosen is None:
        raise ValueError("The selected texture is no longer available. Reimport the source court.")
    result = {"path": str(path), "textures": textures, "selected": chosen["name"]}
    if not target:
        recorded = _recorded_layout(layout_comment, chosen)
        if recorded:
            with open_iff(path) as archive:
                _data, descriptor, revision = _read_dds_data(archive, chosen["name"])
                if archive.comment != layout_comment or descriptor != chosen:
                    raise ValueError("The source court changed during inspection. Reimport it.")
            result["sourceBounds"] = recorded
        else:
            with _import_pixels(path, chosen["name"], inspection=(chosen, layout_comment)) as (image, revision):
                result["sourceBounds"] = detect_court_edges(image)
        result["sourceRevision"] = revision
        result["alignmentSource"] = "recorded-game-uv" if recorded else "image-detection"
    return result


def find_nba2k27_root(game_root: Path | None = None) -> Path:
    candidates = []
    if game_root:
        explicit = _usable_game_root(game_root)
        if explicit is not None:
            return explicit
        raise FileNotFoundError("NBA 2K27 was not found in the selected folder. Choose the folder containing mod.exe and manifest.")
    try:
        saved = read_document(GAME_SETTINGS_PATH, max_bytes=MAX_GAME_SETTINGS_BYTES)
        path = _discovery_path(saved.get("nba2k27Root")) if isinstance(saved, dict) else None
        if path is not None:
            candidates.append(path)
    except (OSError, ValueError, TypeError):
        pass
    environment = _discovery_path(os.environ.get("NBA2K27_ROOT"))
    if environment is not None:
        candidates.append(environment)
    for candidate in candidates:
        resolved = _usable_game_root(candidate)
        if resolved is not None:
            return resolved
    steam_roots = [
        Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Steam",
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Steam",
    ]
    if os.name == "nt":
        import winreg
        for hive, key_name, value_name in (
            (winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
            (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        ):
            try:
                with winreg.OpenKey(hive, key_name) as key:
                    path = _discovery_path(winreg.QueryValueEx(key, value_name)[0])
                    if path is not None:
                        steam_roots.append(path)
            except (OSError, ValueError, TypeError):
                pass
    for steam_root in steam_roots:
        libraries = [steam_root]
        try:
            library_data = _read_steam_libraries(steam_root / "steamapps" / "libraryfolders.vdf")
            folders = library_data.get("libraryfolders", {})
            if not isinstance(folders, dict):
                raise ValueError("Steam libraryfolders must be an object.")
            for key, value in folders.items():
                if str(key).isdigit():
                    library = value.get("path") if isinstance(value, dict) else value
                    path = _discovery_path(library)
                    if path is not None:
                        libraries.append(path)
        except (OSError, ValueError):
            pass
        for library in libraries:
            resolved = _usable_game_root(library / "steamapps" / "common" / "NBA 2K27")
            if resolved is not None:
                return resolved
    raise FileNotFoundError(
        "NBA 2K27 was not found. Install it through Steam or set "
        "NBA2K27_ROOT to its game folder."
    )


def _discovery_path(value):
    if (not isinstance(value, str) or not value.strip() or len(value) > 32768
            or "\0" in value or any(0xD800 <= ord(character) <= 0xDFFF for character in value)):
        return None
    return Path(value)


def _usable_game_root(path):
    try:
        path = Path(path)
        if (path / "mod.exe").is_file() and (path / "manifest").is_file():
            return path.resolve()
    except (OSError, ValueError, TypeError, RuntimeError):
        pass
    return None


def _read_steam_libraries(path):
    with Path(path).open("rb") as stream:
        if os.fstat(stream.fileno()).st_size > MAX_STEAM_LIBRARIES_BYTES:
            raise ValueError("Steam libraries exceed the 1 MiB size limit.")
        data = stream.read(MAX_STEAM_LIBRARIES_BYTES + 1)
    if len(data) > MAX_STEAM_LIBRARIES_BYTES:
        raise ValueError("Steam libraries exceed the 1 MiB size limit.")
    return _parse_vdf(data.decode("utf-8-sig"))


def _parse_vdf(text: str) -> dict:
    if not isinstance(text, str) or len(text) > MAX_STEAM_LIBRARIES_BYTES or len(text.encode("utf-8")) > MAX_STEAM_LIBRARIES_BYTES:
        raise ValueError("Steam libraries exceed the 1 MiB size limit.")
    tokens = []
    offset = 0
    while offset < len(text):
        match = _VDF_TOKEN.match(text, offset)
        if match is None:
            raise ValueError("Invalid token or unclosed quoted string in Steam libraries.")
        token = match.group(); offset = match.end()
        if token.isspace() or token.startswith("//"):
            continue
        tokens.append(token)
        if len(tokens) > MAX_STEAM_LIBRARIES_TOKENS:
            raise ValueError("Steam libraries exceed the token limit.")
    cursor = 0

    def read_object(nested=False, depth=1):
        nonlocal cursor
        if depth > MAX_STEAM_LIBRARIES_DEPTH:
            raise ValueError("Steam library nesting exceeds the 32-level limit.")
        result = {}
        while cursor < len(tokens):
            token = tokens[cursor]
            cursor += 1
            if token == "}":
                if not nested:
                    raise ValueError("Unexpected closing brace in Steam libraries.")
                return result
            if token == "{":
                raise ValueError("Missing Steam library key.")
            key = json.loads(token) if token.startswith('"') else token
            if cursor >= len(tokens):
                raise ValueError("Missing Steam library value.")
            value = tokens[cursor]
            cursor += 1
            if value == "}":
                raise ValueError("Missing Steam library value.")
            result[key] = read_object(True, depth + 1) if value == "{" else json.loads(value) if value.startswith('"') else value
        if nested:
            raise ValueError("Unclosed Steam library object.")
        return result

    return read_object()


def _write_json_atomic(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                         prefix=path.name + ".", suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(value, stream, separators=(",", ":"), allow_nan=False)
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def _scene_document(data: bytes) -> tuple[dict, bool]:
    if not isinstance(data, (bytes, bytearray)):
        raise ValueError("The floor scene must be UTF-8 bytes.")
    if len(data) > MAX_SCENE_BYTES:
        raise ValueError("The floor scene exceeds the 16 MB size limit.")
    payload = data.removeprefix(b"\xef\xbb\xbf").strip()
    wrapped = payload.startswith(b"{")
    try:
        document = parse_document(payload if wrapped else b"{" + payload + b"}", max_bytes=MAX_SCENE_BYTES + 2, encoding="utf-8")
    except ValueError as error:
        raise ValueError("Invalid floor scene: " + str(error)) from error
    if not isinstance(document, dict):
        raise ValueError("The floor scene must be an object.")
    _validate_scene_containers(document)
    return document, wrapped


def _scene_mapping(container, key):
    value = container.get(key, {})
    if not isinstance(value, dict):
        raise ValueError("The floor scene's " + key + " must be an object.")
    return value


def _validate_scene_containers(document):
    for scene in document.values():
        if not isinstance(scene, dict):
            continue
        for model in _scene_mapping(scene, "Model").values():
            if not isinstance(model, dict):
                raise ValueError("The floor scene's Model entries must be objects.")
            primitives = model.get("Prim", [])
            if not isinstance(primitives, list):
                raise ValueError("The floor scene's Model.Prim must be an array.")
            for primitive in primitives:
                if not isinstance(primitive, dict):
                    raise ValueError("The floor scene's Model.Prim entries must be objects.")
                if "Mesh" in primitive and not isinstance(primitive["Mesh"], str):
                    raise ValueError("The floor scene's primitive Mesh must be a name.")
                if primitive.get("Material") is not None and not isinstance(primitive["Material"], str):
                    raise ValueError("The floor scene's primitive Material must be a name or null.")
        for key in ("Material", "Effect"):
            for item in _scene_mapping(scene, key).values():
                if not isinstance(item, dict):
                    raise ValueError("The floor scene's " + key + " entries must be objects.")
                for field in ("Resource", "Parameter"):
                    _scene_mapping(item, field)


def _serialize_scene_document(document, wrapped):
    serialized = json.dumps(document, ensure_ascii=True, indent=2, allow_nan=False)
    data = (serialized if wrapped else serialized[1:-1].strip()).encode("utf-8")
    if len(data) > MAX_SCENE_BYTES:
        raise ValueError("The prepared floor scene exceeds the 16 MB size limit.")
    return data


def clean_floor_scene(data: bytes) -> bytes:
    document, wrapped = _scene_document(data)
    scenes = document.values()
    surfaces = 0
    for scene in scenes:
        if not isinstance(scene, dict):
            continue
        for model in scene.get("Model", {}).values():
            primitives = model.get("Prim", [])
            kept = [prim for prim in primitives if "line" not in str(prim.get("Mesh", "")).casefold()]
            surfaces += sum("full_court_floor" in str(prim.get("Mesh", "")).casefold() for prim in kept)
            if primitives:
                model["Prim"] = kept
    if not surfaces:
        raise ValueError("The export base has no recognized full-court floor surface.")
    return _serialize_scene_document(document, wrapped)


def _check_base_storage(destination, *, sources=(), game_root=None):
    if destination.parent.resolve() != destination.parent.absolute():
        raise ValueError("Stock export base cache cannot use linked folders.")
    if game_root is not None and destination.resolve().is_relative_to(game_root.resolve()):
        raise ValueError("Store the export base cache outside the game installation; source assets must stay unchanged.")
    for path in (destination, destination.with_name(destination.name + ".lock")):
        ensure_new_export(path, *sources)
        _ordinary_file_info(path, "Stock export base cache")


def _base_cache_snapshot(destination):
    return _file_snapshot(destination, max_bytes=MAX_BASE_ARCHIVE_BYTES, label="Stock export base cache", limit_label="archive size limit")


def _clean_base_archive(destination: Path, *, sources=(), validate_lock=None) -> dict:
    destination = Path(destination).absolute()
    sources = tuple(Path(path) for path in sources)
    _check_base_storage(destination, sources=sources)
    expected = _base_cache_snapshot(destination)
    if expected is None:
        raise FileNotFoundError("The stock export base disappeared before cleaning: " + str(destination))

    def validate():
        if validate_lock is not None:
            validate_lock()
        _check_base_storage(destination, sources=sources)
        if _base_cache_snapshot(destination) != expected:
            raise ValueError("The stock export base cache changed while preparing. Retry to use the current cache.")

    with staged_export(destination, sources=sources, validate=validate) as staged:
        identity = staging_identity(staged)
        with open_iff(destination) as original:
            if sum(item.file_size for item in original.infolist()) > MAX_BASE_ARCHIVE_BYTES:
                raise ValueError("The stock export base exceeds its inflated archive size limit.")
            scene = clean_floor_scene(read_iff_scene(original))
            with staged.open("r+b") as stream:
                owned_staging(staged, identity, info=os.fstat(stream.fileno()))
                stream.truncate(0)
                with ZipFile(stream, "w") as cleaned:
                    for item in original.infolist():
                        _copy_iff_entry(original, cleaned, item, scene if item.filename == "level_floor.SCNE" else None)
                    cleaned.comment = BASE_VERSION
        with open_iff(staged) as checked:
            if staged.stat().st_size > MAX_BASE_ARCHIVE_BYTES or sum(item.file_size for item in checked.infolist()) > MAX_BASE_ARCHIVE_BYTES:
                raise ValueError("The prepared export base exceeds its archive size limit.")
            if checked.testzip() is not None or not _entry_matches(checked, "level_floor.SCNE", scene):
                raise ValueError("The prepared export base failed validation.")
        result = inspect_iff(staged, target=True)
    result["path"] = str(destination)
    return result


def cached_2k27_base(destination: Path) -> dict | None:
    destination = Path(destination)
    try:
        with open_iff(destination) as archive:
            if archive.comment != BASE_VERSION:
                return None
        return inspect_iff(destination, target=True)
    except (FileNotFoundError, OSError, ValueError, BadZipFile):
        return None


@contextmanager
def _base_file_lock(destination: Path, *, timeout=240):
    destination = Path(destination).absolute()
    # Non-strict Windows resolution can retain an extended prefix during concurrent mkdir.
    ancestor = destination.parent
    missing = []
    while True:
        try:
            resolved_parent = ancestor.resolve(strict=True).joinpath(*reversed(missing))
            break
        except FileNotFoundError:
            if ancestor.parent == ancestor:
                raise
            missing.append(ancestor.name)
            ancestor = ancestor.parent
    if resolved_parent != destination.parent:
        raise ValueError(f"Stock-data lock cannot use linked folders: {destination.parent} resolves to {resolved_parent}.")
    lock = destination.with_name(destination.name + ".lock")
    _ordinary_file_info(lock, "Stock-data lock")
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.parent.resolve(strict=True) != destination.parent:
        raise ValueError("Stock-data lock cannot use linked folders created during preparation.")
    with lock.open("a+b") as stream:
        opened = os.fstat(stream.fileno())
        identity = opened.st_dev, opened.st_ino

        def validate():
            info = _ordinary_file_info(lock, "Stock-data lock")
            opened = os.fstat(stream.fileno())
            if info is None or (info.st_dev, info.st_ino) != identity or (opened.st_dev, opened.st_ino) != identity or opened.st_nlink != 1:
                raise ValueError("The stock-data lock changed while waiting or preparing; its current file has been retained.")

        validate()
        if stream.tell() == 0:
            stream.write(b"0")
            stream.flush()
        deadline = time.monotonic() + timeout
        while True:
            validate()
            stream.seek(0)
            try:
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if time.monotonic() >= deadline:
                    raise RuntimeError("Another court operation is preparing stock data. Try again shortly.")
                time.sleep(0.1)
        try:
            validate()
            yield validate
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def _recovery_info_token(info):
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns


def _ordinary_file_info(path, label):
    try:
        info = path.lstat()
    except FileNotFoundError:
        return None
    if path.parent.resolve() != path.parent.absolute():
        raise ValueError(label + " cannot use linked folders; its current files have been retained.")
    if (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1
            or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024)):
        raise ValueError(label + " files must be ordinary unshared files; their current contents have been retained.")
    return info


def _file_snapshot(path, *, max_bytes, label, limit_label="file size limit"):
    info = _ordinary_file_info(path, label)
    if info is None:
        return None
    if info.st_size > max_bytes:
        raise ValueError(label + " exceeds its " + limit_label + "; its current file has been retained.")
    token = _recovery_info_token(info)
    digest = hashlib.sha256(); total = 0
    with path.open("rb", buffering=0) as stream:
        if _recovery_info_token(os.fstat(stream.fileno())) != token:
            raise ValueError(label + " changed while being opened; its current file has been retained.")
        while chunk := stream.read(min(1024 * 1024, max_bytes - total + 1)):
            total += len(chunk)
            if total > max_bytes:
                raise ValueError(label + " grew beyond its " + limit_label + "; its current file has been retained.")
            digest.update(chunk)
        final = os.fstat(stream.fileno())
    current = _ordinary_file_info(path, label)
    if current is None or _recovery_info_token(current) != token or _recovery_info_token(final) != token or total != info.st_size:
        raise ValueError(label + " changed during inspection; its current file has been retained.")
    return (*token, digest.hexdigest())


def _recovery_path_info(path):
    return _ordinary_file_info(path, "Interrupted extraction recovery")


def _recovery_file_snapshot(path, *, max_bytes=None):
    return _file_snapshot(path, max_bytes=MAX_RECOVERY_FILE_BYTES if max_bytes is None else max_bytes, label="Interrupted extraction file", limit_label="recovery size limit")


def _recovery_record(journal):
    before = _recovery_file_snapshot(journal, max_bytes=MAX_EXTRACTION_JOURNAL_BYTES)
    if before is None:
        raise ValueError("The interrupted extraction record is missing; its backup has been preserved.")
    try:
        state, revision = read_document_snapshot(journal, max_bytes=MAX_EXTRACTION_JOURNAL_BYTES)
    except ValueError as error:
        raise ValueError("The interrupted extraction record is invalid; its backup has been preserved.") from error
    if revision != before[-1] or _recovery_file_snapshot(journal, max_bytes=MAX_EXTRACTION_JOURNAL_BYTES) != before:
        raise ValueError("The interrupted extraction record changed during inspection; its backup has been preserved.")
    root = _discovery_path(state.get("gameRoot")) if isinstance(state, dict) else None
    restored = state.get("restoredRevision") if isinstance(state, dict) else None
    if (root is None or not root.is_absolute() or type(state.get("hadLoose")) is not bool
            or "restoredRevision" in state and (not state["hadLoose"] or not isinstance(restored, str) or re.fullmatch(r"[0-9a-f]{64}", restored) is None)):
        raise ValueError("The interrupted extraction record is invalid; its backup has been preserved.")
    return state, before


def _copy_file_snapshot(source, expected, target, *, max_bytes, label, allow_shared=False):
    with source.open("rb", buffering=0) as stream:
        info = os.fstat(stream.fileno())
        if _recovery_info_token(info) != expected[:4] or (not allow_shared and info.st_nlink != 1):
            raise ValueError(label + " source changed before copying; its current file has been retained.")
        digest = hashlib.sha256(); total = 0
        while chunk := stream.read(1024 * 1024):
            total += len(chunk)
            if total > max_bytes:
                raise ValueError(label + " source grew beyond its copy size limit.")
            target.write(chunk); digest.update(chunk)
        if _recovery_info_token(os.fstat(stream.fileno())) != expected[:4] or total != expected[2] or digest.hexdigest() != expected[-1]:
            raise ValueError(label + " source changed while copying; its current file has been retained.")
    target.flush(); os.fsync(target.fileno())
    target.seek(0)
    copied = hashlib.sha256(); total = 0
    while chunk := target.read(1024 * 1024):
        total += len(chunk)
        if total > expected[2]:
            raise ValueError(label + " copy has unexpected extra bytes; the original files have been retained.")
        copied.update(chunk)
    if total != expected[2] or copied.hexdigest() != expected[-1]:
        raise ValueError(label + " copy does not match its source; the original files have been retained.")
    return (*_recovery_info_token(os.fstat(target.fileno())), copied.hexdigest())


def _copy_recovery_file(source, expected, target):
    return _copy_file_snapshot(source, expected, target, max_bytes=MAX_RECOVERY_FILE_BYTES, label="The recovery")


def _preserve_recovery_floor(live, expected, directory, validate):
    failure = None
    with tempfile.NamedTemporaryFile(prefix="interrupted-floor-", suffix=".iff", dir=directory, delete=False) as stream:
        preserved = Path(stream.name)
        identity = _recovery_info_token(os.fstat(stream.fileno()))[:2]
        try:
            validate()
            owned_staging(preserved, identity, info=os.fstat(stream.fileno()))
            _copy_recovery_file(live, expected, stream)
            validate()
            owned_staging(preserved, identity)
        except BaseException as error:
            failure = error
    if failure is not None:
        cleanup_staging(preserved, failure, identity=identity)
        raise failure
    try:
        snapshot = _recovery_file_snapshot(preserved)
        if snapshot is None or snapshot[2] != expected[2] or snapshot[-1] != expected[-1]:
            raise ValueError("The preserved game floor does not match its source; the original files have been retained.")
        return preserved, snapshot
    except BaseException as error:
        cleanup_staging(preserved, error, identity=identity)
        raise


def _remove_recovery_file(path, expected, validate):
    for attempt in range(4):
        try:
            current = _recovery_file_snapshot(path)
            if current is None:
                return
            if current != expected:
                raise ValueError("An interrupted extraction cleanup target changed; it was retained: " + str(path))
            validate()
            if _recovery_file_snapshot(path) != expected:
                raise ValueError("An interrupted extraction cleanup target changed; it was retained: " + str(path))
            path.unlink()
            return
        except OSError as error:
            if os.name != "nt" or getattr(error, "winerror", None) not in (5, 32, 33) or attempt == 3:
                raise
            time.sleep(.025 * 2 ** attempt)


def _recover_base_extraction(destination: Path, game_root: Path | None = None) -> None:
    destination = Path(destination).absolute()
    held_path = destination.with_name(destination.name + ".held-loose")
    journal = destination.with_name(destination.name + ".extraction.json")
    if _recovery_path_info(held_path) is None and _recovery_path_info(journal) is None:
        return
    state, journal_snapshot = _recovery_record(journal)
    root = _usable_game_root(Path(state["gameRoot"]))
    if root is None:
        raise ValueError("The recorded game folder is unavailable; the interrupted extraction backup has been preserved.")
    live_path = root / "mods" / NBA2K27_BASE_ENTRY
    sources = (root / "mod.exe", root / "manifest", destination)
    ensure_new_export(live_path, held_path, journal, *sources)
    ensure_new_export(journal, held_path, live_path, *sources)
    held_snapshot = _recovery_file_snapshot(held_path)
    live_snapshot = _recovery_file_snapshot(live_path)
    restored = state.get("restoredRevision")
    if held_snapshot is not None and not state["hadLoose"]:
        raise ValueError("The interrupted extraction record and backup are inconsistent; both have been preserved.")
    if restored is not None:
        if live_snapshot is None or live_snapshot[-1] != restored:
            raise ValueError("The game floor changed after recovery; its record and backup have been preserved.")
        if held_snapshot is not None and held_snapshot[-1] != restored:
            raise ValueError("The interrupted extraction backup changed after recovery; it has been preserved.")
    elif state["hadLoose"] and held_snapshot is None:
        raise ValueError("The interrupted extraction backup is missing; its record and current game floor have been preserved.")

    preserved = None
    preserved_snapshot = None

    def validate():
        if (_recovery_record(journal)[1] != journal_snapshot
                or _recovery_file_snapshot(held_path) != held_snapshot
                or _recovery_file_snapshot(live_path) != live_snapshot):
            raise ValueError("Interrupted extraction files changed while recovering; their current contents have been preserved.")
        if preserved is not None and _recovery_file_snapshot(preserved) != preserved_snapshot:
            raise ValueError("The preserved game floor changed while recovering; the original backup has been retained.")
        ensure_new_export(live_path, held_path, journal, *sources)
        ensure_new_export(journal, held_path, live_path, *sources)

    try:
        if restored is None:
            payload = None
            if held_snapshot is not None:
                state = dict(state, restoredRevision=held_snapshot[-1])
                payload = json.dumps(state, separators=(",", ":"), allow_nan=False).encode("utf-8")
                if len(payload) > MAX_EXTRACTION_JOURNAL_BYTES:
                    raise ValueError("The interrupted extraction record has no room for a completion marker; its backup has been preserved.")
            if live_snapshot is not None and (held_snapshot is None or live_snapshot[-1] != held_snapshot[-1]):
                preserved, preserved_snapshot = _preserve_recovery_floor(live_path, live_snapshot, destination.parent, validate)
            if held_snapshot is not None:
                if live_snapshot is None or live_snapshot[-1] != held_snapshot[-1]:
                    with staged_export(live_path, sources=(held_path, journal, *sources), validate=validate) as staging:
                        identity = staging_identity(staging)
                        with staging.open("r+b") as stream:
                            owned_staging(staging, identity, info=os.fstat(stream.fileno()))
                            _copy_recovery_file(held_path, held_snapshot, stream)
                    live_snapshot = _recovery_file_snapshot(live_path)
                    if live_snapshot is None or live_snapshot[-1] != held_snapshot[-1]:
                        raise ValueError("The restored game floor changed before recording completion; the backup has been retained.")
                # Keep the backup until an exact completion marker makes cleanup retryable.
                with staged_export(journal, sources=(held_path, live_path, *sources), validate=validate) as staging:
                    staging.write_bytes(payload)
                state, journal_snapshot = _recovery_record(journal)
                if journal_snapshot[-1] != hashlib.sha256(payload).hexdigest():
                    raise ValueError("The recovery completion record changed; the backup has been retained.")
        if held_snapshot is not None:
            _remove_recovery_file(held_path, held_snapshot, validate)
            held_snapshot = None
        _remove_recovery_file(journal, journal_snapshot, validate)
    except BaseException as error:
        if preserved is not None:
            error.add_note("The current game floor was copied without moving it; the copy is retained at " + str(preserved))
        raise


@contextmanager
def extracted_stock_floor(game_root: Path):
    """Expand the stock floor in scratch space, leaving installed mods untouched."""
    root = Path(game_root).resolve()
    with tempfile.TemporaryDirectory(prefix="court-stock-floor-") as folder:
        temporary = Path(folder)
        found = False
        # Full manifests repeat a small set of archive paths across many rows.
        archive_paths = {}
        with (root / "manifest").open("r", encoding="utf-8", newline="") as source, (temporary / "manifest").open("w", encoding="utf-8", newline="") as target:
            writer = csv.writer(target, lineterminator="\n")
            for row in csv.reader(source):
                if len(row) == 4:
                    if row[0].replace("\\", "/").casefold() == NBA2K27_BASE_ENTRY.casefold():
                        if found:
                            raise ValueError("The stock floor has duplicate manifest entries.")
                        if int(row[2]) < 0 or int(row[3]) <= 0:
                            raise ValueError("The stock floor has an invalid manifest range.")
                        found = True
                    archive = row[1]
                    if archive not in archive_paths:
                        archive_paths[archive] = str((root / archive).resolve())
                    row[1] = archive_paths[archive]
                writer.writerow(row)
        if not found:
            raise ValueError("The NBA 2K27 manifest does not contain the stock export floor.")
        archive_path = temporary / "mods" / NBA2K27_BASE_ENTRY
        for _attempt in range(2):
            if not archive_path.resolve().is_relative_to(temporary.resolve()):
                raise ValueError("Stock extraction returned a path outside its scratch folder.")
            archive_path.unlink(missing_ok=True)
            result = run_tool(
                [str(root / "mod.exe"), NBA2K27_BASE_ENTRY], cwd=str(temporary),
                timeout=180, label="NBA 2K27 extractor",
            )
            if not archive_path.resolve().is_relative_to(temporary.resolve()):
                raise ValueError("Stock extraction returned a path outside its scratch folder.")
            if result.returncode == 0 and is_zipfile(archive_path):
                break
        else:
            detail = result.stdout[-1200:] if result.stdout else "No output was produced."
            raise RuntimeError("NBA 2K27 could not expand the stock floor. " + detail)
        yield archive_path


def _check_game_settings_storage(path):
    if path.parent.resolve() != path.parent.absolute():
        raise ValueError("Game installation settings cannot use linked folders.")
    for candidate in (path, path.with_name(path.name + ".lock")):
        try:
            info = candidate.lstat()
        except FileNotFoundError:
            continue
        if (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1
                or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024)):
            raise ValueError("Game installation settings and their lock must be ordinary unshared files.")


def _game_settings_snapshot(path):
    _check_game_settings_storage(path)
    try:
        document, revision = read_document_snapshot(path, max_bytes=MAX_GAME_SETTINGS_BYTES)
    except FileNotFoundError:
        return {}, None
    except ValueError as error:
        raise ValueError("The existing game installation settings are damaged or oversized; repair or rename their file before saving a new folder: " + str(path)) from error
    if not isinstance(document, dict):
        raise ValueError("Game installation settings must be an object; their file was left unchanged: " + str(path))
    return document, revision


def _remember_game_root(root, *, required=False):
    path = Path(GAME_SETTINGS_PATH).absolute()
    root = Path(root).resolve()
    sources = (root / "mod.exe", root / "manifest")
    try:
        ensure_new_export(path, *sources)
        ensure_new_export(path.with_name(path.name + ".lock"), *sources)
        _check_game_settings_storage(path)
        if not _GAME_SETTINGS_LOCK.acquire(timeout=10):
            raise RuntimeError("Another court operation is saving the game folder. Try again shortly.")
        try:
            _check_game_settings_storage(path)
            with _base_file_lock(path, timeout=10):
                document, expected = _game_settings_snapshot(path)
                if document.get("nba2k27Root") == str(root):
                    return True
                document["nba2k27Root"] = str(root)
                payload = json.dumps(document, separators=(",", ":"), allow_nan=False).encode("utf-8")
                if len(payload) > MAX_GAME_SETTINGS_BYTES:
                    raise ValueError("Game installation settings exceed the 1 MiB size limit.")

                def validate():
                    _current, revision = _game_settings_snapshot(path)
                    if revision != expected:
                        raise ValueError("Game installation settings changed while saving. Retry to use the current settings.")

                try:
                    with staged_export(path, sources=sources, validate=validate) as staging:
                        staging.write_bytes(payload)
                except Exception:
                    # A completed rename can precede an OS wrapper's reported error.
                    try:
                        _current, revision = _game_settings_snapshot(path)
                        if revision == hashlib.sha256(payload).hexdigest():
                            return True
                    except (OSError, ValueError):
                        pass
                    raise
                return True
        finally:
            _GAME_SETTINGS_LOCK.release()
    except (OSError, ValueError, RuntimeError):
        if required:
            raise
        return False


def prepare_2k27_base(destination: Path, game_root: Path | None = None) -> dict:
    """Expand a stock 2K27 MyTEAM floor and cache its full-court DDS carrier."""
    destination = Path(destination).absolute()
    selected_root = find_nba2k27_root(game_root) if game_root else None
    available_root = selected_root
    discovery_error = None
    if available_root is None:
        try:
            available_root = find_nba2k27_root()
        except FileNotFoundError as error:
            discovery_error = error
    sources = () if available_root is None else (available_root / "mod.exe", available_root / "manifest", available_root / "mods" / NBA2K27_BASE_ENTRY)
    _check_base_storage(destination, sources=sources, game_root=available_root)
    with _BASE_LOCK, _base_file_lock(destination) as validate_lock:
        _recover_base_extraction(destination, game_root)
        _check_base_storage(destination, sources=sources, game_root=available_root)
        cached = cached_2k27_base(destination)
        if cached:
            validate_lock()
            if selected_root is not None:
                _remember_game_root(selected_root, required=True)
            return cached
        if destination.is_file() and is_zipfile(destination):
            inspect_iff(destination, target=True)
            result = _clean_base_archive(destination, sources=sources, validate_lock=validate_lock)
            if selected_root is not None:
                _remember_game_root(selected_root, required=True)
            return result
        if available_root is None:
            raise discovery_error or FileNotFoundError("NBA 2K27 was not found; choose its game installation before preparing a stock base.")
        root = available_root
        sources = (root / "mod.exe", root / "manifest", root / "mods" / NBA2K27_BASE_ENTRY)
        _check_base_storage(destination, sources=sources, game_root=root)
        expected = _base_cache_snapshot(destination)

        def validate():
            validate_lock()
            _check_base_storage(destination, sources=sources, game_root=root)
            if _base_cache_snapshot(destination) != expected:
                raise ValueError("The stock export base cache changed while preparing. Retry to use the current cache.")

        with extracted_stock_floor(root) as archive_path:
            result = _clean_base_archive(archive_path, sources=sources)
            prepared = _base_cache_snapshot(archive_path)
            with staged_export(destination, sources=(*sources, archive_path), validate=validate) as staged:
                identity = staging_identity(staged)
                with staged.open("r+b") as stream:
                    owned_staging(staged, identity, info=os.fstat(stream.fileno()))
                    _copy_file_snapshot(archive_path, prepared, stream, max_bytes=MAX_BASE_ARCHIVE_BYTES, label="Stock export base")
                if _base_cache_snapshot(archive_path) != prepared:
                    raise ValueError("The prepared stock archive changed while copying; the previous cache has been retained.")
                inspected = inspect_iff(staged, target=True)
                if {key: value for key, value in inspected.items() if key != "path"} != {key: value for key, value in result.items() if key != "path"}:
                    raise ValueError("The prepared stock archive changed during validation; the previous cache has been retained.")
        result["path"] = str(destination)
        _remember_game_root(root, required=selected_root is not None)
        return result


def read_dds(path: Path, name: str, *, expected_revision: str | None = None) -> Image.Image:
    with open_iff(path) as archive:
        data, descriptor, _revision = _read_dds_data(archive, name, expected_revision)
    return _decode_dds(data, descriptor)


@contextmanager
def _import_pixels(path: Path, name: str, *, expected_revision: str | None = None, inspection=None):
    with open_iff(path) as archive:
        data, descriptor, revision = _read_dds_data(archive, name, expected_revision)
        if inspection is not None and (descriptor != inspection[0] or archive.comment != inspection[1]):
            raise ValueError("The source court changed during inspection. Reimport it.")
    # Internal consumers only read pixels. Holding this lock through their transform
    # prevents eviction/close while borrowed, without copying another full DDS image.
    with _DDS_IMAGE_CACHE_LOCK:
        image = _DDS_IMAGE_CACHE.get(revision)
        if image is not None:
            _DDS_IMAGE_CACHE.move_to_end(revision)
            del data
            yield image, revision
            return
        image = _decode_dds(data, descriptor)
        del data
        size = image.width * image.height * 4
        retained = size <= _DDS_IMAGE_CACHE_BYTES and _DDS_IMAGE_CACHE_LIMIT > 0
        if retained:
            used = sum(item.width * item.height * 4 for item in _DDS_IMAGE_CACHE.values())
            while _DDS_IMAGE_CACHE and (len(_DDS_IMAGE_CACHE) >= _DDS_IMAGE_CACHE_LIMIT or used + size > _DDS_IMAGE_CACHE_BYTES):
                _, evicted = _DDS_IMAGE_CACHE.popitem(last=False)
                used -= evicted.width * evicted.height * 4
                evicted.close()
            _DDS_IMAGE_CACHE[revision] = image
        try:
            yield image, revision
        finally:
            if not retained:
                image.close()


def _visible_court_edges(sample: Image.Image) -> list[int] | None:
    width, height = sample.size
    if width < 40 or height < 24:
        return None
    blurred = sample.filter(ImageFilter.GaussianBlur(.7))
    pixels = blurred.load()

    def strength(position, start, end, horizontal):
        contrasts = []
        for cross in range(start, end):
            first = pixels[cross, position - 2] if horizontal else pixels[position - 2, cross]
            last = pixels[cross, position + 2] if horizontal else pixels[position + 2, cross]
            contrasts.append(max(abs(a - b) for a, b in zip(first, last)))
        support = sum(value >= 28 for value in contrasts) / len(contrasts)
        return support, sum(contrasts) / len(contrasts)

    def edge(dimension, cross_dimension, low, high, horizontal, outermost_first):
        start, end = round(cross_dimension * .2), round(cross_dimension * .8)
        candidates = []
        run = []
        for position in range(max(2, round(dimension * low)), min(dimension - 2, round(dimension * high))):
            support, contrast = strength(position, start, end, horizontal)
            if support >= (.75 if horizontal else .55):
                run.append((position, contrast))
            elif run:
                candidates.append(sum(p * weight for p, weight in run) / sum(weight for _, weight in run))
                run = []
        if run:
            candidates.append(sum(p * weight for p, weight in run) / sum(weight for _, weight in run))
        if not candidates:
            return None
        return round(candidates[0] if outermost_first else candidates[-1])

    # Long opposing edges distinguish the playing surface from a colored apron;
    # logos, arcs, and lettering do not provide four continuous rectangle edges.
    bounds = [edge(width, height, .025, .4, False, True),
              edge(height, width, .025, .4, True, True),
              edge(width, height, .6, .975, False, False),
              edge(height, width, .6, .975, True, False)]
    if any(value is None for value in bounds):
        return None
    left, top, right, bottom = bounds
    inset = 3
    if right - left <= 2 * inset or bottom - top <= 2 * inset:
        return None
    if any(strength(position, start, end, horizontal)[0] < (.7 if horizontal else .55) for position, start, end, horizontal in (
        (top, left + inset, right - inset, True), (bottom, left + inset, right - inset, True),
        (left, top + inset, bottom - inset, False), (right, top + inset, bottom - inset, False))):
        return None
    return bounds


def detect_court_edges(image: Image.Image) -> list[int]:
    scale = min(1, 768 / image.width, 384 / image.height)
    sample = image.resize((max(1, round(image.width * scale)), max(1, round(image.height * scale))),
                          Image.Resampling.BILINEAR).convert("RGB")
    width, height = sample.size
    visible = _visible_court_edges(sample)
    if visible:
        return [round(value * original / dimension) for value, original, dimension in zip(
            visible, (image.width, image.height, image.width, image.height), (width, height, width, height))]
    pixels = sample.convert("RGB").tobytes()
    occupied = [max(pixels[i:i + 3]) > 12 for i in range(0, len(pixels), 3)]
    rows = [y for y in range(height) if sum(occupied[y * width:(y + 1) * width]) > width * 0.38]
    if not rows:
        return [0, 0, image.width, image.height]
    top, bottom = min(rows), max(rows)
    columns = [x for x in range(width) if sum(occupied[y * width + x] for y in range(top + 2, bottom - 1))
               > max(1, bottom - top - 3) * 0.7]
    if not columns:
        return [0, 0, image.width, image.height]
    return [round(min(columns) * image.width / width), round(top * image.height / height),
            round((max(columns) + 1) * image.width / width), round((bottom + 1) * image.height / height)]


def _aligned_image(source: Image.Image, bounds: list[int], background: Path,
                   output_size: tuple[int, int], *, guide: bool = False, geometry: dict | None = None,
                   background_revision: str | None = None) -> Image.Image:
    left, top, right, bottom = (int(value) for value in bounds)
    if not (0 <= left < right <= source.width and 0 <= top < bottom <= source.height):
        raise ValueError("Court edges must stay inside the imported texture.")
    width, height = output_size
    target = target_court_bounds(output_size, geometry=geometry)
    tx1, ty1, tx2, ty2 = target
    scale_x = (right - left) / (tx2 - tx1)
    scale_y = (bottom - top) / (ty2 - ty1)
    matrix = (scale_x, 0, left - tx1 * scale_x, 0, scale_y, top - ty1 * scale_y)
    base = None
    try:
        with verified_asset_stream(background, background_revision) as stream, Image.open(stream) as base_file:
            validate_asset_image(base_file)
            with closing(base_file.convert("RGB")) as decoded:
                base = decoded.resize(output_size, Image.Resampling.BILINEAR)
        rgba = source if source.mode == "RGBA" else source.convert("RGBA")
        try:
            with closing(rgba.transform(output_size, Image.Transform.AFFINE, matrix,
                                        resample=Image.Resampling.BICUBIC, fillcolor=(0, 0, 0, 0))) as overlay:
                with closing(overlay.getchannel("A")) as alpha:
                    base.paste(overlay, (0, 0), alpha)
        finally:
            if rgba is not source:
                rgba.close()
        if guide:
            ImageDraw.Draw(base).rectangle(target, outline="#12A88B", width=max(2, width // 500))
        return base
    except BaseException:
        if base is not None:
            base.close()
        raise


def render_texture(source_path: Path, texture_name: str, bounds: list[int],
                   background: Path, output_path: Path, *, preview: bool = False, geometry: dict | None = None,
                   protected_sources=(), source_revision: str | None = None, background_revision: str | None = None,
                   publication_receipt: dict | None = None) -> Path:
    sources = (Path(source_path), Path(background), *protected_sources)
    ensure_new_export(output_path, *sources)
    output_size = (1200, 600) if preview else OUTPUT_SIZE
    with _import_pixels(source_path, texture_name, expected_revision=source_revision) as (source, _revision):
        result = _aligned_image(source, bounds, background, output_size, guide=preview, geometry=geometry, background_revision=background_revision)
    output_path = Path(output_path)
    pending_receipt = None
    staged_path = None
    def validate_preview():
        if pending_receipt is not None:
            validate_receipt(staged_path, pending_receipt)
    with closing(result), staged_export(output_path, sources=sources,
            writable_stream=publication_receipt is not None,
            validate=validate_preview, validate_cleanup=validate_preview) as staged:
        staged_path = Path(staged.name) if publication_receipt is not None else staged
        result.save(staged, format="PNG", compress_level=3)
        if publication_receipt is not None:
            pending_receipt = stream_receipt(staged)
    if publication_receipt is not None:
        publication_receipt.update(pending_receipt)
    return output_path


def _export_input_revision(path: Path, stream, *, max_bytes, label, limit_label):
    position = stream.tell()
    try:
        info = os.fstat(stream.fileno())
        token = _recovery_info_token(info)
        if not stat.S_ISREG(info.st_mode) or info.st_size > max_bytes:
            raise ValueError("The " + label + " exceeds its " + limit_label + " or is not an ordinary file.")
        if _recovery_info_token(path.stat()) != token:
            raise ValueError("The " + label + " changed while being opened. Retry the export.")
        stream.seek(0)
        digest = hashlib.sha256()
        total = 0
        while chunk := stream.read(min(1024 * 1024, max_bytes - total + 1)):
            total += len(chunk)
            if total > max_bytes:
                raise ValueError("The " + label + " grew beyond its " + limit_label + ".")
            digest.update(chunk)
        if (_recovery_info_token(os.fstat(stream.fileno())) != token
                or _recovery_info_token(path.stat()) != token or total != info.st_size):
            raise ValueError("The " + label + " changed during validation. Retry the export.")
        return (*token, digest.hexdigest())
    finally:
        stream.seek(position)


def _export_base_revision(path: Path, stream):
    return _export_input_revision(path, stream, max_bytes=MAX_BASE_ARCHIVE_BYTES,
                                 label="export base", limit_label="archive size limit")


@contextmanager
def _pinned_flattened_png(path: Path, copied_path: Path):
    label = "flattened court texture"
    with path.open("rb", buffering=0) as source:
        expected = _export_input_revision(path, source, max_bytes=MAX_ASSET_BYTES,
                                          label=label, limit_label="512 MB file size limit")
        with copied_path.open("x+b", buffering=0) as target:
            copied = _copy_file_snapshot(path, expected, target, max_bytes=MAX_ASSET_BYTES,
                                         label="The " + label, allow_shared=True)

        def validate():
            current = _export_input_revision(path, source, max_bytes=MAX_ASSET_BYTES,
                                             label=label, limit_label="512 MB file size limit")
            if current != expected:
                raise ValueError("The flattened court texture changed while converting. Retry the export.")
            if _file_snapshot(copied_path, max_bytes=MAX_ASSET_BYTES, label="Private flattened court texture") != copied:
                raise ValueError("The private flattened court texture changed while converting. Retry the export.")

        validate()
        try:
            with Image.open(copied_path) as image:
                if image.format != "PNG" or image.size != OUTPUT_SIZE:
                    raise ValueError("The flattened court texture must be an 8192 x 4096 PNG image.")
                image.verify()
        except (OSError, SyntaxError, Image.DecompressionBombError) as error:
            raise ValueError("The flattened court PNG is damaged or incomplete. Retry the export.") from error
        validate()
        yield validate


@contextmanager
def _pinned_export_base(path: Path):
    info = path.stat()
    if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_BASE_ARCHIVE_BYTES:
        raise ValueError("The export base exceeds its archive size limit or is not an ordinary file.")
    with path.open("rb", buffering=0) as stream:
        expected = _export_base_revision(path, stream)

        def validate():
            if _export_base_revision(path, stream) != expected:
                raise ValueError("The export base changed while converting. Retry the export to use the current base.")

        with _read_iff_archive(stream) as archive:
            validate()
            if sum(item.file_size for item in archive.infolist()) > MAX_BASE_ARCHIVE_BYTES:
                raise ValueError("The export base exceeds its inflated archive size limit.")
            yield archive, validate


def package_png_into_iff(
    png_path: Path,
    target_iff: Path,
    target_texture: str,
    output_path: Path,
    texconv: Path,
    *,
    scene_override: bytes | None = None,
    native_layout: bool = False,
    geometry: dict | None = None,
    protected_sources=(),
) -> Path:
    png_path = Path(png_path)
    target_iff = Path(target_iff)
    output_path = Path(output_path)
    sources = (png_path, target_iff, Path(texconv), *protected_sources)
    ensure_new_export(output_path, *sources)
    if scene_override is not None:
        if not isinstance(scene_override, (bytes, bytearray)):
            raise ValueError("The replacement floor scene must be UTF-8 bytes.")
        if len(scene_override) > MAX_SCENE_BYTES:
            raise ValueError("The replacement floor scene exceeds the 16 MB size limit.")
        scene_override = bytes(scene_override)
        scene, _wrapped = _scene_document(scene_override)
        if not any("full_court_floor" in primitive.get("Mesh", "").casefold() and "line" not in primitive.get("Mesh", "").casefold()
                   for value in scene.values() if isinstance(value, dict)
                   for model in value.get("Model", {}).values() for primitive in model.get("Prim", [])):
            raise ValueError("The replacement floor scene has no recognized full-court floor surface.")
    with _pinned_export_base(target_iff) as (original, validate_base):
        inspected = inspect_iff(target_iff, target=True)
        selected = next((item for item in inspected["textures"] if item["name"] == target_texture), None)
        if selected is None or target_texture not in original.namelist():
            raise ValueError("The selected target DDS is not a supported full-court texture.")
        if scene_override is not None and "level_floor.SCNE" not in original.namelist():
            raise ValueError("The export base has no floor scene to update.")
        validate_base()
        if not png_path.is_file():
            raise FileNotFoundError("The flattened court texture was not created.")
        if not Path(texconv).is_file():
            raise FileNotFoundError("The DDS converter is missing from the app tools folder.")

        with (
            tempfile.TemporaryDirectory(prefix="court-dds-") as folder,
            _pinned_flattened_png(png_path, Path(folder) / "court.png") as validate_png,
        ):
            temporary = Path(folder)
            source_png = temporary / "court.png"
            def validate_inputs():
                validate_base()
                validate_png()

            result = run_tool(
                [str(texconv), "-nologo", "-y", "-m", str(selected["mipmaps"] or 1),
                 "-f", selected["format"], "-o", str(temporary), str(source_png)],
                timeout=600, label="DDS converter",
            )
            dds = temporary / "court.DDS"
            if result.returncode or not dds.is_file():
                raise RuntimeError(f"DDS conversion failed: {(result.stdout + result.stderr)[-1200:]}")
            data = _read_encoded_dds(dds, selected)
            validate_inputs()
            inflated_size = sum(len(data) if item.filename == target_texture else
                                len(scene_override) if item.filename == "level_floor.SCNE" and scene_override is not None
                                else item.file_size for item in original.infolist())
            if inflated_size > MAX_BASE_ARCHIVE_BYTES:
                raise ValueError("The converted IFF exceeds its inflated archive size limit.")

            with staged_export(output_path, sources=sources, validate=validate_inputs, writable_stream=True) as stream:
                staged = Path(stream.name)
                info = os.fstat(stream.fileno())
                identity = info.st_dev, info.st_ino
                owned_staging(staged, identity, info=info)
                owned_staging(staged, identity)
                stream.truncate(0)
                with ZipFile(stream, "w") as converted:
                    for item in original.infolist():
                        owned_staging(staged, identity)
                        replacement = (data if item.filename == target_texture else
                                       scene_override if item.filename == "level_floor.SCNE" and scene_override is not None else None)
                        _copy_iff_entry(original, converted, item, replacement)
                    converted.comment = (LAYOUT_PREFIX + json.dumps({
                        "mapping": "game-uv", "texture": target_texture,
                        "size": list(OUTPUT_SIZE), "courtBounds": target_court_bounds(geometry=geometry),
                    }, separators=(",", ":")).encode("utf-8") if native_layout else original.comment)
                stream.flush()
                owned_staging(staged, identity)
                if os.fstat(stream.fileno()).st_size > MAX_BASE_ARCHIVE_BYTES:
                    raise ValueError("The converted IFF exceeds its archive size limit.")
                with open_iff(staged) as check:
                    if sum(item.file_size for item in check.infolist()) > MAX_BASE_ARCHIVE_BYTES:
                        raise ValueError("The converted IFF exceeds its inflated archive size limit.")
                    if (check.testzip() is not None or not _entry_matches(check, target_texture, data)
                            or scene_override is not None and not _entry_matches(check, "level_floor.SCNE", scene_override)):
                        raise RuntimeError("The converted IFF did not pass archive verification.")
    return output_path


def build_iff(source_path: Path, texture_name: str, bounds: list[int], background: Path,
              target_iff: Path, target_texture: str, output_path: Path, texconv: Path, *,
              scene_override: bytes | None = None, native_layout: bool = False, geometry: dict | None = None,
              protected_sources=(), source_revision: str | None = None, background_revision: str | None = None) -> Path:
    source_path, target_iff, output_path = Path(source_path), Path(target_iff), Path(output_path)
    sources = (source_path, target_iff, Path(background), Path(texconv), *protected_sources)
    ensure_new_export(output_path, *sources)
    with tempfile.TemporaryDirectory(prefix="court-iff-") as folder:
        temporary = Path(folder)
        png = render_texture(source_path, texture_name, bounds, background, temporary / "court.png", geometry=geometry,
                             source_revision=source_revision, background_revision=background_revision)
        return package_png_into_iff(
            png, target_iff, target_texture, output_path, texconv,
            scene_override=scene_override, native_layout=native_layout, geometry=geometry, protected_sources=sources,
        )
