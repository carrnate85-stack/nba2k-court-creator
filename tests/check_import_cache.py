"""Real BC7 alignment cache audit; does not alter the stock base or game files."""
from collections import OrderedDict
import hashlib
import json
from pathlib import Path
import tempfile
import time
from unittest.mock import patch

from PIL import Image

from court_creator import backend, court_import, experimental_lines


def fingerprint(path):
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    return path.stat().st_size, path.stat().st_mtime_ns, digest


def main():
    source = backend.NBA2K27_EXPORT_BASE
    before = fingerprint(source)
    geometry = experimental_lines.load_geometry(backend.PROJECT_ROOT)
    assert geometry is not None, "The existing stock geometry is required; this audit never rebuilds it."
    metadata = court_import.inspect_iff(source, target=True)
    texture = metadata["selected"]
    descriptor = next(item for item in metadata["textures"] if item["name"] == texture)
    assert (descriptor["width"], descriptor["height"], descriptor["format"]) == (8192, 4096, "BC7_UNORM")
    with court_import.open_iff(source) as archive:
        data, _, revision = court_import._read_dds_data(archive, texture)
    del data
    bounds = court_import.target_court_bounds(geometry=geometry)
    cache = OrderedDict()
    original_decode = court_import._decode_dds
    timings = []
    temporary_path = None
    with tempfile.TemporaryDirectory(prefix="court-import-cache-audit-") as folder:
        temporary_path = Path(folder)
        background = temporary_path / "background.png"
        with Image.new("RGB", (64, 32), (180, 145, 95)) as image:
            image.save(background)
        background_revision = fingerprint(background)[2]
        def render(output, selected_bounds):
            start = time.perf_counter()
            court_import.render_texture(source, texture, selected_bounds, background, output, preview=True,
                                        geometry=geometry, source_revision=revision, background_revision=background_revision)
            elapsed = (time.perf_counter() - start) * 1000
            with Image.open(output) as image:
                assert image.size == (1200, 600)
            return elapsed
        with patch.object(court_import, "_DDS_IMAGE_CACHE", cache), patch.object(court_import, "_decode_dds", wraps=original_decode) as decode:
            try:
                cold_output = temporary_path / "cold.png"
                cold = render(cold_output, bounds)
                original_hash = fingerprint(cold_output)[2]
                assert decode.call_count == 1
                for index, offset in enumerate((0, 4, -4, 8, -8)):
                    adjusted = [bounds[0] + offset, bounds[1], bounds[2] + offset, bounds[3]]
                    output = temporary_path / f"cached-{index}.png"
                    timings.append(render(output, adjusted))
                    if offset == 0:
                        assert fingerprint(output)[2] == original_hash, "Repeated cached preview changed pixels."
                assert decode.call_count == 1, "Repeated alignment changes decoded the BC7 source again."
                retained_bytes = sum(image.width * image.height * 4 for image in cache.values())
                assert retained_bytes == 128 * 1024 * 1024 and len(cache) == 1
                with patch.object(court_import, "_DDS_IMAGE_CACHE", OrderedDict()), patch.object(court_import, "_DDS_IMAGE_CACHE_BYTES", 0):
                    uncached_output = temporary_path / "uncached.png"
                    uncached = render(uncached_output, bounds)
                assert decode.call_count == 2
                assert fingerprint(uncached_output)[2] == original_hash, "Uncached and cached previews differ."
            finally:
                for image in cache.values():
                    image.close()
                cache.clear()
    assert temporary_path is not None and not temporary_path.exists()
    assert fingerprint(source) == before
    report = {"description": "Real local BC7 alignment preview benchmark; OS disk caches are not cleared. Not a cold app-launch or in-game check.",
              "texture": descriptor, "firstPreviewMs": cold, "cachedAlignmentPreviewMs": timings,
              "uncachedSamePreviewMs": uncached, "decodesAcrossSixCachedPreviews": 1,
              "retainedPixelMiB": retained_bytes / (1024 * 1024), "pixelIdentical": True,
              "sourceUnchanged": True, "temporaryFilesRemoved": True, "inGameVerified": False}
    output = backend.PROJECT_ROOT / "outputs" / "import-cache-audit.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    court_import._write_json_atomic(output, report)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
