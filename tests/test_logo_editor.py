import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from http.server import ThreadingHTTPServer
from threading import Thread
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from court_creator import backend
from tools.court_logo_web import clean_items, logo_path, handler_class


class LogoEditorTests(unittest.TestCase):
    def test_editor_http_preserves_guides_and_limits_imported_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            logo = root / "logo.png"
            logo.write_bytes(b"test image")
            state_path = root / "state.json"
            guides = {"anchors": [{"id": "center", "x": 100, "y": 100}]}
            original = {"revision": 1, "project": {"projectRoot": str(root), "items": [],
                                                  "allowedLogoPaths": [str(logo)], "guides": guides}}
            state_path.write_text(json.dumps(original))
            server = ThreadingHTTPServer(("127.0.0.1", 0), handler_class(state_path))
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            base = f"http://127.0.0.1:{server.server_port}"
            try:
                with urlopen(base + "/studio-theme.js") as response:
                    self.assertIn("javascript", response.headers["Content-Type"])
                    self.assertIn(b"WindowBrush", response.read())
                payload = {"selectedId": "copy", "items": [{"id": "copy", "path": str(logo), "x": 123}], "guides": {"evil": True}}
                request = Request(base + "/api/save", json.dumps(payload).encode(), {"Content-Type": "application/json", "Origin": base})
                with urlopen(request) as response:
                    saved = json.load(response)
                self.assertEqual(saved["revision"], 2)
                self.assertEqual(saved["project"]["guides"], guides)
                with urlopen(base + "/api/logo/copy") as response:
                    self.assertEqual(response.read(), b"test image")
                payload["items"][0]["path"] = str(root / "not-imported.png")
                with self.assertRaises(HTTPError) as error:
                    urlopen(Request(base + "/api/save", json.dumps(payload).encode(), {"Content-Type": "application/json"}))
                self.assertEqual(error.exception.code, 400)
                self.assertEqual(json.loads(state_path.read_text())["revision"], 2)
                with self.assertRaises(HTTPError):
                    urlopen(Request(base + "/api/return", b"{}", {"Content-Type": "application/json", "Origin": "http://other.test"}))
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_editor_background_excludes_logos_and_authorizes_imported_paths(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            external = root.parent / "imported-logo.png"
            request = {"backgroundOutput": str(root / "court.png"), "selectedId": "logo", "logoImages": [
                {"id": "logo", "path": str(external), "x": 200, "y": 300, "width": 400, "height": 200}]}
            request_path = root / "request.json"
            request_path.write_text(json.dumps(request))
            guides = {"alignment": "Template Aligned"}
            with patch.object(backend, "default_template_path", return_value=root / "template.psd"), patch.object(
                backend, "parse_court_psd_layers", return_value=SimpleNamespace(width=8192, height=4096)
            ), patch.object(backend, "render_preview") as render, patch(
                "court_creator.experimental_lines.load_geometry", return_value={"version": 3}
            ), patch("court_creator.experimental_lines.editor_guides", return_value=guides):
                project = backend.prepare_logo_editor(request_path)
            self.assertEqual(render.call_args.args[0]["logoImages"], [])
            self.assertEqual(project["allowedLogoPaths"], [str(external)])
            self.assertEqual(project["items"][0]["x"], 200)
            self.assertEqual(project["guides"], guides)

    def test_imported_external_logo_is_allowed_but_similar_root_name_is_not(self):
        root = Path(tempfile.gettempdir()) / "court-project"
        external = root.parent / "chosen-logo.png"
        self.assertEqual(logo_path(root, {"path": str(external)}, [str(external)]), external.resolve())
        with self.assertRaises(ValueError):
            logo_path(root, {"path": str(root.parent / "court-project-other" / "secret.png")})

    def test_clean_items_rejects_nonfinite_positions_and_preserves_zero_opacity(self):
        items = clean_items([{"id": "bad", "path": "logo.png", "x": float("nan")},
                             {"id": "good", "path": "logo.png", "opacity": 0}])
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0]["opacity"], 0)


if __name__ == "__main__":
    unittest.main()
