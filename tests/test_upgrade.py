import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
from court_creator import backend
import updater

class UpgradeTests(unittest.TestCase):
    def test_export_resolution(self):
        with patch.object(backend, "parse_court_psd_layers", return_value=SimpleNamespace(layers=[])), patch.object(backend, "create_visible_court_preview_png") as render:
            backend.render_preview({"templatePath": "test.psd", "exportFullResolution": True})
            self.assertIsNone(render.call_args.kwargs["max_size"])
            backend.render_preview({"templatePath": "test.psd"})
            self.assertEqual(render.call_args.kwargs["max_size"], (2048, 1024))

    def test_update_and_rollback(self):
        for fail in (False, True):
            with tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                pending = root / "updates/pending/electron"
                pending.mkdir(parents=True)
                (root / "electron").mkdir()
                (root / "electron/main.js").write_text("old")
                (pending / "main.js").write_text("new")
                (pending.parent / "personal.json").write_text("excluded")
                with patch.object(updater, "ROOT", root), patch.object(updater, "UPDATES", root / "updates"):
                    if fail:
                        with patch.object(updater.os, "replace", side_effect=OSError("busy")):
                            with self.assertRaises(OSError):
                                updater.apply()
                    else:
                        updater.apply()
                self.assertEqual((root / "electron/main.js").read_text(), "old" if fail else "new")
                self.assertEqual((root / "updates/rollback/electron/main.js").read_text(), "old")
                self.assertFalse((root / "personal.json").exists())

if __name__ == "__main__":
    unittest.main()
