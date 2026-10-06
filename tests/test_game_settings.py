from contextlib import ExitStack, nullcontext
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch

from court_creator import court_import, export_io
from test_export_publication import locked_destination
from test_stock_extraction import write_floor


class GameSettingsTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="court-game-settings-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.game = self.root / "selected game"; self.game.mkdir()
        (self.game / "mod.exe").write_bytes(b"game tool sentinel")
        (self.game / "manifest").write_bytes(b"manifest sentinel")
        self.settings = self.root / "data" / "game_installation.json"; self.settings.parent.mkdir()
        self.base = self.root / "cache" / "base.iff"
        self.scopes = ExitStack(); self.addCleanup(self.scopes.close)
        self.scopes.enter_context(patch.object(court_import, "GAME_SETTINGS_PATH", self.settings))

    def test_cached_explicit_selection_preserves_other_settings(self):
        original = {"nba2k27Root": "previous folder", "note": "retain", "extra": {"version": 3}}
        self.settings.write_text(json.dumps(original), encoding="utf-8")
        cached = {"path": str(self.base), "selected": "bigcourt.dds"}
        with patch.object(court_import, "cached_2k27_base", return_value=cached):
            self.assertEqual(court_import.prepare_2k27_base(self.base, self.game), cached)
        self.assertEqual(json.loads(self.settings.read_bytes()), dict(original, nba2k27Root=str(self.game.resolve())))

    def test_failed_extraction_does_not_save_the_selected_folder(self):
        self.settings.write_bytes(b'{"nba2k27Root":"previous folder","note":"retain"}')
        before = self.settings.read_bytes(), self.settings.stat().st_mtime_ns
        with patch.object(court_import, "cached_2k27_base", return_value=None), patch.object(court_import, "extracted_stock_floor", side_effect=OSError("controlled extraction failure")), self.assertRaisesRegex(OSError, "extraction failure"):
            court_import.prepare_2k27_base(self.base, self.game)
        self.assertEqual((self.settings.read_bytes(), self.settings.stat().st_mtime_ns), before)
        self.assertFalse(self.base.exists())

    def test_automatic_preparation_does_not_replace_damaged_settings(self):
        self.settings.write_bytes(b"personal damaged settings sentinel")
        before = self.settings.read_bytes(), self.settings.stat().st_mtime_ns
        stock = self.root / "stock.iff"; write_floor(stock)
        with patch.object(court_import, "cached_2k27_base", return_value=None), patch.object(court_import, "find_nba2k27_root", return_value=self.game), patch.object(court_import, "extracted_stock_floor", side_effect=lambda _root: nullcontext(stock)):
            result = court_import.prepare_2k27_base(self.base)
        self.assertEqual(result["path"], str(self.base))
        self.assertEqual((self.settings.read_bytes(), self.settings.stat().st_mtime_ns), before)
        self.assertEqual((self.game / "mod.exe").read_bytes(), b"game tool sentinel")
        self.assertEqual((self.game / "manifest").read_bytes(), b"manifest sentinel")

    def test_failed_inspection_or_cleanup_does_not_save_the_selected_folder(self):
        self.settings.write_bytes(b'{"nba2k27Root":"previous folder","note":"retain"}')
        before = self.settings.read_bytes(), self.settings.stat().st_mtime_ns
        stock = self.root / "stock.iff"; write_floor(stock)
        for step in ("inspect_iff", "_clean_base_archive"):
            with self.subTest(step=step), patch.object(court_import, "cached_2k27_base", return_value=None), patch.object(court_import, "extracted_stock_floor", side_effect=lambda _root: nullcontext(stock)), patch.object(court_import, step, side_effect=ValueError("controlled preparation failure")), self.assertRaisesRegex(ValueError, "preparation failure"):
                court_import.prepare_2k27_base(self.base, self.game)
            self.assertEqual((self.settings.read_bytes(), self.settings.stat().st_mtime_ns), before)
            self.assertFalse(self.base.exists())
            self.assertFalse(list(self.base.parent.glob("*.tmp")))

    def test_explicit_cached_selection_reports_damaged_settings_without_replacing_cache(self):
        self.settings.write_bytes(b"personal damaged settings sentinel")
        self.base.parent.mkdir(); self.base.write_bytes(b"cached court sentinel")
        before = [(path.read_bytes(), path.stat().st_mtime_ns) for path in (self.settings, self.base)]
        cached = {"path": str(self.base), "selected": "bigcourt.dds"}
        with patch.object(court_import, "cached_2k27_base", return_value=cached), patch.object(court_import, "extracted_stock_floor", side_effect=AssertionError("Cached selection must not extract")), self.assertRaisesRegex(ValueError, "repair or rename"):
            court_import.prepare_2k27_base(self.base, self.game)
        self.assertEqual([(path.read_bytes(), path.stat().st_mtime_ns) for path in (self.settings, self.base)], before)
        self.assertFalse(list(self.settings.parent.glob("*.tmp")))

    def test_invalid_existing_settings_are_preserved_in_optional_and_required_saves(self):
        for payload in (b"broken", b"[]", b"null", b'{"value":NaN}', b'{"value":1e400}', b'{"value":"\xff"}', b'[' * 80 + b'0' + b']' * 80,
                        b'{}' + b' ' * court_import.MAX_GAME_SETTINGS_BYTES):
            with self.subTest(payload=payload[:25]):
                self.settings.write_bytes(payload)
                before = self.settings.stat().st_mtime_ns
                self.assertFalse(court_import._remember_game_root(self.game))
                with self.assertRaises(ValueError):
                    court_import._remember_game_root(self.game, required=True)
                self.assertEqual(self.settings.read_bytes(), payload)
                self.assertEqual(self.settings.stat().st_mtime_ns, before)
        self.assertFalse(list(self.settings.parent.glob("*.tmp")))

    def test_noop_preserves_exact_encoding_bytes_and_timestamp_without_staging(self):
        payload = ('\ufeff' + json.dumps({"nba2k27Root": str(self.game.resolve()), "note": "keep"}, indent=2)).encode("utf-8")
        self.settings.write_bytes(payload); before = self.settings.stat().st_mtime_ns
        with patch.object(court_import, "staged_export", side_effect=AssertionError("No-op must not stage a write")):
            self.assertTrue(court_import._remember_game_root(self.game, required=True))
        self.assertEqual(self.settings.read_bytes(), payload)
        self.assertEqual(self.settings.stat().st_mtime_ns, before)

    def test_serialized_limit_keeps_existing_settings(self):
        self.settings.write_text(json.dumps({"note": "x" * (court_import.MAX_GAME_SETTINGS_BYTES - 64)}), encoding="utf-8")
        before = self.settings.read_bytes(), self.settings.stat().st_mtime_ns
        self.assertLessEqual(len(before[0]), court_import.MAX_GAME_SETTINGS_BYTES)
        self.assertFalse(court_import._remember_game_root(self.game))
        with self.assertRaisesRegex(ValueError, "size limit"):
            court_import._remember_game_root(self.game, required=True)
        self.assertEqual((self.settings.read_bytes(), self.settings.stat().st_mtime_ns), before)
        self.assertFalse(list(self.settings.parent.glob("*.tmp")))

    def test_settings_and_lock_hard_links_are_rejected_without_touching_aliases(self):
        for target in (self.settings, self.settings.with_name(self.settings.name + ".lock")):
            if target.exists(): target.unlink()
            target.write_bytes(b'{"note":"personal sentinel"}')
            alias = self.root / (target.name + ".personal"); os.link(target, alias)
            before = alias.read_bytes(), alias.stat().st_mtime_ns
            try:
                self.assertFalse(court_import._remember_game_root(self.game))
                with self.assertRaisesRegex(ValueError, "unshared"):
                    court_import._remember_game_root(self.game, required=True)
                self.assertEqual((target.read_bytes(), target.stat().st_mtime_ns), before)
                self.assertEqual((alias.read_bytes(), alias.stat().st_mtime_ns), before)
            finally:
                alias.unlink(); target.unlink()

    @unittest.skipUnless(os.name == "nt", "Real Windows settings junction")
    def test_linked_settings_folder_and_game_source_collision_do_not_write(self):
        target = self.root / "personal folder"; target.mkdir()
        personal = target / self.settings.name; personal.write_bytes(b'{"note":"personal sentinel"}')
        before = personal.read_bytes(), personal.stat().st_mtime_ns
        linked = self.root / "linked folder"
        result = subprocess.run(["cmd", "/c", "mklink", "/J", str(linked), str(target)], capture_output=True, text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        try:
            with patch.object(court_import, "GAME_SETTINGS_PATH", linked / self.settings.name):
                self.assertFalse(court_import._remember_game_root(self.game))
                with self.assertRaisesRegex(ValueError, "linked folders"):
                    court_import._remember_game_root(self.game, required=True)
            self.assertEqual((personal.read_bytes(), personal.stat().st_mtime_ns), before)
            self.assertEqual(list(target.iterdir()), [personal])
        finally:
            linked.rmdir()
        for source in (self.game / "mod.exe", self.game / "manifest"):
            before = source.read_bytes(), source.stat().st_mtime_ns
            with patch.object(court_import, "GAME_SETTINGS_PATH", source):
                self.assertFalse(court_import._remember_game_root(self.game))
                with self.assertRaisesRegex(ValueError, "source assets"):
                    court_import._remember_game_root(self.game, required=True)
            self.assertEqual((source.read_bytes(), source.stat().st_mtime_ns), before)
        self.assertEqual({path.name for path in self.game.iterdir()}, {"mod.exe", "manifest"})

    @unittest.skipUnless(os.name == "nt", "Windows settings publication retries")
    def test_retry_time_external_edit_or_creation_is_preserved(self):
        for existing in (False, True):
            if self.settings.exists(): self.settings.unlink()
            if existing: self.settings.write_bytes(b'{"note":"previous settings"}')
            conflict = b'{"nba2k27Root":"external folder","note":"new external edit"}'
            failure = OSError(13, "controlled sharing failure"); failure.winerror = 32
            def edit(_delay):
                self.settings.write_bytes(conflict)
            with patch.object(export_io.os, "replace", side_effect=failure) as replace, patch.object(export_io.time, "sleep", side_effect=edit) as sleep:
                with self.assertRaisesRegex(ValueError, "changed while saving"):
                    court_import._remember_game_root(self.game, required=True)
                replace.assert_called_once(); sleep.assert_called_once_with(.025)
            self.assertEqual(self.settings.read_bytes(), conflict)
            self.assertFalse(list(self.settings.parent.glob("*.tmp")))

    @unittest.skipUnless(os.name == "nt", "Real Windows settings lock")
    def test_real_temporary_and_persistent_settings_locks(self):
        self.settings.write_bytes(b'{"nba2k27Root":"old folder","note":"retain"}')
        before = self.settings.read_bytes(), self.settings.stat().st_mtime_ns
        with locked_destination(self.settings), patch.object(export_io.time, "sleep") as sleep:
            self.assertFalse(court_import._remember_game_root(self.game))
            self.assertEqual([call.args[0] for call in sleep.call_args_list], [.025, .05, .1])
        self.assertEqual((self.settings.read_bytes(), self.settings.stat().st_mtime_ns), before)
        with locked_destination(self.settings) as release, patch.object(export_io.time, "sleep", side_effect=lambda _delay: release()) as sleep:
            self.assertTrue(court_import._remember_game_root(self.game, required=True))
            sleep.assert_called_once_with(.025)
        self.assertEqual(json.loads(self.settings.read_bytes()), {"nba2k27Root": str(self.game.resolve()), "note": "retain"})
        self.assertFalse(list(self.settings.parent.glob("*.tmp")))

    def test_ambiguous_post_commit_error_recognizes_the_complete_settings(self):
        self.settings.write_bytes(b'{"note":"retain"}')
        actual_replace = export_io.os.replace
        def replace(source, target):
            actual_replace(source, target)
            raise OSError(112, "reported after commit")
        with patch.object(export_io.os, "replace", replace):
            self.assertTrue(court_import._remember_game_root(self.game, required=True))
        self.assertEqual(json.loads(self.settings.read_bytes()), {"nba2k27Root": str(self.game.resolve()), "note": "retain"})
        self.assertFalse(list(self.settings.parent.glob("*.tmp")))

    def test_concurrent_threads_preserve_fields_and_use_one_publisher(self):
        self.settings.write_bytes(b'{"note":"retain","extra":{"version":3}}')
        roots = []
        for index in range(8):
            root = self.root / ("game-" + str(index)); root.mkdir(); (root / "mod.exe").touch(); (root / "manifest").touch(); roots.append(root)
        actual_replace = export_io.os.replace; gate = threading.Lock(); active = maximum = 0
        def replace(source, target):
            nonlocal active, maximum
            with gate: active += 1; maximum = max(maximum, active)
            try: actual_replace(source, target)
            finally:
                with gate: active -= 1
        with patch.object(export_io.os, "replace", replace), ThreadPoolExecutor(max_workers=8) as executor:
            results = list(executor.map(lambda root: court_import._remember_game_root(root, required=True), roots))
        self.assertEqual(results, [True] * 8); self.assertEqual(maximum, 1)
        saved = json.loads(self.settings.read_bytes())
        self.assertEqual((saved["note"], saved["extra"]), ("retain", {"version": 3}))
        self.assertIn(saved["nba2k27Root"], [str(root.resolve()) for root in roots])
        self.assertFalse(list(self.settings.parent.glob("*.tmp")))

    def test_independent_processes_serialize_and_preserve_other_fields(self):
        self.settings.write_bytes(b'{"note":"retain","extra":{"version":3}}')
        code = "from pathlib import Path; import sys; from court_creator import court_import as c; c.GAME_SETTINGS_PATH=Path(sys.argv[1]); print(c._remember_game_root(Path(sys.argv[2]),required=True))"
        processes = []
        try:
            for _index in range(4):
                processes.append(subprocess.Popen([sys.executable, "-B", "-c", code, str(self.settings), str(self.game)], cwd=Path(court_import.__file__).resolve().parent.parent,
                                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)))
            for process in processes:
                stdout, stderr = process.communicate(timeout=30)
                self.assertEqual(process.returncode, 0, stderr); self.assertEqual(stdout.strip(), "True")
        finally:
            for process in processes:
                if process.poll() is None: process.kill(); process.communicate(timeout=5)
        self.assertEqual(json.loads(self.settings.read_bytes()), {"nba2k27Root": str(self.game.resolve()), "note": "retain", "extra": {"version": 3}})
        self.assertFalse(list(self.settings.parent.glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
