from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager, ExitStack
import ctypes
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

from PIL import Image
from court_creator import court_template, export_io


@contextmanager
def locked_destination(path):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel.CloseHandle.restype = ctypes.c_int
    handle = kernel.CreateFileW(str(path), 0x80000000, 1, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    closed = False
    def release():
        nonlocal closed
        if not closed:
            closed = True
            if not kernel.CloseHandle(handle):
                raise ctypes.WinError(ctypes.get_last_error())
    try:
        yield release
    finally:
        release()


class ExportPublicationTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="court-export-publish-")
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        self.output = self.root / "court.png"
        self.staging = self.root / "owned-stage.tmp"
        self.output.write_bytes(b"old export")
        self.staging.write_bytes(b"new export")

    def test_stream_writer_keeps_creation_handle_and_closes_before_publication(self):
        borrowed = None
        checks = []
        def validate():
            self.assertTrue(borrowed.closed)
            checks.append(True)
        with patch.object(export_io, "staging_identity", side_effect=AssertionError("Creation identity must come from the handle")):
            with export_io.staged_export(self.output, validate=validate, writable_stream=True) as stream:
                borrowed = stream
                stage = Path(stream.name)
                handle_info = os.fstat(stream.fileno())
                name_info = stage.stat()
                self.assertEqual((handle_info.st_dev, handle_info.st_ino), (name_info.st_dev, name_info.st_ino))
                stream.write(b"complete stream export")
        self.assertEqual(self.output.read_bytes(), b"complete stream export")
        self.assertTrue(borrowed.closed)
        self.assertEqual(checks, [True])
        self.assertFalse(stage.exists())

    def test_stream_write_sync_publication_and_interrupt_failures_preserve_output(self):
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        for phase in ("write", "sync", "publication", "interrupt"):
            primary = KeyboardInterrupt("cancel stream export") if phase == "interrupt" else OSError(112, "original " + phase + " failure")
            with self.subTest(phase=phase), ExitStack() as faults:
                if phase == "sync":
                    faults.enter_context(patch.object(export_io.os, "fsync", side_effect=primary))
                elif phase == "publication":
                    faults.enter_context(patch.object(export_io.os, "replace", side_effect=primary))
                with self.assertRaises(type(primary)) as result:
                    with export_io.staged_export(self.output, writable_stream=True) as stream:
                        stage = Path(stream.name)
                        stream.write(b"incomplete stream export")
                        if phase in ("write", "interrupt"):
                            raise primary
                self.assertIs(result.exception, primary)
                self.assertTrue(stream.closed)
                self.assertFalse(stage.exists())
                self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)

    def test_stream_close_failure_does_not_mask_an_existing_failure(self):
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        primary = ValueError("original stream render failure")
        close_error = OSError(112, "controlled close failure")
        with self.assertRaises(ValueError) as result:
            with export_io.staged_export(self.output, writable_stream=True) as stream:
                stage = Path(stream.name)
                stream.write(b"incomplete export")
                close = stream.close
                def fail_close():
                    close()
                    raise close_error
                stream.close = fail_close
                raise primary
        self.assertIs(result.exception, primary)
        self.assertTrue(any("close failed" in note for note in primary.__notes__))
        self.assertTrue(stream.closed)
        self.assertFalse(stage.exists())
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)

    def test_stream_close_failure_without_prior_error_is_reported(self):
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        close_error = OSError(112, "controlled close failure")
        with self.assertRaises(OSError) as result:
            with export_io.staged_export(self.output, writable_stream=True) as stream:
                stage = Path(stream.name)
                stream.write(b"complete but uncommitted export")
                close = stream.close
                def fail_close():
                    close()
                    raise close_error
                stream.close = fail_close
        self.assertIs(result.exception, close_error)
        self.assertTrue(stream.closed)
        self.assertFalse(stage.exists())
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)

    @unittest.skipUnless(os.name == "nt", "Real Windows staging-file lock")
    def test_cleanup_failure_keeps_original_error_and_previous_export(self):
        primary = ValueError("original render failure")
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        foreign = self.root / ".foreign.tmp"; foreign.write_bytes(b"personal staging")
        with ExitStack() as locks:
            with self.assertRaises(ValueError) as result:
                with export_io.staged_export(self.output) as staging:
                    staging.write_bytes(b"incomplete export")
                    locks.enter_context(locked_destination(staging))
                    raise primary
            self.assertIs(result.exception, primary)
            self.assertTrue(staging.exists())
            self.assertTrue(any(str(staging) in note and "cleanup" in note.lower() for note in primary.__notes__))
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
        self.assertEqual(foreign.read_bytes(), b"personal staging")
        staging.unlink()

    @unittest.skipUnless(os.name == "nt", "Windows cleanup retry codes")
    def test_cleanup_retries_are_bounded_and_unrelated_errors_are_not_retried(self):
        for code, count in ((5, 4), (32, 4), (33, 4), (112, 1), (1176, 1)):
            primary = ValueError("original failure")
            cleanup = OSError(13, "cleanup failure"); cleanup.winerror = code
            with self.subTest(code=code), patch.object(Path, "unlink", side_effect=cleanup) as unlink, patch.object(export_io.time, "sleep") as sleep:
                export_io.cleanup_staging(self.staging, primary)
                self.assertEqual(unlink.call_count, count)
                self.assertEqual([call.args[0] for call in sleep.call_args_list], [.025, .05, .1] if count == 4 else [])
                self.assertEqual(str(primary), "original failure")
                self.assertEqual(len(primary.__notes__), 1)
                self.assertIn(str(self.staging), primary.__notes__[0])
            self.assertEqual(self.staging.read_bytes(), b"new export")

    def test_cleanup_without_primary_failure_does_not_silently_succeed(self):
        cleanup = OSError(112, "cleanup disk failure")
        with patch.object(Path, "unlink", side_effect=cleanup), self.assertRaises(OSError) as result:
            export_io.cleanup_staging(self.staging)
        self.assertIs(result.exception, cleanup)
        self.assertEqual(self.staging.read_bytes(), b"new export")
        missing = self.root / "already-published.tmp"
        export_io.cleanup_staging(missing)
        self.assertFalse(missing.exists())

    @unittest.skipUnless(os.name == "nt", "Real temporary Windows staging lock")
    def test_temporary_cleanup_lock_recovers_without_hiding_render_failure(self):
        primary = ValueError("original render failure")
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        worker = None
        with ExitStack() as locks:
            try:
                with self.assertRaises(ValueError) as result:
                    with export_io.staged_export(self.output) as staging:
                        staging.write_bytes(b"incomplete export")
                        release = locks.enter_context(locked_destination(staging))
                        worker = threading.Thread(target=lambda: (time.sleep(.04), release()))
                        worker.start()
                        raise primary
                self.assertIs(result.exception, primary)
                self.assertFalse(staging.exists())
                self.assertFalse(getattr(primary, "__notes__", []))
            finally:
                if worker is not None:
                    worker.join(timeout=2)
                    self.assertFalse(worker.is_alive())
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)

    def test_publication_sync_and_interrupt_errors_survive_cleanup_failure(self):
        actual_unlink = Path.unlink
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        for phase in ("publication", "sync", "interrupt"):
            primary = KeyboardInterrupt("cancel export") if phase == "interrupt" else OSError(112, "original " + phase + " failure")
            cleanup = OSError(112, "cleanup failure")
            owned = None
            def unlink(path, *args, **kwargs):
                if path == owned:
                    raise cleanup
                return actual_unlink(path, *args, **kwargs)
            with self.subTest(phase=phase), patch.object(Path, "unlink", unlink), ExitStack() as faults:
                if phase == "publication":
                    faults.enter_context(patch.object(export_io.os, "replace", side_effect=primary))
                elif phase == "sync":
                    faults.enter_context(patch.object(export_io.os, "fsync", side_effect=primary))
                with self.assertRaises(type(primary)) as result:
                    with export_io.staged_export(self.output) as staging:
                        owned = staging
                        staging.write_bytes(b"incomplete export")
                        if phase == "interrupt":
                            raise primary
                self.assertIs(result.exception, primary)
                self.assertTrue(staging.exists())
                self.assertIn("cleanup", primary.__notes__[0])
            self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
            staging.unlink()

    def test_replaced_staging_path_is_retained_instead_of_deleted(self):
        primary = ValueError("original render failure")
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        moved = self.root / "original-stage-retained.tmp"
        with self.assertRaises(ValueError) as result:
            with export_io.staged_export(self.output) as staging:
                staging.write_bytes(b"original partial export")
                staging.rename(moved)
                staging.write_bytes(b"foreign replacement")
                raise primary
        self.assertIs(result.exception, primary)
        self.assertIn("identity changed", primary.__notes__[0])
        self.assertEqual(staging.read_bytes(), b"foreign replacement")
        self.assertEqual(moved.read_bytes(), b"original partial export")
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)

    def test_replaced_staging_file_cannot_be_published(self):
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        moved = self.root / "original-stage-retained.tmp"
        with self.assertRaisesRegex(OSError, "identity changed"):
            with export_io.staged_export(self.output) as staging:
                staging.write_bytes(b"complete owned export")
                staging.rename(moved)
                staging.write_bytes(b"foreign replacement")
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
        self.assertEqual(staging.read_bytes(), b"foreign replacement")
        self.assertEqual(moved.read_bytes(), b"complete owned export")

    def test_shared_staging_file_cannot_publish_or_touch_its_alias(self):
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        alias = self.root / "personal-stage-alias.tmp"
        with patch.object(export_io.os, "fsync") as sync, patch.object(export_io.os, "replace") as replace:
            with self.assertRaisesRegex(OSError, "became shared"):
                with export_io.staged_export(self.output) as staging:
                    staging.write_bytes(b"complete owned export")
                    os.link(staging, alias)
                    shared = staging.read_bytes(), staging.stat().st_mtime_ns
            sync.assert_not_called(); replace.assert_not_called()
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
        self.assertEqual((staging.read_bytes(), staging.stat().st_mtime_ns), shared)
        self.assertEqual((alias.read_bytes(), alias.stat().st_mtime_ns), shared)

    def test_opened_sync_handle_must_match_the_owned_staging_file(self):
        personal = self.root / "personal.txt"; personal.write_bytes(b"protected personal bytes")
        protected = personal.read_bytes(), personal.stat().st_mtime_ns
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        actual_open = Path.open; owned = None
        def redirect(path, *args, **kwargs):
            if path == owned and args and args[0] == "r+b":
                return actual_open(personal, *args, **kwargs)
            return actual_open(path, *args, **kwargs)
        with patch.object(Path, "open", redirect), patch.object(export_io.os, "fsync") as sync, patch.object(export_io.os, "replace") as replace:
            with self.assertRaisesRegex(OSError, "identity changed"):
                with export_io.staged_export(self.output, sources=(personal,)) as staging:
                    owned = staging; staging.write_bytes(b"complete owned export")
            sync.assert_not_called(); replace.assert_not_called()
        self.assertEqual((personal.read_bytes(), personal.stat().st_mtime_ns), protected)
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
        self.assertFalse(staging.exists())

    @unittest.skipUnless(os.name == "nt", "Windows publication retry boundary")
    def test_retry_time_staging_replacement_stops_before_second_publication(self):
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        moved = self.root / "original-stage-retained.tmp"
        error = OSError(13, "controlled sharing failure"); error.winerror = 32
        owned = None
        def substitute(_delay):
            owned.rename(moved); owned.write_bytes(b"foreign replacement")
        with patch.object(export_io.os, "replace", side_effect=error) as replace, patch.object(export_io.time, "sleep", side_effect=substitute) as sleep:
            with self.assertRaisesRegex(OSError, "identity changed"):
                with export_io.staged_export(self.output) as staging:
                    owned = staging; staging.write_bytes(b"complete owned export")
            replace.assert_called_once(); sleep.assert_called_once_with(.025)
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
        self.assertEqual(staging.read_bytes(), b"foreign replacement")
        self.assertEqual(moved.read_bytes(), b"complete owned export")

    def test_validation_callback_cannot_replace_stage_or_alias_output_to_source(self):
        personal = self.root / "personal.txt"; personal.write_bytes(b"protected personal bytes")
        protected = personal.read_bytes(), personal.stat().st_mtime_ns
        for change in ("stage", "output"):
            self.output.write_bytes(b"last good export")
            before = self.output.read_bytes(), self.output.stat().st_mtime_ns
            moved = self.root / (change + "-retained.tmp"); owned = None
            def validate():
                if change == "stage":
                    owned.rename(moved); owned.write_bytes(b"foreign replacement")
                else:
                    self.output.rename(moved); os.link(personal, self.output)
            expected = OSError if change == "stage" else ValueError
            message = "identity changed" if change == "stage" else "source assets"
            with self.subTest(change=change), patch.object(export_io.os, "replace") as replace:
                with self.assertRaisesRegex(expected, message):
                    with export_io.staged_export(self.output, sources=(personal,), validate=validate) as staging:
                        owned = staging; staging.write_bytes(b"complete owned export")
                replace.assert_not_called()
            self.assertEqual((personal.read_bytes(), personal.stat().st_mtime_ns), protected)
            if change == "stage":
                self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
                self.assertEqual(staging.read_bytes(), b"foreign replacement")
                self.assertEqual(moved.read_bytes(), b"complete owned export")
            else:
                self.assertTrue(self.output.samefile(personal))
                self.assertEqual(moved.read_bytes(), b"last good export")
                self.assertFalse(staging.exists())

    def test_replaced_png_stage_never_changes_the_previous_export(self):
        image = Image.new("RGBA", (8, 4), (10, 20, 30, 255)); self.addCleanup(image.close)
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        moved = self.root / "rendered-owned-stage.png"; retained = []
        actual_save = Image.Image.save
        def save(bitmap, path, *args, **kwargs):
            actual_save(bitmap, path, *args, **kwargs)
            stage = Path(path); stage.rename(moved); stage.write_bytes(b"foreign replacement")
            retained.append(stage)
        with patch.object(Image.Image, "save", save), self.assertRaisesRegex(OSError, "identity changed"):
            court_template._save_png_atomic(image, self.output, fast=True)
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
        self.assertEqual(len(retained), 1)
        self.assertEqual(retained[0].read_bytes(), b"foreign replacement")
        with Image.open(moved) as rendered:
            self.assertEqual(rendered.size, (8, 4))
            self.assertEqual(rendered.getpixel((0, 0)), (10, 20, 30, 255))

    def test_symlink_staging_is_not_followed_or_published(self):
        personal = self.root / "personal.txt"; personal.write_bytes(b"protected personal bytes")
        protected = personal.read_bytes(), personal.stat().st_mtime_ns
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        moved = self.root / "original-stage-retained.tmp"
        with patch.object(export_io.os, "fsync") as sync, patch.object(export_io.os, "replace") as replace:
            with self.assertRaisesRegex(OSError, "identity changed"):
                with export_io.staged_export(self.output, sources=(personal,)) as staging:
                    staging.write_bytes(b"complete owned export"); staging.rename(moved)
                    try:
                        staging.symlink_to(personal)
                    except OSError as error:
                        self.skipTest("File symlinks unavailable: " + str(error))
            sync.assert_not_called(); replace.assert_not_called()
        self.assertTrue(staging.is_symlink())
        self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
        self.assertEqual((personal.read_bytes(), personal.stat().st_mtime_ns), protected)
        self.assertEqual(moved.read_bytes(), b"complete owned export")

    @unittest.skipUnless(os.name == "nt", "Real Windows directory junction")
    def test_junction_staging_is_not_followed_published_or_deleted(self):
        outside = self.root / "personal-folder"; outside.mkdir()
        personal = outside / "personal.txt"; personal.write_bytes(b"protected personal bytes")
        protected = personal.read_bytes(), personal.stat().st_mtime_ns
        before = self.output.read_bytes(), self.output.stat().st_mtime_ns
        moved = self.root / "original-stage-retained.tmp"
        staging = None
        try:
            with patch.object(export_io.os, "fsync") as sync, patch.object(export_io.os, "replace") as replace:
                with self.assertRaisesRegex(OSError, "identity changed") as result:
                    with export_io.staged_export(self.output, sources=(personal,)) as staging:
                        staging.write_bytes(b"complete owned export"); staging.rename(moved)
                        linked = subprocess.run(["cmd", "/c", "mklink", "/J", str(staging), str(outside)], capture_output=True, text=True)
                        self.assertEqual(linked.returncode, 0, linked.stdout + linked.stderr)
                sync.assert_not_called(); replace.assert_not_called()
            self.assertTrue(staging.is_junction())
            self.assertIn("cleanup", result.exception.__notes__[0])
            self.assertEqual((self.output.read_bytes(), self.output.stat().st_mtime_ns), before)
            self.assertEqual((personal.read_bytes(), personal.stat().st_mtime_ns), protected)
            self.assertEqual(moved.read_bytes(), b"complete owned export")
            self.assertEqual(list(outside.iterdir()), [personal])
        finally:
            if staging is not None and staging.is_junction():
                staging.rmdir()

    @unittest.skipUnless(os.name == "nt", "Windows cleanup retry boundary")
    def test_cleanup_rechecks_identity_after_a_transient_lock(self):
        primary = ValueError("original failure")
        identity = export_io.staging_identity(self.staging)
        moved = self.root / "original-stage-retained.tmp"
        cleanup = OSError(13, "sharing failure"); cleanup.winerror = 32
        def replace_during_retry(_delay):
            self.staging.rename(moved)
            self.staging.write_bytes(b"foreign replacement")
        with patch.object(Path, "unlink", side_effect=cleanup) as unlink, patch.object(export_io.time, "sleep", side_effect=replace_during_retry) as sleep:
            export_io.cleanup_staging(self.staging, primary, identity=identity)
            unlink.assert_called_once(); sleep.assert_called_once_with(.025)
        self.assertIn("identity changed", primary.__notes__[0])
        self.assertEqual(self.staging.read_bytes(), b"foreign replacement")
        self.assertEqual(moved.read_bytes(), b"new export")

    def test_shared_staging_path_and_alias_remain_unchanged(self):
        alias = self.root / "personal-stage-alias.tmp"
        identity = export_io.staging_identity(self.staging)
        os.link(self.staging, alias)
        before = self.staging.read_bytes(), self.staging.stat().st_mtime_ns
        primary = ValueError("original failure")
        export_io.cleanup_staging(self.staging, primary, identity=identity)
        self.assertIn("became shared", primary.__notes__[0])
        self.assertEqual((self.staging.read_bytes(), self.staging.stat().st_mtime_ns), before)
        self.assertEqual((alias.read_bytes(), alias.stat().st_mtime_ns), before)
        with self.assertRaisesRegex(OSError, "became shared"):
            export_io.cleanup_staging(self.staging, identity=identity)
        self.assertTrue(self.staging.exists() and alias.exists())

    def test_reused_staging_filename_after_commit_preserves_both_files(self):
        actual_replace = os.replace
        retained = []
        def publish(source, destination):
            actual_replace(source, destination)
            path = Path(source); path.write_bytes(b"foreign replacement")
            retained.append(path)
        with patch.object(export_io.os, "replace", side_effect=publish), self.assertRaisesRegex(OSError, "identity changed"):
            with export_io.staged_export(self.output) as staging:
                staging.write_bytes(b"complete export")
        self.assertEqual(self.output.read_bytes(), b"complete export")
        self.assertEqual(retained, [staging])
        self.assertEqual(staging.read_bytes(), b"foreign replacement")

    @unittest.skipUnless(os.name == "nt", "Win32 transient access errors")
    def test_bounded_retry_preserves_final_error_and_does_not_retry_other_failures(self):
        for code, count in ((5, 4), (32, 4), (33, 4), (112, 1), (1176, 1)):
            error = OSError(13, "controlled publication error")
            error.winerror = code
            with self.subTest(code=code), patch.object(export_io.os, "replace", side_effect=error) as replace, patch.object(export_io.time, "sleep") as sleep:
                with self.assertRaises(OSError) as result:
                    export_io._publish_export(self.staging, self.output, ())
                self.assertIs(result.exception, error)
                self.assertEqual(replace.call_count, count)
                self.assertEqual([call.args[0] for call in sleep.call_args_list], [.025, .05, .1] if count == 4 else [])
            self.assertEqual(self.output.read_bytes(), b"old export")
        self.staging.unlink()
        error.winerror = 5
        with patch.object(export_io.os, "replace", side_effect=error) as replace, patch.object(export_io.time, "sleep") as sleep:
            with self.assertRaises(OSError):
                export_io._publish_export(self.staging, self.output, ())
            replace.assert_not_called(); sleep.assert_not_called()

    @unittest.skipUnless(os.name == "nt", "Win32 transient access errors")
    def test_source_alias_is_rechecked_after_transient_failure(self):
        source = self.root / "source.png"
        source.write_bytes(b"protected artwork")
        error = OSError(13, "controlled sharing failure")
        error.winerror = 32
        def change_target(_delay):
            self.output.unlink()
            os.link(source, self.output)
        with patch.object(export_io.os, "replace", side_effect=error) as replace, patch.object(export_io.time, "sleep", side_effect=change_target):
            with self.assertRaisesRegex(ValueError, "source assets"):
                export_io._publish_export(self.staging, self.output, (source,))
            replace.assert_called_once()
        self.assertEqual(source.read_bytes(), b"protected artwork")
        self.assertEqual(self.staging.read_bytes(), b"new export")

    @unittest.skipUnless(os.name == "nt", "Real Windows destination sharing locks")
    def test_real_temporary_lock_recovers_and_persistent_lock_preserves_output(self):
        image = Image.new("RGBA", (8, 4), (10, 20, 30, 255))
        self.addCleanup(image.close)
        with locked_destination(self.output) as release:
            worker = threading.Thread(target=lambda: (time.sleep(.04), release()))
            worker.start()
            try:
                court_template._save_png_atomic(image, self.output, fast=True)
            finally:
                worker.join(timeout=2)
            self.assertFalse(worker.is_alive())
        with Image.open(self.output) as rendered:
            self.assertEqual(rendered.getpixel((0, 0)), (10, 20, 30, 255))
        before = self.output.read_bytes()
        with locked_destination(self.output):
            with self.assertRaises(PermissionError):
                court_template._save_png_atomic(image, self.output, fast=True)
        self.assertEqual(self.output.read_bytes(), before)
        self.assertEqual(list(self.root.glob(".court.png.*.tmp")), [])

    def test_repeated_concurrent_png_publication_leaves_complete_images_and_no_staging_files(self):
        images = [Image.new("RGBA", (8, 4), color) for color in ((30, 60, 90, 255), (80, 50, 20, 255))]
        for image in images:
            self.addCleanup(image.close)
        colors = {image.getpixel((0, 0)) for image in images}
        with ThreadPoolExecutor(max_workers=2) as pool:
            for _ in range(100):
                list(pool.map(lambda image: court_template._save_png_atomic(image, self.output, fast=True), images))
                with Image.open(self.output) as rendered:
                    self.assertEqual(rendered.size, (8, 4))
                    self.assertIn(rendered.getpixel((0, 0)), colors)
        self.assertEqual(list(self.root.glob(".court.png.*.tmp")), [])


if __name__ == "__main__":
    unittest.main()
