"""Cache readiness must not require rediscovering a game or modifying cached artwork."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('portable_preparation', ROOT / 'tools/prepare_portable.py')
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)


class PortableCacheTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.catalog = self.root / 'assets/court_floor_templates/nba2k27'
        self.catalog.mkdir(parents=True)
        self.index = self.catalog / 'nba2k27_floor_templates.json'
        self.png = self.catalog / 'floor.png'; self.png.write_bytes(b'unchanged court pixels')
        self.thumbnail = self.catalog / 'floor.jpg'; self.thumbnail.write_bytes(b'unchanged thumbnail')
        self.index.write_text(json.dumps({'templates': [{
            'path': str(self.png.relative_to(self.root / 'assets')),
            'thumbnailPath': str(self.thumbnail.relative_to(self.root / 'assets')),
        }]}), encoding='utf-8')
        for name, value in [('ROOT', self.root), ('CATALOG', self.catalog), ('INDEX', self.index)]:
            mocked = patch.object(bootstrap, name, value); mocked.start(); self.addCleanup(mocked.stop)
        geometry = patch.object(bootstrap, 'load_geometry', return_value={'valid': True})
        geometry.start(); self.addCleanup(geometry.stop)

    def test_completed_cache_reuses_artwork_without_game_discovery(self):
        before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in (self.index, self.png, self.thumbnail)}
        with patch.object(bootstrap, 'find_nba2k27_root', side_effect=AssertionError('Game discovery must not run')):
            with patch.object(sys, 'argv', ['prepare_portable.py']), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(bootstrap.main(), 0)
        self.assertEqual(before, {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in before})

    def test_missing_thumbnail_requires_preparation(self):
        self.thumbnail.unlink()
        self.assertFalse(bootstrap.ready())

    def test_bad_or_empty_catalog_is_not_ready(self):
        for text in ('broken json', '{"templates": []}'):
            self.index.write_text(text, encoding='utf-8')
            self.assertFalse(bootstrap.ready())

    def test_check_only_does_not_prepare_or_discover(self):
        self.index.unlink()
        with patch.object(bootstrap, 'find_nba2k27_root', side_effect=AssertionError('Read-only check must not discover')):
            with patch.object(sys, 'argv', ['prepare_portable.py', '--check-only']), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(bootstrap.main(), 3)
        self.assertFalse(self.index.exists())


if __name__ == '__main__':
    unittest.main()
