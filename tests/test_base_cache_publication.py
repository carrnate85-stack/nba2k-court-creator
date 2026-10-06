from contextlib import contextmanager, ExitStack, nullcontext
import json
import os
from pathlib import Path
import tempfile
import subprocess
import sys
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from court_creator import court_import, export_io
from test_export_publication import locked_destination
from test_stock_extraction import game_fixture, write_floor


class BaseCachePublicationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="court-base-cache-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.game, self.live = game_fixture(self.root)
        self.base = self.root / "cache" / "base.iff"; self.base.parent.mkdir()
        write_floor(self.base)
        self.scopes = ExitStack(); self.addCleanup(self.scopes.close)
        self.scopes.enter_context(patch.object(court_import, "GAME_SETTINGS_PATH", self.root / "settings.json"))

    def test_same_size_external_cache_edit_during_cleaning_is_preserved(self):
        before = self.base.read_bytes(), self.base.stat().st_mtime_ns
        conflict = b"X" * len(before[0]); actual = court_import._copy_iff_entry
        def copy(original, cleaned, item, replacement=None):
            actual(original, cleaned, item, replacement)
            if item.filename == "level_floor.SCNE":
                self.base.write_bytes(conflict)
                os.utime(self.base, ns=(self.base.stat().st_atime_ns, before[1]))
        with patch.object(court_import, "_copy_iff_entry", side_effect=copy), self.assertRaisesRegex(ValueError, "cache changed"):
            court_import._clean_base_archive(self.base)
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), (conflict, before[1]))
        self.assertFalse(list(self.base.parent.glob("*.tmp")))

    def test_shared_cache_and_lock_do_not_touch_their_personal_aliases(self):
        alias = self.root / "personal floor.iff"; os.link(self.base, alias)
        before = alias.read_bytes(), alias.stat().st_mtime_ns
        with self.assertRaisesRegex(ValueError, "unshared"):
            court_import._clean_base_archive(self.base)
        self.assertEqual((alias.read_bytes(), alias.stat().st_mtime_ns), before)
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), before)
        alias.unlink()
        tool = self.game / "mod.exe"
        lock = self.base.with_name(self.base.name + ".lock"); os.link(tool, lock)
        before = tool.read_bytes(), tool.stat().st_mtime_ns
        with patch.object(court_import, "cached_2k27_base", return_value={"path": str(self.base)}), self.assertRaisesRegex(ValueError, "source assets"):
            court_import.prepare_2k27_base(self.base, self.game)
        self.assertEqual((tool.read_bytes(), tool.stat().st_mtime_ns), before)
        self.assertEqual((lock.read_bytes(), lock.stat().st_mtime_ns), before)

    def test_selected_game_floor_cannot_be_used_as_the_writable_cache(self):
        write_floor(self.live)
        before = self.live.read_bytes(), self.live.stat().st_mtime_ns
        with self.assertRaisesRegex(ValueError, "source assets"):
            court_import.prepare_2k27_base(self.live, self.game)
        self.assertEqual((self.live.read_bytes(), self.live.stat().st_mtime_ns), before)
        self.assertFalse(self.live.with_name(self.live.name + ".lock").exists())

    def test_fresh_preparation_retains_an_external_cache_created_during_extraction(self):
        self.base.unlink(); stock = self.root / "stock.iff"; write_floor(stock)
        actual = court_import._clean_base_archive
        def clean(path, **options):
            result = actual(path, **options)
            self.base.write_bytes(b"external cache created during extraction")
            return result
        with patch.object(court_import, "extracted_stock_floor", side_effect=lambda _root: nullcontext(stock)), patch.object(court_import, "_clean_base_archive", side_effect=clean), self.assertRaisesRegex(ValueError, "cache changed"):
            court_import.prepare_2k27_base(self.base, self.game)
        self.assertEqual(self.base.read_bytes(), b"external cache created during extraction")
        self.assertEqual(self.live.read_bytes(), b"user floor mod, never changed")
        self.assertFalse(list(self.base.parent.glob("*.tmp")))
        self.assertFalse((self.root / "settings.json").exists())

    @unittest.skipUnless(os.name == "nt", "Real Windows cache publication lock")
    def test_temporary_and_persistent_cleaning_locks_have_bounded_retries(self):
        before = self.base.read_bytes(), self.base.stat().st_mtime_ns
        with locked_destination(self.base), patch.object(export_io.time, "sleep") as sleep, self.assertRaises(OSError):
            court_import._clean_base_archive(self.base)
        self.assertEqual([call.args[0] for call in sleep.call_args_list], [.025, .05, .1])
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), before)
        with locked_destination(self.base) as release, patch.object(export_io.time, "sleep", side_effect=lambda _delay: release()) as sleep:
            court_import._clean_base_archive(self.base)
            sleep.assert_called_once_with(.025)
        with ZipFile(self.base) as archive:
            self.assertEqual(archive.comment, court_import.BASE_VERSION)
            self.assertIsNone(archive.testzip())
        self.assertFalse(list(self.base.parent.glob("*.tmp")))

    def test_inflated_archive_budget_rejects_before_copying_any_entry(self):
        before = self.base.read_bytes(), self.base.stat().st_mtime_ns
        with patch.object(court_import, "MAX_BASE_ARCHIVE_BYTES", self.base.stat().st_size + 1), patch.object(court_import, "_copy_iff_entry") as copy, self.assertRaisesRegex(ValueError, "archive size limit"):
            court_import._clean_base_archive(self.base)
        copy.assert_not_called()
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), before)
        self.assertFalse(list(self.base.parent.glob("*.tmp")))

    def test_generated_archive_budget_is_checked_before_crc_inflation(self):
        before = self.base.read_bytes(), self.base.stat().st_mtime_ns
        with ZipFile(self.base) as archive:
            budget = sum(item.file_size for item in archive.infolist()) + 1024
        actual = court_import._copy_iff_entry
        def copy(original, cleaned, item, replacement=None):
            actual(original, cleaned, item, replacement)
            if item.filename == "level_floor.SCNE": cleaned.writestr("unexpected large entry", b"X" * 4096)
        with patch.object(court_import, "MAX_BASE_ARCHIVE_BYTES", budget), patch.object(court_import, "_copy_iff_entry", side_effect=copy), patch.object(ZipFile, "testzip", side_effect=AssertionError("Oversized generated archive must not be inflated")) as verify, self.assertRaisesRegex(ValueError, "archive size limit"):
            court_import._clean_base_archive(self.base)
        verify.assert_not_called()
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), before)
        self.assertFalse(list(self.base.parent.glob("*.tmp")))

    def test_cached_offline_preparation_does_not_rewrite_hash_copy_or_extract(self):
        expected = court_import._clean_base_archive(self.base)
        before = self.base.read_bytes(), self.base.stat().st_mtime_ns
        with patch.object(court_import, "find_nba2k27_root", side_effect=FileNotFoundError("offline")), patch.object(court_import, "_file_snapshot", side_effect=AssertionError("Cache hit must not hash entire files")), patch.object(court_import, "_clean_base_archive", side_effect=AssertionError("Cache hit must not rewrite")), patch.object(court_import, "extracted_stock_floor", side_effect=AssertionError("Cache hit must not extract")):
            self.assertEqual(court_import.prepare_2k27_base(self.base), expected)
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), before)

    def test_missing_offline_cache_reports_one_discovery_failure_without_a_second_scan(self):
        self.base.unlink(); failure = FileNotFoundError("controlled offline installation")
        with patch.object(court_import, "find_nba2k27_root", side_effect=failure) as find, patch.object(court_import, "extracted_stock_floor", side_effect=AssertionError("Unavailable game must not extract")), self.assertRaises(FileNotFoundError) as caught:
            court_import.prepare_2k27_base(self.base)
        self.assertIs(caught.exception, failure); find.assert_called_once_with()
        self.assertFalse(self.base.exists()); self.assertFalse((self.root / "settings.json").exists())
        self.assertFalse(list(self.base.parent.glob("*.tmp")))

    def test_replaced_cleaning_stage_is_neither_published_nor_deleted(self):
        before = self.base.read_bytes(), self.base.stat().st_mtime_ns
        actual = export_io._publish_export; foreign = None; moved = self.root / "owned clean archive.iff"
        def publish(staged, output, *args, **options):
            nonlocal foreign
            foreign = staged
            staged.rename(moved); staged.write_bytes(b"foreign clean staging sentinel")
            return actual(staged, output, *args, **options)
        with patch.object(export_io, "_publish_export", side_effect=publish), self.assertRaisesRegex(OSError, "identity changed") as caught:
            court_import._clean_base_archive(self.base)
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), before)
        self.assertEqual(foreign.read_bytes(), b"foreign clean staging sentinel")
        with ZipFile(moved) as archive:
            self.assertEqual(archive.comment, court_import.BASE_VERSION); self.assertIsNone(archive.testzip())
        self.assertIn(str(foreign), "\n".join(caught.exception.__notes__))

    @unittest.skipUnless(os.name == "nt", "Real Windows cleaning-stage cleanup lock")
    def test_cleanup_lock_preserves_primary_error_and_prior_cache(self):
        before = self.base.read_bytes(), self.base.stat().st_mtime_ns
        actual = court_import.open_iff; staged = None
        primary = ValueError("controlled validation failure")
        with ExitStack() as locks:
            @contextmanager
            def opened(path):
                nonlocal staged
                with actual(path) as archive:
                    if Path(path) != self.base:
                        staged = Path(path); locks.enter_context(locked_destination(staged)); raise primary
                    yield archive
            with patch.object(court_import, "open_iff", side_effect=opened), self.assertRaises(ValueError) as caught:
                court_import._clean_base_archive(self.base)
            self.assertIs(caught.exception, primary)
            self.assertTrue(staged.is_file())
            self.assertIn(str(staged), "\n".join(primary.__notes__))
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), before)
        staged.unlink()

    @unittest.skipUnless(os.name == "nt", "Windows cache publication retry")
    def test_retry_time_cache_edit_is_retained_before_the_second_replace(self):
        before = self.base.read_bytes(), self.base.stat().st_mtime_ns
        conflict = b"Y" * len(before[0])
        failure = OSError(13, "controlled publication lock"); failure.winerror = 32
        def edit(_delay):
            self.base.write_bytes(conflict)
            os.utime(self.base, ns=(self.base.stat().st_atime_ns, before[1]))
        with patch.object(export_io.os, "replace", side_effect=failure) as replace, patch.object(export_io.time, "sleep", side_effect=edit) as sleep, self.assertRaisesRegex(ValueError, "cache changed"):
            court_import._clean_base_archive(self.base)
        replace.assert_called_once(); sleep.assert_called_once_with(.025)
        self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), (conflict, before[1]))
        self.assertFalse(list(self.base.parent.glob("*.tmp")))

    def test_reported_post_commit_failure_leaves_a_valid_reusable_cache(self):
        actual = export_io.os.replace
        def replace(source, target):
            actual(source, target); raise OSError(112, "controlled error after cache commit")
        with patch.object(export_io.os, "replace", side_effect=replace), self.assertRaisesRegex(OSError, "after cache commit"):
            court_import._clean_base_archive(self.base)
        with patch.object(court_import, "find_nba2k27_root", side_effect=FileNotFoundError("offline")), patch.object(court_import, "_clean_base_archive", side_effect=AssertionError("Committed cache must not rewrite")):
            result = court_import.prepare_2k27_base(self.base)
        self.assertEqual(result["selected"], "bigcourt.dds")
        self.assertFalse(list(self.base.parent.glob("*.tmp")))

    @unittest.skipUnless(os.name == "nt", "Real Windows cache-folder junction")
    def test_linked_cache_folder_does_not_change_its_target(self):
        directory = self.base.parent; saved = directory.with_name("ordinary-cache")
        directory.rename(saved)
        result = subprocess.run(["cmd", "/c", "mklink", "/J", str(directory), str(saved)], capture_output=True, text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        try:
            before = self.base.read_bytes(), self.base.stat().st_mtime_ns
            with self.assertRaisesRegex(ValueError, "linked folders"):
                court_import.prepare_2k27_base(self.base, self.game)
            self.assertEqual((self.base.read_bytes(), self.base.stat().st_mtime_ns), before)
            self.assertEqual([path.name for path in saved.iterdir()], [self.base.name])
        finally:
            directory.rmdir(); saved.rename(directory)

    def test_independent_processes_prepare_one_archive_and_reuse_the_cache(self):
        self.base.unlink(); stock = self.root / "stock.iff"; write_floor(stock)
        count = self.root / "extraction-count"; settings = self.root / "settings.json"
        code = "\n".join([
            "from contextlib import contextmanager",
            "import json, sys",
            "from pathlib import Path",
            "from court_creator import court_import as c",
            "c.GAME_SETTINGS_PATH=Path(sys.argv[4])",
            "@contextmanager",
            "def extracted(root):",
            "    with Path(sys.argv[5]).open('ab') as marker: marker.write(b'X')",
            "    yield Path(sys.argv[3])",
            "c.extracted_stock_floor=extracted",
            "print(json.dumps(c.prepare_2k27_base(Path(sys.argv[1]),Path(sys.argv[2]))))",
        ])
        before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in self.game.rglob("*") if path.is_file()}
        processes = []; results = []
        try:
            for _index in range(4):
                processes.append(subprocess.Popen([sys.executable, "-B", "-c", code, str(self.base), str(self.game), str(stock), str(settings), str(count)],
                                                 cwd=Path(court_import.__file__).resolve().parent.parent, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                                 creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)))
            for process in processes:
                stdout, stderr = process.communicate(timeout=30)
                self.assertEqual(process.returncode, 0, stderr); results.append(json.loads(stdout))
        finally:
            for process in processes:
                if process.poll() is None: process.kill(); process.communicate(timeout=5)
        self.assertEqual(count.read_bytes(), b"X")
        self.assertEqual(results, [results[0]] * 4)
        self.assertEqual(results[0]["selected"], "bigcourt.dds")
        self.assertEqual({path: (path.read_bytes(), path.stat().st_mtime_ns) for path in before}, before)
        self.assertEqual(json.loads(settings.read_bytes())["nba2k27Root"], str(self.game.resolve()))
        self.assertFalse(list(self.base.parent.glob("*.tmp")))


if __name__ == "__main__":
    unittest.main()
