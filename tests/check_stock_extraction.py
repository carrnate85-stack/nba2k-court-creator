"""Fresh stock extraction audit; leaves game inputs and the app's cache intact."""
import csv
import cProfile
import hashlib
import json
from pathlib import Path
import tempfile
import time
import pstats
from unittest.mock import patch
from zipfile import ZipFile

from court_creator import backend, court_import
from court_creator.experimental_lines import load_geometry, prepare_geometry


def fingerprint(path):
    if not path.is_file():
        return None
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    return (path.stat().st_size, path.stat().st_mtime_ns, digest)


def mods_snapshot(root):
    mods = root / "mods"
    return sorted((str(path.relative_to(mods)), path.stat().st_size, path.stat().st_mtime_ns)
                  for path in mods.rglob("*") if path.is_file()) if mods.is_dir() else None


def main():
    game = court_import.find_nba2k27_root()
    live = game / "mods" / court_import.NBA2K27_BASE_ENTRY
    before = {"manifest": fingerprint(game / "manifest"), "extractor": fingerprint(game / "mod.exe"),
              "looseFloor": fingerprint(live), "mods": mods_snapshot(game),
              "cachedBase": fingerprint(backend.NBA2K27_EXPORT_BASE)}
    with (game / "manifest").open(encoding="utf-8", newline="") as stream:
        entry = next(row for row in csv.reader(stream) if len(row) == 4 and row[0] == court_import.NBA2K27_BASE_ENTRY)
    chunk = game / entry[1]
    archive_state = (chunk.stat().st_size, chunk.stat().st_mtime_ns)
    cached_geometry = load_geometry(backend.PROJECT_ROOT)
    temporary_path = None
    profile = cProfile.Profile()
    timings = {}
    with tempfile.TemporaryDirectory(prefix="court-stock-extraction-audit-") as folder:
        temporary_path = Path(folder)
        base = temporary_path / "base.iff"
        with patch.object(court_import, "GAME_SETTINGS_PATH", temporary_path / "settings.json"):
            print("Preparing a fresh stock export base...", flush=True)
            start = time.perf_counter()
            profile.enable()
            prepared = court_import.prepare_2k27_base(base, game)
            profile.disable()
            timings["freshBaseSeconds"] = round(time.perf_counter() - start, 3)
            start = time.perf_counter()
            assert court_import.prepare_2k27_base(base) == prepared
            timings["cachedBaseMilliseconds"] = round((time.perf_counter() - start) * 1000, 3)
            print("Fresh base prepared in " + str(timings["freshBaseSeconds"]) + " seconds.", flush=True)
        selected = next(item for item in prepared["textures"] if item["name"] == prepared["selected"])
        assert (selected["width"], selected["height"]) == (8192, 4096)
        with ZipFile(base) as archive:
            assert archive.comment == court_import.BASE_VERSION and archive.testzip() is None
            scene, _ = court_import._scene_document(archive.read("level_floor.SCNE"))
            prims = [prim for value in scene.values() if isinstance(value, dict)
                     for model in value.get("Model", {}).values() for prim in model.get("Prim", [])]
            assert len(prims) == 1 and "full_court_floor" in prims[0]["Mesh"]
        image = court_import.read_dds(base, prepared["selected"])
        assert image.size == (8192, 4096)
        image.close()
        print("Preparing fresh stock geometry...", flush=True)
        start = time.perf_counter()
        profile.enable()
        geometry = prepare_geometry(temporary_path / "fresh-geometry")
        profile.disable()
        timings["freshGeometrySeconds"] = round(time.perf_counter() - start, 3)
        start = time.perf_counter()
        assert prepare_geometry(temporary_path / "fresh-geometry") == geometry
        timings["cachedGeometryMilliseconds"] = round((time.perf_counter() - start) * 1000, 3)
        print("Fresh geometry prepared in " + str(timings["freshGeometrySeconds"]) + " seconds.", flush=True)
        assert geometry["gameUv"]["surfaceTriangles"] == 584
        assert all(.999 <= value <= 1.001 for value in geometry["gameUv"]["projectionCoverage"].values())
        assert cached_geometry is not None and geometry == cached_geometry, "Fresh geometry differs from the app's existing stock geometry."
        assert not base.with_name(base.name + ".held-loose").exists()
        assert not base.with_name(base.name + ".extraction.json").exists()
    after = {"manifest": fingerprint(game / "manifest"), "extractor": fingerprint(game / "mod.exe"),
             "looseFloor": fingerprint(live), "mods": mods_snapshot(game),
             "cachedBase": fingerprint(backend.NBA2K27_EXPORT_BASE)}
    assert after == before, "Stock extraction changed installed game inputs, mods, or the app's existing cache."
    assert archive_state == (chunk.stat().st_size, chunk.stat().st_mtime_ns)
    assert temporary_path is not None and not temporary_path.exists()
    report = {"gameRoot": str(game), "freshBaseTexture": selected,
              "freshGeometryMatches": True, "surfaceTriangles": geometry["gameUv"]["surfaceTriangles"],
              "gameUvHardwoodBounds": geometry["gameUv"]["hardwoodBounds"],
              "installedInputsUnchanged": True, "existingCacheUnchanged": True,
              "temporaryFilesRemoved": True, "inGameVerified": False, "timings": timings}
    output = backend.PROJECT_ROOT / "outputs" / "stock-extraction-audit"
    output.mkdir(parents=True, exist_ok=True)
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    profile.dump_stats(output / "profile.pstats")
    with (output / "profile.txt").open("w", encoding="utf-8") as stream:
        pstats.Stats(profile, stream=stream).sort_stats("cumulative").print_stats(40)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
