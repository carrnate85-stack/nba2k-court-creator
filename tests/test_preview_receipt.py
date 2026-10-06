import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from PIL import Image
from court_creator import backend, court_import, export_io, preview_receipt
from test_court_import import solid_dds


class PreviewReceiptTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory(prefix="court-preview-receipt-")
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name)
        self.source = self.root / "source.iff"
        with ZipFile(self.source, "w") as archive:
            archive.writestr("bigcourt.dds", solid_dds(32, 16))
        self.background = self.root / "background.png"
        with Image.new("RGB", (32, 16), "blue") as image:
            image.save(self.background)
        self.output = self.root / "preview.png"
        self.output.write_bytes(b"last good preview")
        self.before = self.output.read_bytes(), self.output.stat().st_mtime_ns

    def render(self, receipt):
        with patch.object(court_import, "_aligned_image", side_effect=lambda *a, **kw: Image.new("RGB", (32, 16), "red")):
            return court_import.render_texture(self.source, "bigcourt.dds", [0, 0, 32, 16],
                self.background, self.output, preview=True, publication_receipt=receipt)

    def test_creation_handle_receipt_survives_atomic_publication(self):
        receipt = {}
        self.assertEqual(self.render(receipt), self.output)
        self.assertEqual(receipt["version"], 1)
        self.assertEqual(receipt["bytes"], self.output.stat().st_size)
        self.assertEqual(receipt["sha256"], hashlib.sha256(self.output.read_bytes()).hexdigest())
        preview_receipt.validate_receipt(self.output, receipt)
        self.assertEqual(set(receipt["identity"]), {"volume", "high", "low"} if os.name == "nt" else {"device", "index"})
        self.assertFalse(list(self.root.glob(".preview.png.*.tmp")))

    def test_worker_returns_receipt_matching_actual_preview(self):
        request_path = self.root / "request.json"
        request_path.write_text(json.dumps({"sourcePath": str(self.source), "textureName": "bigcourt.dds",
            "backgroundPath": str(self.background), "bounds": [0, 0, 32, 16], "outputPath": str(self.output)}), encoding="utf-8")
        with patch.object(court_import, "_aligned_image", side_effect=lambda *a, **kw: Image.new("RGB", (32, 16), "red")):
            result = backend.preview_import(request_path)
        self.assertEqual(result["previewPath"], str(self.output))
        preview_receipt.validate_receipt(self.output, result["previewReceipt"])

    def test_same_size_time_edit_is_detected_on_publication_and_retained(self):
        receipt = {}
        original = export_io._publish_export
        changed = []
        def mutate(stage, *args, **kwargs):
            info = stage.stat()
            data = bytearray(stage.read_bytes()); data[-1] ^= 1
            stage.write_bytes(data); os.utime(stage, ns=(info.st_atime_ns, info.st_mtime_ns))
            changed.append((stage, bytes(data)))
            return original(stage, *args, **kwargs)
        with patch.object(export_io, "_publish_export", side_effect=mutate):
            with self.assertRaisesRegex(ValueError, "changed") as failure:
                self.render(receipt)
        self.assertEqual(receipt, {})
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), self.before)
        self.assertEqual(changed[0][0].read_bytes(), changed[0][1])
        self.assertTrue(any(str(changed[0][0]) in note for note in failure.exception.__notes__))

    def test_same_byte_replacement_and_shared_alias_are_not_adopted(self):
        for action in ("replace", "hardlink"):
            with self.subTest(action=action):
                receipt = {}; paths = []
                original = export_io._publish_export
                def mutate(stage, *args, **kwargs):
                    other = self.root / (action + ".retained")
                    if action == "replace":
                        data = stage.read_bytes(); stage.rename(other); stage.write_bytes(data)
                    else:
                        os.link(stage, other)
                    paths.extend((stage, other))
                    return original(stage, *args, **kwargs)
                with patch.object(export_io, "_publish_export", side_effect=mutate):
                    with self.assertRaises(OSError):
                        self.render(receipt)
                self.assertEqual(receipt, {})
                self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), self.before)
                self.assertTrue(all(path.exists() for path in paths))
                for path in paths:
                    path.unlink()

    def test_failed_publication_has_no_receipt_and_cleans_only_unchanged_stage(self):
        for failure in (OSError(112, "disk full"), KeyboardInterrupt("cancel preview")):
            with self.subTest(failure=failure):
                receipt = {}
                with patch.object(export_io, "_publish_export", side_effect=failure):
                    with self.assertRaises(type(failure)) as result:
                        self.render(receipt)
                self.assertIs(result.exception, failure)
                self.assertEqual(receipt, {})
                self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), self.before)
                self.assertFalse(list(self.root.glob(".preview.png.*.tmp")))

    def test_receipt_read_restores_position_and_checks_file_budget(self):
        path = self.root / "receipt.bin"
        path.write_bytes(b"exact receipt bytes")
        with path.open("r+b", buffering=0) as stream:
            stream.seek(3); receipt = preview_receipt.stream_receipt(stream)
            self.assertEqual(stream.tell(), 3)
            self.assertEqual(receipt["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
            with patch.object(preview_receipt, "MAX_ASSET_BYTES", path.stat().st_size - 1):
                with self.assertRaisesRegex(ValueError, "size"):
                    preview_receipt.stream_receipt(stream)
        path.write_bytes(b"")
        with path.open("rb", buffering=0) as stream:
            with self.assertRaisesRegex(ValueError, "size"):
                preview_receipt.stream_receipt(stream)

    def test_receipt_rejects_same_bytes_at_another_identity(self):
        receipt = {}; self.render(receipt)
        copy = self.root / "foreign.png"; copy.write_bytes(self.output.read_bytes())
        with self.assertRaisesRegex(ValueError, "changed"):
            preview_receipt.validate_receipt(copy, receipt)

    def test_retry_time_edit_is_rechecked_without_publishing_or_deleting_it(self):
        if os.name != "nt":
            self.skipTest("Windows publication retry")
        receipt = {}; attempts = []; original = os.replace
        def replace(stage, output):
            attempts.append(Path(stage))
            if len(attempts) == 1:
                error = PermissionError("controlled sharing failure"); error.winerror = 32
                raise error
            return original(stage, output)
        def retry(_delay):
            stage = attempts[0]; info = stage.stat()
            data = bytearray(stage.read_bytes()); data[-1] ^= 1
            stage.write_bytes(data); os.utime(stage, ns=(info.st_atime_ns, info.st_mtime_ns))
        with patch.object(export_io.os, "replace", side_effect=replace), patch.object(export_io.time, "sleep", side_effect=retry):
            with self.assertRaisesRegex(ValueError, "changed"):
                self.render(receipt)
        self.assertEqual(len(attempts), 1)
        self.assertTrue(attempts[0].exists())
        self.assertEqual(receipt, {})
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), self.before)
