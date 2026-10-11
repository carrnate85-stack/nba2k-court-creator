"""Convert extracted geometry to raw game buffers without changing its layout."""
from __future__ import annotations

from pathlib import Path, PurePosixPath
import struct

MAX_BUFFER_BYTES = 16 * 1024 * 1024
MAX_GEOMETRY_BYTES = 64 * 1024 * 1024


def read_buffer(archive, descriptor, game_root=None):
    size = descriptor.get("Size")
    if type(size) is not int or not 0 < size <= MAX_BUFFER_BYTES:
        raise ValueError("Floor geometry has an invalid buffer size.")
    name = descriptor.get("Binary")
    if not isinstance(name, str) or PurePosixPath(name).name != name or "\\" in name:
        raise ValueError("Floor geometry has an invalid buffer name.")
    entry = str(PurePosixPath(name).with_suffix(".bin")) if name.endswith(".gz") else name
    try:
        info = archive.getinfo(entry)
    except KeyError as error:
        raise ValueError("Floor geometry is missing " + entry) from error
    if info.file_size > MAX_BUFFER_BYTES:
        raise ValueError("Floor geometry exceeds the buffer size limit.")
    with archive.open(info) as stream:
        raw = stream.read(MAX_BUFFER_BYTES + 1)
    if raw.startswith(b"\x1f\x8b\x21"):
        if len(raw) < 24 or struct.unpack_from("<I", raw, len(raw) - 4)[0] != size:
            raise ValueError("Compressed floor geometry has an invalid length.")
        if game_root is None:
            from .court_import import find_nba2k27_root
            game_root = find_nba2k27_root()
        if not (Path(game_root) / "data/oodle/oo2core_9_win64.dll").is_file():
            raise ValueError("Select the NBA 2K27 installation to decode its floor geometry.")
        from tools.export_2k26_court_texture import oodle_decompress
        try:
            raw = oodle_decompress(Path(game_root), raw[16:-8], size)
        except SystemExit as error:
            raise ValueError("The installed game could not decode the floor geometry: " + str(error)) from error
    if len(raw) != size:
        raise ValueError("Floor geometry has an invalid decoded buffer length.")
    return entry, raw


def normalized_scene(archive, scene_bytes, game_root=None):
    from .court_import import _scene_document, _serialize_scene_document
    document, wrapped = _scene_document(scene_bytes)
    replacements = {}
    total = 0
    for scene in document.values():
        if not isinstance(scene, dict):
            continue
        for model in scene.get("Model", {}).values():
            streams = model.get("VertexStream", [])
            if not isinstance(streams, list):
                raise ValueError("Floor geometry has invalid vertex streams.")
            descriptors = [*streams]
            if model.get("IndexBuffer"):
                descriptors.append(model["IndexBuffer"])
            for descriptor in descriptors:
                if not isinstance(descriptor, dict):
                    raise ValueError("Floor geometry has an invalid buffer descriptor.")
                size = descriptor.get("Size")
                if type(size) is not int or size <= 0:
                    raise ValueError("Floor geometry has an invalid buffer size.")
                total += size
                if total > MAX_GEOMETRY_BYTES:
                    raise ValueError("Floor geometry exceeds the total size limit.")
                if "Stride" in descriptor:
                    stride = descriptor["Stride"]
                    if type(stride) is not int or not 1 <= stride <= 1024 or size % stride:
                        raise ValueError("Floor geometry has an invalid vertex stride.")
                entry, raw = read_buffer(archive, descriptor, game_root)
                if entry in replacements and replacements[entry] != raw:
                    raise ValueError("Floor geometry has conflicting buffer descriptors.")
                replacements[entry] = raw
                descriptor["Binary"] = entry
                descriptor.pop("CompressionMethod", None)
    replacements["level_floor.SCNE"] = _serialize_scene_document(document, wrapped)
    return replacements


def lightmap_transform(model, raw, vertex_indices=None):
    """Recover a stock floor's planar position-to-UV1 mapping, including packing."""
    import numpy as np
    streams = model.get("VertexStream", [])
    fmt = model.get("VertexFormat", {})
    uv = fmt.get("TEXCOORD1", {})
    if (len(streams) != 1 or streams[0].get("Stride") != 28
            or fmt.get("POSITION0", {}).get("Format") != "R32G32B32_FLOAT"
            or uv.get("Format") != "R16G16_SNORM" or uv.get("ByteOffset") != 16
            or len(raw) % 28 or len(raw) < 84):
        raise ValueError("The regular floor has an unsupported lightmap vertex layout.")
    scale, bias = np.array(uv.get("Scale", [])[:2]), np.array(uv.get("Offset", [])[:2])
    if scale.shape != (2,) or bias.shape != (2,) or not np.isfinite([scale, bias]).all() or (scale <= 0).any():
        raise ValueError("The regular floor has an invalid lightmap transform.")
    positions = np.array([struct.unpack_from("<fff", raw, i) for i in range(0, len(raw), 28)])
    packed = np.array([struct.unpack_from("<hh", raw, i + 16) for i in range(0, len(raw), 28)])
    if not np.isfinite(positions).all() or abs(positions[:, 1]).max() > .001:
        raise ValueError("The regular floor must be a finite flat surface.")
    design = np.column_stack((positions[:, 0], positions[:, 2], np.ones(len(positions))))
    uv_values = np.maximum(-1, packed / 32767) * scale + bias
    fit_design, fit_uvs = design, uv_values
    if vertex_indices is not None:
        ids = np.unique(vertex_indices)
        if len(ids) < 3 or ids.min() < 0 or ids.max() >= len(positions):
            raise ValueError("The regular floor has invalid lightmap surface indices.")
        fit_design, fit_uvs = design[ids], uv_values[ids]
    transform, _, rank, _ = np.linalg.lstsq(fit_design, fit_uvs, rcond=None)
    if rank != 3 or not np.isfinite(transform).all() or abs(fit_design @ transform - fit_uvs).max() > .00002:
        raise ValueError("The regular floor does not have a supported planar lightmap.")
    return transform, design, scale, bias


def remap_lightmap(model, raw, reference_model, reference_raw, reference_indices=None):
    """Replace only UV1; keep court positions and all artwork/detail bytes intact."""
    import numpy as np
    target, _design, _scale, _bias = lightmap_transform(reference_model, reference_raw, reference_indices)
    _source, design, scale, bias = lightmap_transform(model, raw)
    # The standard regular floor is the same footprint as the template. Never
    # scale the court or silently apply the practice arena's expanded geometry.
    for key in ("Min", "Max"):
        a, b = np.array(model.get(key, [])), np.array(reference_model.get(key, []))
        if a.shape != (3,) or b.shape != (3,) or not np.isfinite([a, b]).all() or abs(a - b).max() > .02:
            raise ValueError("The regular stock floor bounds differ from the template.")
    encoded = np.rint((design @ target - bias) / scale * 32767)
    if not np.isfinite(encoded).all() or (encoded < -32768).any() or (encoded > 32768).any():
        raise ValueError("The regular floor lightmap exceeds its coordinate range.")
    encoded = np.clip(encoded, -32767, 32767).astype(int)
    result = bytearray(raw)
    for i, pair in enumerate(encoded):
        struct.pack_into("<hh", result, i * 28 + 16, *pair)
    return bytes(result)


def regular_lightmap(replacements, game_root=None):
    from .court_import import _scene_document, extracted_stock_floor, find_nba2k27_root, open_iff, read_iff_scene
    document, _wrapped = _scene_document(replacements["level_floor.SCNE"])
    scenes = [scene for scene in document.values() if isinstance(scene, dict)
              and scene.get("Attribute", {}).get("VC_LevelId") == "floor_myteam2k26_int"]
    if not scenes:
        return replacements
    root = find_nba2k27_root(game_root)
    with extracted_stock_floor(root, "levels/arena_000_int_floor.iff") as path, open_iff(path) as archive:
        reference_doc, _ = _scene_document(read_iff_scene(archive))
        references = [m for s in reference_doc.values() if isinstance(s, dict)
                      for m in s.get("Model", {}).values() if m.get("VertexStream")]
        if len(references) != 1:
            raise ValueError("The standard arena 000 floor model is ambiguous.")
        reference = references[0]
        _entry, reference_raw = read_buffer(archive, reference["VertexStream"][0], root)
        # Arena 000's team-specific line meshes have adjusted positions but
        # retain older UVs. Fit the actual floor surface, never those line meshes.
        descriptor = reference.get("IndexBuffer", {})
        if descriptor.get("Format") != "R16_UINT":
            raise ValueError("The regular floor has an unsupported index format.")
        _entry, index_raw = read_buffer(archive, descriptor, root)
        count = 0
        for primitive in reference.get("Prim", []):
            if "line" in primitive.get("Mesh", "").casefold():
                break
            size = primitive.get("Count")
            if type(size) is not int or size <= 0 or size % 3:
                raise ValueError("The regular floor has invalid surface primitives.")
            count += size
        if count < 3 or count * 2 > len(index_raw):
            raise ValueError("The regular floor surface is missing or truncated.")
        reference_indices = struct.unpack_from("<" + "H" * count, index_raw)
        changed = 0
        for scene in scenes:
            for model in scene.get("Model", {}).values():
                if not any("full_court_floor" in p.get("Mesh", "") for p in model.get("Prim", [])):
                    continue
                streams = model.get("VertexStream", [])
                if len(streams) != 1 or streams[0].get("Binary") not in replacements:
                    raise ValueError("The template floor has no decoded vertex buffer.")
                entry = streams[0]["Binary"]
                replacements[entry] = remap_lightmap(model, replacements[entry], reference, reference_raw, reference_indices)
                changed += 1
        if changed != 1:
            raise ValueError("Could not isolate the regular export floor.")
    return replacements
