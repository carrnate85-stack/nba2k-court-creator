import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from court_creator import court_import, export_io
from test_export_publication import locked_destination


class ExtractionRecoveryTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="court-recovery-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.game = self.root / "game"; self.game.mkdir()
        (self.game / "mod.exe").write_bytes(b"game tool sentinel")
        (self.game / "manifest").write_bytes(b"manifest sentinel")
        self.base = self.root / "cache" / "base.iff"; self.base.parent.mkdir()
        self.held = self.base.with_name(self.base.name + ".held-loose")
        self.held.write_bytes(b"original user floor")
        self.journal = self.base.with_name(self.base.name + ".extraction.json")
        self.state = {"gameRoot": str(self.game), "hadLoose": True, "extra": "retain"}
        self.journal.write_text(json.dumps(self.state), encoding="utf-8")
        self.live = self.game / "mods" / court_import.NBA2K27_BASE_ENTRY
        self.live.parent.mkdir(parents=True)
        self.live.write_bytes(b"current floor sentinel")

    def snapshot(self):
        return {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in
                (self.held, self.journal, self.live, self.game / "mod.exe", self.game / "manifest") if path.exists()}

    def test_nonfinite_or_oversized_records_do_not_move_any_file(self):
        for payload in (b'{"gameRoot":' + json.dumps(str(self.game)).encode() + b',"hadLoose":true,"extra":NaN}',
                        json.dumps(self.state).encode() + b' ' * (64 * 1024),
                        b'[' * 80 + b'0' + b']' * 80, b'null', b'[]', b'{"invalid":"\xff"}',
                        json.dumps(dict(self.state, gameRoot=str(self.game) + '\0')).encode(),
                        json.dumps(dict(self.state, restoredRevision="not a hash")).encode(),
                        json.dumps(dict(self.state, hadLoose=False, restoredRevision="a" * 64)).encode()):
            with self.subTest(payload=payload[:40]):
                self.journal.write_bytes(payload)
                before = self.snapshot()
                with self.assertRaises(ValueError):
                    court_import._recover_base_extraction(self.base)
                self.assertEqual(self.snapshot(), before)
                self.assertFalse(list(self.base.parent.glob("interrupted-floor-*.iff")))

    def test_orphan_backup_does_not_restore_into_a_guessed_game(self):
        self.journal.unlink(); before = self.snapshot()
        with patch.object(court_import, "find_nba2k27_root", return_value=self.game) as find, self.assertRaisesRegex(ValueError, "record is missing"):
            court_import._recover_base_extraction(self.base)
        find.assert_not_called()
        self.assertEqual(self.snapshot(), before)

    def test_shared_backup_and_inconsistent_record_are_retained(self):
        alias = self.root / "personal floor.iff"; os.link(self.held, alias)
        before = self.snapshot()
        with self.assertRaisesRegex(ValueError, "unshared"):
            court_import._recover_base_extraction(self.base)
        self.assertEqual(self.snapshot(), before); self.assertEqual(alias.read_bytes(), b"original user floor")
        alias.unlink()
        self.state["hadLoose"] = False; self.journal.write_text(json.dumps(self.state), encoding="utf-8")
        before = self.snapshot()
        with self.assertRaisesRegex(ValueError, "inconsistent"):
            court_import._recover_base_extraction(self.base)
        self.assertEqual(self.snapshot(), before)

    def test_a_new_user_floor_is_retained_in_the_game_folder(self):
        self.held.unlink(); self.state["hadLoose"] = False
        self.journal.write_text(json.dumps(self.state), encoding="utf-8")
        before = self.live.read_bytes(), self.live.stat().st_mtime_ns
        court_import._recover_base_extraction(self.base)
        self.assertEqual((self.live.read_bytes(), self.live.stat().st_mtime_ns), before)
        copies = list(self.base.parent.glob("interrupted-floor-*.iff"))
        self.assertEqual(len(copies), 1); self.assertEqual(copies[0].read_bytes(), before[0])
        self.assertFalse(self.journal.exists())

    @unittest.skipUnless(os.name == "nt", "Real Windows backup lock")
    def test_a_locked_backup_never_leaves_the_game_without_a_floor(self):
        with locked_destination(self.held), self.assertRaises(OSError):
            court_import._recover_base_extraction(self.base)
        self.assertTrue(self.live.is_file(), "The current game floor must not be moved away before a restore can commit")
        self.assertEqual(self.live.read_bytes(), b"original user floor")
        self.assertEqual(self.held.read_bytes(), b"original user floor")
        state = json.loads(self.journal.read_bytes())
        self.assertEqual(state.get("restoredRevision"), hashlib.sha256(self.held.read_bytes()).hexdigest())
        self.assertEqual(state["extra"], "retain")
        court_import._recover_base_extraction(self.base)
        self.assertFalse(self.held.exists()); self.assertFalse(self.journal.exists())
        self.assertEqual(self.live.read_bytes(), b"original user floor")
        self.assertEqual(len(list(self.base.parent.glob("interrupted-floor-*.iff"))), 1)

    def test_a_completed_restore_with_a_newer_user_floor_preserves_the_record(self):
        self.held.unlink()
        self.state["restoredRevision"] = hashlib.sha256(b"original user floor").hexdigest()
        self.journal.write_text(json.dumps(self.state), encoding="utf-8")
        before = self.snapshot()
        with self.assertRaisesRegex(ValueError, "changed after recovery"):
            court_import._recover_base_extraction(self.base)
        self.assertEqual(self.snapshot(), before)

    def test_bom_records_restore_and_preserve_exact_interrupted_pixels(self):
        for encoding in ("utf-8-sig", "utf-16"):
            with self.subTest(encoding=encoding):
                self.held.write_bytes(b"original user floor"); self.live.write_bytes(b"current floor sentinel")
                self.journal.write_bytes(json.dumps(self.state).encode(encoding))
                court_import._recover_base_extraction(self.base)
                self.assertEqual(self.live.read_bytes(), b"original user floor")
                self.assertFalse(self.held.exists()); self.assertFalse(self.journal.exists())
        copies = list(self.base.parent.glob("interrupted-floor-*.iff"))
        self.assertEqual(len(copies), 2)
        self.assertEqual([path.read_bytes() for path in copies], [b"current floor sentinel"] * 2)

    def test_incomplete_copies_and_cancellation_preserve_sources_and_cleanup_owned_files(self):
        for failure in (OSError("controlled copy failure"), KeyboardInterrupt()):
            def copy(_source, _expected, target):
                target.write(b"partial copy"); raise failure
            before = self.snapshot()
            with self.subTest(failure=type(failure).__name__), patch.object(court_import, "_copy_recovery_file", side_effect=copy), self.assertRaises(type(failure)):
                court_import._recover_base_extraction(self.base)
            self.assertEqual(self.snapshot(), before)
            self.assertFalse(list(self.base.parent.glob("interrupted-floor-*.iff")))
            self.assertFalse(list(self.live.parent.glob("*.tmp")))

    def test_restore_copy_failure_retains_a_complete_current_floor_copy(self):
        actual = court_import._copy_recovery_file
        def copy(source, expected, target):
            if source == self.held:
                target.write(b"partial restore"); raise OSError("controlled restore failure")
            return actual(source, expected, target)
        before = self.snapshot()
        with patch.object(court_import, "_copy_recovery_file", side_effect=copy), self.assertRaisesRegex(OSError, "restore failure") as caught:
            court_import._recover_base_extraction(self.base)
        self.assertEqual(self.snapshot(), before)
        copies = list(self.base.parent.glob("interrupted-floor-*.iff"))
        self.assertEqual(len(copies), 1); self.assertEqual(copies[0].read_bytes(), b"current floor sentinel")
        self.assertIn(str(copies[0]), "\n".join(caught.exception.__notes__))
        self.assertFalse(list(self.live.parent.glob("*.tmp")))

    def test_copy_readback_rejects_successfully_written_wrong_bytes(self):
        class CorruptingTarget:
            def __init__(self, stream): self.stream = stream
            def __getattr__(self, name): return getattr(self.stream, name)
            def write(self, data): return self.stream.write(b"X" * len(data))
        expected = court_import._recovery_file_snapshot(self.live)
        before = self.snapshot()
        with tempfile.TemporaryFile() as target, self.assertRaisesRegex(ValueError, "copy does not match"):
            court_import._copy_recovery_file(self.live, expected, CorruptingTarget(target))
        self.assertEqual(self.snapshot(), before)

    def test_reported_errors_after_live_or_record_commit_resume_without_losing_files(self):
        for committed in (self.live, self.journal):
            self.held.write_bytes(b"original user floor"); self.live.write_bytes(b"current floor sentinel")
            self.journal.write_text(json.dumps(self.state), encoding="utf-8")
            actual = export_io.os.replace
            def replace(source, target):
                actual(source, target)
                if Path(target) == committed:
                    raise OSError(112, "controlled error after commit")
            count = len(list(self.base.parent.glob("interrupted-floor-*.iff")))
            with self.subTest(committed=committed.name), patch.object(export_io.os, "replace", side_effect=replace), self.assertRaisesRegex(OSError, "after commit"):
                court_import._recover_base_extraction(self.base)
            self.assertEqual(self.held.read_bytes(), b"original user floor")
            self.assertEqual(self.live.read_bytes(), b"original user floor")
            self.assertTrue(self.journal.exists())
            court_import._recover_base_extraction(self.base)
            self.assertFalse(self.held.exists()); self.assertFalse(self.journal.exists())
            self.assertEqual(self.live.read_bytes(), b"original user floor")
            self.assertEqual(len(list(self.base.parent.glob("interrupted-floor-*.iff"))), count + 1)
            self.assertFalse(list(self.base.parent.glob("*.tmp")))
            self.assertFalse(list(self.live.parent.glob("*.tmp")))

    def test_replaced_preservation_filename_is_not_published_or_deleted(self):
        actual = court_import._recovery_file_snapshot; foreign = None; moved = None
        def snapshot(path, **options):
            nonlocal foreign, moved
            if path.name.startswith("interrupted-floor-") and foreign is None:
                foreign = path
                moved = foreign.with_suffix(".owned-copy")
                foreign.rename(moved); foreign.write_bytes(b"foreign preservation filename")
            return actual(path, **options)
        before = self.snapshot()
        with patch.object(court_import, "_recovery_file_snapshot", side_effect=snapshot), self.assertRaisesRegex(ValueError, "does not match") as caught:
            court_import._recover_base_extraction(self.base)
        self.assertEqual(self.snapshot(), before)
        self.assertIsNotNone(foreign)
        self.assertEqual(foreign.read_bytes(), b"foreign preservation filename")
        self.assertEqual(moved.read_bytes(), b"current floor sentinel")
        self.assertIn(str(foreign), "\n".join(caught.exception.__notes__))

    @unittest.skipUnless(os.name == "nt", "Windows retry boundary")
    def test_retry_time_record_backup_and_live_edits_are_not_overwritten(self):
        for changed in (self.journal, self.held, self.live):
            self.held.write_bytes(b"original user floor"); self.live.write_bytes(b"current floor sentinel")
            self.journal.write_text(json.dumps(self.state), encoding="utf-8")
            before = self.snapshot()
            conflict = json.dumps(dict(self.state, extra="external edit")).encode() if changed == self.journal else b"X" * len(before[changed][0])
            actual = export_io.os.replace; attempts = []
            def replace(source, target):
                if Path(target) == self.live:
                    attempts.append(target)
                    failure = OSError(13, "controlled sharing failure"); failure.winerror = 32; raise failure
                return actual(source, target)
            def edit(_delay):
                changed.write_bytes(conflict)
                os.utime(changed, ns=(changed.stat().st_atime_ns, before[changed][1]))
            with self.subTest(changed=changed.name), patch.object(export_io.os, "replace", side_effect=replace), patch.object(export_io.time, "sleep", side_effect=edit), self.assertRaisesRegex(ValueError, "changed while recovering"):
                court_import._recover_base_extraction(self.base)
            self.assertEqual(attempts, [self.live])
            for path, expected in before.items():
                self.assertEqual((path.read_bytes(), path.stat().st_mtime_ns), (conflict, expected[1]) if path == changed else expected)
            self.assertFalse(list(self.live.parent.glob("*.tmp")))

    @unittest.skipUnless(os.name == "nt", "Real Windows recovery record lock")
    def test_locked_completion_record_retains_backup_and_can_resume(self):
        before = self.journal.read_bytes(), self.journal.stat().st_mtime_ns
        with locked_destination(self.journal), self.assertRaises(OSError):
            court_import._recover_base_extraction(self.base)
        self.assertEqual(self.live.read_bytes(), b"original user floor")
        self.assertEqual(self.held.read_bytes(), b"original user floor")
        self.assertEqual((self.journal.read_bytes(), self.journal.stat().st_mtime_ns), before)
        court_import._recover_base_extraction(self.base)
        self.assertEqual(self.live.read_bytes(), b"original user floor")
        self.assertFalse(self.held.exists()); self.assertFalse(self.journal.exists())
        self.assertEqual(len(list(self.base.parent.glob("interrupted-floor-*.iff"))), 1)
        self.assertFalse(list(self.base.parent.glob("*.tmp")))

    @unittest.skipUnless(os.name == "nt", "Real Windows recovery cleanup lock")
    def test_record_cleanup_lock_resumes_without_a_backup_or_another_restore(self):
        actual = court_import._remove_recovery_file
        def remove(path, expected, validate):
            if path == self.journal:
                with locked_destination(path):
                    return actual(path, expected, validate)
            return actual(path, expected, validate)
        with patch.object(court_import, "_remove_recovery_file", side_effect=remove), self.assertRaises(OSError):
            court_import._recover_base_extraction(self.base)
        self.assertFalse(self.held.exists()); self.assertTrue(self.journal.exists())
        before = self.live.read_bytes(), self.live.stat().st_mtime_ns
        with patch.object(court_import, "_copy_recovery_file", side_effect=AssertionError("Completed restore must not copy again")):
            court_import._recover_base_extraction(self.base)
        self.assertEqual((self.live.read_bytes(), self.live.stat().st_mtime_ns), before)
        self.assertFalse(self.journal.exists())
        self.assertEqual(len(list(self.base.parent.glob("interrupted-floor-*.iff"))), 1)

    @unittest.skipUnless(os.name == "nt", "Windows cleanup retry")
    def test_cleanup_retry_preserves_an_externally_changed_backup(self):
        actual = Path.unlink; attempts = []
        def unlink(path, *args, **options):
            if path == self.held:
                attempts.append(path)
                failure = OSError(13, "controlled cleanup lock"); failure.winerror = 32; raise failure
            return actual(path, *args, **options)
        def edit(_delay):
            self.held.write_bytes(b"external backup sentinel")
        with patch.object(Path, "unlink", unlink), patch.object(court_import.time, "sleep", side_effect=edit), self.assertRaisesRegex(ValueError, "cleanup target changed"):
            court_import._recover_base_extraction(self.base)
        self.assertEqual(attempts, [self.held])
        self.assertEqual(self.held.read_bytes(), b"external backup sentinel")
        self.assertEqual(self.live.read_bytes(), b"original user floor")
        self.assertTrue(self.journal.exists())

    def test_oversized_backup_or_current_floor_is_rejected_before_copying(self):
        for path in (self.held, self.live):
            before = self.snapshot()
            with self.subTest(path=path.name), patch.object(court_import, "MAX_RECOVERY_FILE_BYTES", len(before[path][0]) - 1), self.assertRaisesRegex(ValueError, "recovery size limit"):
                court_import._recover_base_extraction(self.base)
            self.assertEqual(self.snapshot(), before)
            self.assertFalse(list(self.base.parent.glob("interrupted-floor-*.iff")))

    @unittest.skipUnless(os.name == "nt", "Real Windows recovery junctions")
    def test_linked_live_or_cache_folders_are_rejected(self):
        for directory in (self.live.parent, self.base.parent):
            saved = directory.with_name(directory.name + "-ordinary")
            directory.rename(saved)
            result = subprocess.run(["cmd", "/c", "mklink", "/J", str(directory), str(saved)], capture_output=True, text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            try:
                before = self.snapshot()
                with self.assertRaisesRegex(ValueError, "linked folders"):
                    court_import._recover_base_extraction(self.base)
                self.assertEqual(self.snapshot(), before)
            finally:
                directory.rmdir(); saved.rename(directory)


if __name__ == "__main__":
    unittest.main()
