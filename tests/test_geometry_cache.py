import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch

from court_creator import court_import, experimental_lines as geometry


def fixture():
    polygon = [[100, 100], [900, 100], [900, 500], [100, 500]]
    lines = [*geometry.NAMES, "college-three", "high-school-three"]
    paints = [f"{prefix}-{side}" for prefix in ("paint", "secondary-paint", "two-point") for side in ("left", "right")]
    layer = lambda name: {"id": name, "name": name, "color": "#FFFFFF", "visible": True,
                          "polygons": [copy.deepcopy(polygon)], "gameUvPolygons": [copy.deepcopy(polygon)]}
    ids = ["court-center", *(f"court-corner-{i}" for i in range(4)),
           *(f"{side}-{kind}" for side in ("left", "right") for kind in ("free-throw", "paint-center")),
           *(f"{side}-key-{i}" for side in ("left", "right") for i in range(4))]
    positions = {"court-center": (500, 300), **{f"court-corner-{i}": point for i, point in enumerate(
        ((110, 490), (110, 110), (890, 110), (890, 490)))}}
    return {"version": geometry.GEOMETRY_VERSION, "size": [8192, 4096], "source": court_import.NBA2K27_BASE_ENTRY,
            "layers": [layer(name) for name in lines], "paints": [layer(name) for name in paints],
            "guides": {mapping: {"anchors": [{"id": name, "name": name, "x": positions.get(name, (200, 200))[0],
                                               "y": positions.get(name, (200, 200))[1]} for name in ids]}
                       for mapping in ("template", "game-uv")},
            "gameUv": {"texcoord": 0, "matrix": [[1, 0, 0], [0, 1, 0]], "surfaceTriangles": 2,
                       "courtSurfacePolygons": [copy.deepcopy(polygon)], "hardwoodBounds": [100, 100, 800, 400],
                       "projectionCoverage": {name: 1 for name in [*lines, *paints]}}}


class GeometryCacheTests(unittest.TestCase):
    def test_valid_cache_is_loaded_without_rebuilding(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            court_import._write_json_atomic(geometry.geometry_path(root), fixture())
            with patch.object(geometry, "_build_geometry", side_effect=AssertionError("Valid cache was rebuilt")):
                self.assertEqual(geometry.prepare_geometry(root), fixture())

    def test_bad_json_and_shapes_are_cache_misses_without_mutation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path = geometry.geometry_path(root)
            path.parent.mkdir(parents=True)
            for payload in (b"partial JSON", b"\xff", b"null", b"[]", b"1", b'"text"', b"{" * 2000):
                path.write_bytes(payload)
                self.assertIsNone(geometry.load_geometry(root))
                self.assertEqual(path.read_bytes(), payload)
            cases = [
                lambda d: d.update(version=3), lambda d: d.update(size=[4096, 2048]),
                lambda d: d.update(source="another-floor.iff"), lambda d: d.update(layers=[]),
                lambda d: d["layers"][0].update(color="#invalid"), lambda d: d["layers"][0].update(visible=1),
                lambda d: d["layers"][0].update(id=d["layers"][1]["id"]),
                lambda d: d["layers"][0].update(polygons=[[[True, 100], [900, 100], [900, 500]]]),
                lambda d: d["layers"][0].update(gameUvPolygons=[[[float("nan"), 100], [900, 100], [900, 500]]]),
                lambda d: d["layers"][0].update(polygons=[[[10**500, 100], [900, 100], [900, 500]]]),
                lambda d: d["guides"]["game-uv"]["anchors"][0].update(x=None),
                lambda d: d["guides"]["game-uv"]["anchors"].pop(),
                lambda d: d["guides"]["game-uv"]["anchors"][1].update(x=-10),
                lambda d: [anchor.update(x=110, y=110) for anchor in d["guides"]["game-uv"]["anchors"] if anchor["id"].startswith("court-corner-")],
                lambda d: d["gameUv"].update(matrix=[[0, 0, 0], [0, 0, 0]]),
                lambda d: d["gameUv"].update(hardwoodBounds=[8000, 100, 800, 400]),
                lambda d: d["gameUv"].update(hardwoodBounds=[100, 100, 799, 400]),
                lambda d: d["gameUv"].update(texcoord=True), lambda d: d["gameUv"].update(surfaceTriangles=-1),
                lambda d: d["gameUv"]["projectionCoverage"].pop("paint-left"),
                lambda d: d["gameUv"]["projectionCoverage"].update({"paint-left": .5}),
                lambda d: d.update(regulationCalibration={"metadataValue": float("inf")}),
            ]
            for mutate in cases:
                document = fixture()
                mutate(document)
                payload = json.dumps(document).encode()
                path.write_bytes(payload)
                self.assertIsNone(geometry.load_geometry(root))
                self.assertEqual(path.read_bytes(), payload)

    def test_size_and_point_budgets_are_enforced(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            court_import._write_json_atomic(geometry.geometry_path(root), fixture())
            with patch.object(geometry, "MAX_GEOMETRY_BYTES", 64):
                self.assertIsNone(geometry.load_geometry(root))
        document = fixture()
        document["layers"][0]["polygons"] = [[[100, 100], [900, 100], [900, 500]]] * 50000
        document["layers"][0]["gameUvPolygons"] = copy.deepcopy(document["layers"][0]["polygons"])
        self.assertFalse(geometry._valid_geometry(document))

    def test_failed_rebuild_and_retry_preserve_old_cache_until_commit(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path = geometry.geometry_path(root)
            path.parent.mkdir(parents=True)
            path.write_bytes(b"damaged old cache")
            with patch.object(geometry, "_build_geometry", side_effect=[RuntimeError("extraction failed"), fixture()]):
                with self.assertRaisesRegex(RuntimeError, "extraction failed"):
                    geometry.prepare_geometry(root)
                self.assertEqual(path.read_bytes(), b"damaged old cache")
                self.assertEqual(geometry.prepare_geometry(root), fixture())
            self.assertEqual(geometry.load_geometry(root), fixture())

    def test_failed_atomic_write_and_invalid_generation_leave_no_temporary_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path = geometry.geometry_path(root)
            path.parent.mkdir(parents=True)
            path.write_bytes(b"damaged old cache")
            with patch.object(geometry, "_build_geometry", return_value=fixture()), patch.object(court_import.os, "fsync", side_effect=OSError("disk full")):
                with self.assertRaisesRegex(OSError, "disk full"):
                    geometry.prepare_geometry(root)
            self.assertEqual(path.read_bytes(), b"damaged old cache")
            self.assertEqual({p.name for p in path.parent.iterdir()}, {path.name, path.name + ".lock"})
            with patch.object(geometry, "_build_geometry", return_value={"version": geometry.GEOMETRY_VERSION}):
                with self.assertRaisesRegex(ValueError, "cache validation"):
                    geometry.prepare_geometry(root)
            self.assertEqual(path.read_bytes(), b"damaged old cache")
            with patch.object(geometry, "_build_geometry", return_value=fixture()), patch.object(geometry, "MAX_GEOMETRY_BYTES", 64):
                with self.assertRaisesRegex(ValueError, "cache size limit"):
                    geometry.prepare_geometry(root)
            self.assertEqual(path.read_bytes(), b"damaged old cache")
            with patch.object(geometry, "_build_geometry", return_value=fixture()):
                self.assertEqual(geometry.prepare_geometry(root), fixture())

    def test_atomic_json_serialization_failure_preserves_target_and_cleans_staging(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "settings.json"
            path.write_bytes(b"original")
            for value, error in (({"bad": object()}, TypeError), ({"bad": float("nan")}, ValueError)):
                with self.assertRaises(error):
                    court_import._write_json_atomic(path, value)
                self.assertEqual(path.read_bytes(), b"original")
                self.assertEqual(list(path.parent.iterdir()), [path])

    def test_simultaneous_thread_preparation_rechecks_cache_under_lock(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)

            def build():
                time.sleep(.03)
                return fixture()

            with patch.object(geometry, "_build_geometry", side_effect=build) as builder:
                with ThreadPoolExecutor(max_workers=2) as executor:
                    results = list(executor.map(lambda _: geometry.prepare_geometry(root), range(2)))
            self.assertEqual(results, [fixture(), fixture()])
            self.assertEqual(builder.call_count, 1)

    def test_separate_processes_share_one_cache_preparation(self):
        script = """
import json, sys, time
from pathlib import Path
from court_creator import experimental_lines as geometry
root = Path(sys.argv[1])
def build():
    with (root / 'builds.log').open('a') as stream: stream.write('build\\n')
    time.sleep(.1)
    return json.loads((root / 'fixture.json').read_text())
geometry._build_geometry = build
geometry.prepare_geometry(root)
print('ready')
"""
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "fixture.json").write_text(json.dumps(fixture()), encoding="utf-8")
            processes = []
            try:
                for _ in range(2):
                    processes.append(subprocess.Popen([sys.executable, "-B", "-c", script, str(root)],
                        cwd=Path(__file__).resolve().parents[1], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                        text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)))
                for process in processes:
                    output, _ = process.communicate(timeout=20)
                    self.assertEqual(process.returncode, 0, output)
                    self.assertEqual(output.strip(), "ready")
            finally:
                for process in processes:
                    if process.poll() is None:
                        process.kill()
                    process.wait(timeout=10)
                    if process.stdout:
                        process.stdout.close()
            self.assertEqual((root / "builds.log").read_text().splitlines(), ["build"])
            self.assertEqual(geometry.load_geometry(root), fixture())

    @unittest.skipUnless(os.name == "nt", "Windows non-strict resolution prefix race")
    def test_cache_lock_does_not_use_nonstrict_resolution_of_missing_parent(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve(strict=True)
            parent = geometry.geometry_path(root).parent
            actual_resolve = Path.resolve

            def resolve(path, *args, **kwargs):
                if path == parent and not kwargs.get("strict", False) and not path.exists():
                    # Reproduce the observed mkdir race retaining an equivalent extended prefix.
                    parent.mkdir(parents=True)
                    return Path("\\\\?\\" + str(parent))
                return actual_resolve(path, *args, **kwargs)

            with patch.object(Path, "resolve", resolve), patch.object(geometry, "_build_geometry", return_value=fixture()) as builder:
                self.assertEqual(geometry.prepare_geometry(root), fixture())
            self.assertEqual(builder.call_count, 1)
            self.assertEqual(geometry.load_geometry(root), fixture())

    @unittest.skipUnless(os.name == "nt", "Windows cache-folder substitution junction")
    def test_lock_rechecks_parent_after_directory_creation_before_opening(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve(strict=True)
            destination = geometry.geometry_path(root)
            parent = destination.parent
            parent.parent.mkdir(parents=True)
            foreign = root / "foreign"
            foreign.mkdir()
            sentinel = foreign / "personal.txt"
            sentinel.write_bytes(b"protected folder bytes")
            before = sentinel.read_bytes(), sentinel.stat().st_mtime_ns
            actual_mkdir = Path.mkdir
            created = False

            def mkdir(path, *args, **kwargs):
                nonlocal created
                if path != parent:
                    return actual_mkdir(path, *args, **kwargs)
                result = subprocess.run(["cmd", "/c", "mklink", "/J", str(parent), str(foreign)],
                    capture_output=True, text=True, timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                created = True

            try:
                with patch.object(Path, "mkdir", mkdir), self.assertRaisesRegex(ValueError, "linked folders"):
                    with court_import._base_file_lock(destination):
                        self.fail("Linked parent acquired a stock-data lock.")
                self.assertEqual((sentinel.read_bytes(), sentinel.stat().st_mtime_ns), before)
                self.assertEqual(list(foreign.iterdir()), [sentinel])
            finally:
                if created:
                    self.assertTrue(parent.is_junction())
                    self.assertEqual(parent.parent.resolve(strict=True), root / "data")
                    parent.rmdir()


if __name__ == "__main__":
    unittest.main()
