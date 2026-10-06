from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager, ExitStack
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from PIL import Image
from court_creator import backend, custom_floor_store, export_io
from test_export_publication import locked_destination


class CustomFloorStoreTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="court-custom-store-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name); self.folder = self.root / "custom"; self.folder.mkdir()
        self.metadata = self.folder / "catalog.json"; self.metadata.write_bytes(b'{"floors":[],"note":"keep"}')
        self.source = self.root / "floor.png"; Image.new("RGB", (16, 8), (10, 20, 30)).save(self.source)
        self.original = self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns
        self.source_before = self.source.read_bytes(), self.source.stat().st_mtime_ns
        self.geometry = {"gameUv": {"hardwoodBounds": [0, 0, 8192, 4096]}, "paints": [], "layers": []}

    @contextmanager
    def configured(self):
        with patch.object(backend, "PROJECT_ROOT", self.root), patch.object(backend, "CUSTOM_FLOORS_DIR", self.folder), patch.object(backend, "CUSTOM_FLOORS_META", self.metadata), patch("court_creator.experimental_lines.load_geometry", return_value=self.geometry):
            yield

    def import_floor(self):
        return backend.add_custom_floor(self.source, native=True)

    def assert_source_preserved(self):
        self.assertEqual((self.source.read_bytes(), self.source.stat().st_mtime_ns), self.source_before)
        self.assertFalse(list(self.folder.glob(".court-floor-import-*.tmp")))
        self.assertFalse(list(self.folder.glob(".catalog.json.*.tmp")))

    def test_partial_catalog_write_preserves_live_file_and_foreign_staging(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); folder = root / "custom"; folder.mkdir()
            metadata = folder / "custom_floors.json"; metadata.write_bytes(b'{"floors":[],"note":"keep"}')
            foreign = folder / ".personal.tmp"; foreign.write_bytes(b"keep")
            before = metadata.read_bytes(), metadata.stat().st_mtime_ns
            def partial(path, _payload):
                with path.open("wb") as stream:
                    stream.write(b"partial")
                raise OSError("Injected full disk during staging")
            with patch.object(backend, "PROJECT_ROOT", root), patch.object(backend, "CUSTOM_FLOORS_DIR", folder), patch.object(backend, "CUSTOM_FLOORS_META", metadata), patch.object(Path, "write_bytes", partial):
                with self.assertRaisesRegex(OSError, "full disk"):
                    backend.save_custom_floor_metadata([])
            self.assertEqual((metadata.read_bytes(), metadata.stat().st_mtime_ns), before)
            self.assertEqual(foreign.read_bytes(), b"keep")
            self.assertEqual(list(folder.glob(".*.tmp")), [foreign])

    def test_invalid_image_does_not_publish_or_change_catalog(self):
        self.source.write_bytes(b"not an image")
        before = self.source.read_bytes(), self.source.stat().st_mtime_ns
        with self.configured(), self.assertRaises((OSError, ValueError)):
            self.import_floor()
        self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
        self.assertEqual((self.source.read_bytes(), self.source.stat().st_mtime_ns), before)
        self.assertEqual(list(self.folder.glob("*.png")), [])
        self.assertFalse(list(self.folder.glob(".court-floor-import-*.tmp")))

    @unittest.skipUnless(os.name == "nt", "Real Windows import staging lock")
    def test_decoder_failure_survives_locked_import_staging_cleanup(self):
        primary = ValueError("original image decoder failure")
        retained = []
        with ExitStack() as locks:
            def decode(path):
                self.assertEqual(path.parent, self.folder)
                self.assertTrue(path.name.startswith(".court-floor-import-"))
                retained.append(path)
                locks.enter_context(locked_destination(path))
                raise primary
            with self.configured(), patch.object(custom_floor_store.Image, "open", side_effect=decode), self.assertRaises(ValueError) as result:
                self.import_floor()
            self.assertIs(result.exception, primary)
            self.assertEqual(len(retained), 1)
            self.assertTrue(retained[0].exists())
            self.assertTrue(any(str(retained[0]) in note and "cleanup" in note for note in primary.__notes__))
            self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
            self.assertEqual(list(self.folder.glob("*.png")), [])
        retained[0].unlink()
        self.assert_source_preserved()

    def test_replaced_or_shared_decoded_image_cannot_be_added_to_catalog(self):
        actual_open = Image.open
        for change in ("replaced", "shared"):
            retained = []; moved = self.root / (change + "-retained.tmp")
            @contextmanager
            def decode(path):
                with actual_open(path) as image:
                    yield image
                retained.append(path)
                if change == "replaced":
                    path.rename(moved); path.write_bytes(b"foreign replacement")
                else:
                    os.link(path, moved)
            with self.subTest(change=change), self.configured(), patch.object(custom_floor_store.Image, "open", side_effect=decode), self.assertRaisesRegex(OSError, "identity changed"):
                self.import_floor()
            self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
            self.assertEqual((self.source.read_bytes(), self.source.stat().st_mtime_ns), self.source_before)
            self.assertEqual(list(self.folder.glob("*.png")), [])
            self.assertEqual(len(retained), 1)
            self.assertEqual(retained[0].read_bytes(), b"foreign replacement" if change == "replaced" else self.source_before[0])
            self.assertEqual(moved.read_bytes(), self.source_before[0])
            retained[0].unlink(); moved.unlink()

    def test_filename_collision_retry_rechecks_image_staging_identity(self):
        operation = "rename" if os.name == "nt" else "link"
        actual_publish = getattr(custom_floor_store.os, operation)
        actual_rename = os.rename
        moved = self.root / "original-stage-retained.tmp"; retained = []; calls = 0
        def publish(source, destination):
            nonlocal calls
            calls += 1
            if calls == 1:
                stage = Path(source); actual_rename(stage, moved); stage.write_bytes(b"foreign replacement")
                retained.append(stage); Path(destination).write_bytes(b"foreign filename collision")
                raise FileExistsError("controlled concurrent filename collision")
            return actual_publish(source, destination)
        with self.configured(), patch.object(custom_floor_store.os, operation, side_effect=publish), self.assertRaisesRegex(OSError, "identity changed"):
            self.import_floor()
        self.assertEqual(calls, 1)
        self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
        self.assertEqual((self.source.read_bytes(), self.source.stat().st_mtime_ns), self.source_before)
        self.assertEqual((self.folder / "floor.png").read_bytes(), b"foreign filename collision")
        self.assertEqual(retained[0].read_bytes(), b"foreign replacement")
        self.assertEqual(moved.read_bytes(), self.source_before[0])
        self.assertFalse((self.folder / "floor-2.png").exists())

    def test_filename_created_at_publish_boundary_is_never_overwritten(self):
        collision = self.folder / "floor.png"
        operation = "rename" if os.name == "nt" else "link"
        actual = getattr(custom_floor_store.os, operation)
        injected = False
        def publish(source, destination):
            nonlocal injected
            if Path(destination) == collision and not injected:
                injected = True; collision.write_bytes(b"foreign artwork")
            return actual(source, destination)
        with self.configured(), patch.object(custom_floor_store.os, operation, side_effect=publish):
            result = self.import_floor()
        self.assertTrue(injected)
        self.assertEqual(collision.read_bytes(), b"foreign artwork")
        self.assertEqual(Path(result["image"]["path"]).name, "floor-2.png")
        self.assertEqual(Path(result["image"]["path"]).read_bytes(), self.source_before[0])
        self.assertEqual(json.loads(self.metadata.read_text())["note"], "keep")
        self.assert_source_preserved()

    def test_external_catalog_edit_before_commit_is_retained_and_copy_is_removed(self):
        actual = Path.write_bytes
        changed = b'{"floors":[],"note":"external"}'
        def write(path, payload):
            result = actual(path, payload)
            if path.name.startswith(".catalog.json."):
                actual(self.metadata, changed)
            return result
        with self.configured(), patch.object(Path, "write_bytes", write), self.assertRaisesRegex(ValueError, "catalog changed"):
            self.import_floor()
        self.assertEqual(self.metadata.read_bytes(), changed)
        self.assertEqual(list(self.folder.glob("*.png")), [])
        self.assert_source_preserved()

    @unittest.skipUnless(os.name == "nt", "Windows publication retries")
    def test_external_edit_is_rechecked_after_sharing_failure(self):
        failure = OSError(13, "injected sharing failure"); failure.winerror = 32
        changed = b'{"floors":[],"note":"changed during retry"}'
        with self.configured(), patch.object(export_io.os, "replace", side_effect=failure) as replace, patch.object(export_io.time, "sleep", side_effect=lambda _delay: self.metadata.write_bytes(changed)), self.assertRaisesRegex(ValueError, "catalog changed"):
            self.import_floor()
        replace.assert_called_once()
        self.assertEqual(self.metadata.read_bytes(), changed)
        self.assertEqual(list(self.folder.glob("*.png")), [])
        self.assert_source_preserved()

    def test_error_after_committed_rename_keeps_referenced_artwork(self):
        actual = export_io.os.replace
        def committed(source, destination):
            actual(source, destination)
            raise OSError("Injected error after completed rename")
        with self.configured(), patch.object(export_io.os, "replace", side_effect=committed) as replace:
            result = self.import_floor()
        replace.assert_called_once()
        saved = json.loads(self.metadata.read_text())
        self.assertEqual(saved["floors"][0]["id"], result["image"]["id"])
        self.assertEqual(Path(result["image"]["path"]).read_bytes(), self.source_before[0])
        self.assert_source_preserved()

    def test_changed_uncommitted_copy_is_not_deleted(self):
        copied = self.folder / "floor.png"
        def reject(_store, _payload, _revision, **_options):
            copied.write_bytes(b"externally edited artwork")
            raise OSError("Injected append failure")
        with self.configured(), patch.object(custom_floor_store.CustomFloorStore, "publish", reject), self.assertRaisesRegex(OSError, "append failure"):
            self.import_floor()
        self.assertEqual(copied.read_bytes(), b"externally edited artwork")
        self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
        self.assert_source_preserved()

    @unittest.skipUnless(os.name == "nt", "Real Windows sharing locks")
    def test_temporary_lock_recovers_and_persistent_lock_rolls_back_image(self):
        with self.configured(), locked_destination(self.metadata) as release:
            worker = threading.Thread(target=lambda: (time.sleep(.04), release())); worker.start()
            try:
                result = self.import_floor()
            finally:
                worker.join(timeout=2)
            self.assertFalse(worker.is_alive())
        self.assertTrue(Path(result["image"]["path"]).is_file())
        before = self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns
        with self.configured(), locked_destination(self.metadata), self.assertRaises(PermissionError):
            self.import_floor()
        self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), before)
        self.assertEqual(len(list(self.folder.glob("*.png"))), 1)
        self.assert_source_preserved()

    def test_concurrent_threads_append_without_lost_rows(self):
        with self.configured(), ThreadPoolExecutor(max_workers=8) as pool:
            results = list(pool.map(lambda _index: self.import_floor(), range(8)))
        saved = json.loads(self.metadata.read_text())
        self.assertEqual(len(saved["floors"]), 8)
        self.assertEqual(len({item["id"] for item in saved["floors"]}), 8)
        self.assertEqual(len({item["image"]["path"] for item in results}), 8)
        self.assertEqual(saved["note"], "keep")
        self.assert_source_preserved()

    def test_concurrent_processes_append_without_lost_rows(self):
        code = '''import json,sys
from pathlib import Path
from unittest.mock import patch
from court_creator import backend
root=Path(sys.argv[1])
backend.PROJECT_ROOT=root
backend.CUSTOM_FLOORS_DIR=root/"custom"
backend.CUSTOM_FLOORS_META=backend.CUSTOM_FLOORS_DIR/"catalog.json"
geometry={"gameUv":{"hardwoodBounds":[0,0,8192,4096]},"paints":[],"layers":[]}
with patch("court_creator.experimental_lines.load_geometry",return_value=geometry):
 print(json.dumps(backend.add_custom_floor(root/"floor.png",native=True)))
'''
        children = [subprocess.Popen([sys.executable, "-B", "-c", code, str(self.root)], cwd=backend.PROJECT_ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8") for _index in range(3)]
        try:
            for child in children:
                stdout, stderr = child.communicate(timeout=30)
                self.assertEqual(child.returncode, 0, stderr)
                self.assertTrue(json.loads(stdout)["ok"])
        finally:
            for child in children:
                if child.poll() is None:
                    child.kill(); child.communicate(timeout=5)
        saved = json.loads(self.metadata.read_text())
        self.assertEqual(len(saved["floors"]), 3)
        self.assertEqual(len({item["id"] for item in saved["floors"]}), 3)
        self.assertEqual(saved["note"], "keep")
        self.assert_source_preserved()

    def test_hard_linked_catalog_is_rejected_without_changing_alias(self):
        alias = self.root / "personal.json"; os.link(self.metadata, alias)
        with self.configured(), self.assertRaisesRegex(ValueError, "unshared"):
            self.import_floor()
        self.assertEqual(alias.read_bytes(), self.original[0])
        self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
        self.assertEqual(list(self.folder.glob("*.png")), [])

    def test_missing_catalog_artwork_name_stays_reserved(self):
        missing = self.folder / "floor.png"
        row = {"id": "missing-floor", "path": str(missing)}
        self.metadata.write_text(json.dumps({"floors": [row], "note": "keep"}))
        with self.configured():
            result = self.import_floor()
        self.assertFalse(missing.exists())
        self.assertEqual(Path(result["image"]["path"]).name, "floor-2.png")
        self.assertEqual(json.loads(self.metadata.read_text())["floors"][0], row)
        self.assert_source_preserved()

    def test_external_catalog_adoption_keeps_the_imported_copy(self):
        copied = self.folder / "floor.png"
        changed = {"floors": [{"id": "external", "path": str(copied)}], "note": "adopted"}
        def adopt(_store, _payload, _revision, **_options):
            self.metadata.write_text(json.dumps(changed))
            raise ValueError("Catalog changed externally")
        with self.configured(), patch.object(custom_floor_store.CustomFloorStore, "publish", adopt), self.assertRaisesRegex(ValueError, "externally"):
            self.import_floor()
        self.assertEqual(copied.read_bytes(), self.source_before[0])
        self.assertEqual(json.loads(self.metadata.read_text()), changed)
        self.assert_source_preserved()

    def test_copy_sync_failure_leaves_no_live_image_or_partial_catalog(self):
        with self.configured(), patch.object(custom_floor_store.os, "fsync", side_effect=OSError("Injected image sync failure")), self.assertRaisesRegex(OSError, "sync failure"):
            self.import_floor()
        self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
        self.assertEqual(list(self.folder.glob("*.png")), [])
        self.assert_source_preserved()

    def test_changed_source_during_copy_is_rejected_without_modifying_catalog(self):
        actual = custom_floor_store._stream_revision
        def changed(stream):
            Image.new("RGB", (8, 4), (90, 80, 70)).save(self.source)
            return actual(stream)
        with self.configured(), patch.object(custom_floor_store, "_stream_revision", side_effect=changed), self.assertRaisesRegex(ValueError, "changed while"):
            self.import_floor()
        with Image.open(self.source) as image:
            self.assertEqual(image.getpixel((0, 0)), (90, 80, 70))
        self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
        self.assertEqual(list(self.folder.glob("*.png")), [])
        self.assertFalse(list(self.folder.glob(".court-floor-import-*.tmp")))

    @unittest.skipUnless(os.name == "nt", "Windows directory junctions")
    def test_junction_storage_is_rejected_without_touching_its_target(self):
        temporary = tempfile.TemporaryDirectory(prefix="court-custom-external-"); self.addCleanup(temporary.cleanup)
        outside = Path(temporary.name); metadata = outside / "catalog.json"; metadata.write_bytes(b'{"floors":[],"personal":true}')
        before = metadata.read_bytes(), metadata.stat().st_mtime_ns
        junction = self.root / "linked"
        linked = subprocess.run(["cmd", "/c", "mklink", "/J", str(junction), str(outside)], capture_output=True, text=True)
        self.assertEqual(linked.returncode, 0, linked.stderr)
        with patch.object(backend, "PROJECT_ROOT", self.root), patch.object(backend, "CUSTOM_FLOORS_DIR", junction), patch.object(backend, "CUSTOM_FLOORS_META", junction / "catalog.json"), self.assertRaisesRegex(ValueError, "linked"):
            self.import_floor()
        self.assertEqual((metadata.read_bytes(), metadata.stat().st_mtime_ns), before)
        self.assertEqual(set(outside.iterdir()), {metadata})

    def test_shared_lock_file_is_rejected_before_any_lock_content_write(self):
        personal = self.root / "personal.txt"; personal.write_bytes(b"personal lock alias")
        lock = self.metadata.with_name(self.metadata.name + ".lock"); os.link(personal, lock)
        before = personal.read_bytes(), personal.stat().st_mtime_ns
        with self.configured(), self.assertRaisesRegex(ValueError, "unshared"):
            self.import_floor()
        self.assertEqual((personal.read_bytes(), personal.stat().st_mtime_ns), before)
        self.assertEqual((self.metadata.read_bytes(), self.metadata.stat().st_mtime_ns), self.original)
        self.assertEqual(list(self.folder.glob("*.png")), [])

    def test_parent_segments_cannot_escape_the_application_folder(self):
        temporary = tempfile.TemporaryDirectory(prefix="court-custom-outside-", dir=self.root.parent); self.addCleanup(temporary.cleanup)
        outside = Path(temporary.name); metadata = outside / "catalog.json"; metadata.write_bytes(b'{"floors":[],"personal":true}')
        before = metadata.read_bytes(), metadata.stat().st_mtime_ns
        intermediate = self.root / "sub"; intermediate.mkdir()
        escaped = intermediate / ".." / ".." / outside.name
        with patch.object(backend, "PROJECT_ROOT", self.root), patch.object(backend, "CUSTOM_FLOORS_DIR", escaped), patch.object(backend, "CUSTOM_FLOORS_META", escaped / "catalog.json"), self.assertRaisesRegex(ValueError, "application folder"):
            self.import_floor()
        self.assertEqual((metadata.read_bytes(), metadata.stat().st_mtime_ns), before)
        self.assertEqual(set(outside.iterdir()), {metadata})
        self.assert_source_preserved()


if __name__ == "__main__":
    unittest.main()
