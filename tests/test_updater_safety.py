from contextlib import redirect_stderr
from concurrent.futures import ThreadPoolExecutor
from io import BytesIO, StringIO
import json
import os
from pathlib import Path
import stat
import struct
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
import warnings
import zipfile

import updater


class Response(BytesIO):
    def __init__(self, data, *, length=None, url=updater.RELEASE_URL):
        super().__init__(data)
        self.headers = {} if length is None else {"Content-Length": str(length)}
        self.url = url
        self.read_sizes = []

    def geturl(self):
        return self.url

    def read(self, size=-1):
        self.read_sizes.append(size)
        return super().read(size)


class UpdaterSafetyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="court-update-safety-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.updates = self.root / "updates"
        self.addCleanup(patch.stopall)
        patch.object(updater, "ROOT", self.root).start()
        patch.object(updater, "UPDATES", self.updates).start()
        (self.root / "package.json").write_text(json.dumps({"version": "1.0.0", "devDependencies": {"electron": "same"}}))
        (self.root / "studio-build.json").write_text(json.dumps({"runtime": "wpf-net8"}))
        (self.root / "requirements.txt").write_text("Pillow>=10.4,<13\nShapely>=2.1,<3\n")
        (self.root / "desktop").mkdir()
        (self.root / "desktop/NBA2KCourtCreator.exe").write_bytes(b"old application")
        (self.root / "logos").mkdir()
        (self.root / "logos/personal.png").write_bytes(b"personal artwork")

    def pending(self, name="pending"):
        directory = self.updates / name
        (directory / "desktop").mkdir(parents=True)
        (directory / "desktop/NBA2KCourtCreator.exe").write_bytes(b"new application")
        for name in updater.NATIVE_FILES[1:]:
            (directory / "desktop" / name).write_bytes(b"native update sentinel")
        (directory / "desktop/NBA2KCourtCreator.runtimeconfig.json").write_text(json.dumps(self.runtime_config()))
        (directory / "studio-build.json").write_bytes((self.root / "studio-build.json").read_bytes())
        (directory / "requirements.txt").write_bytes((self.root / "requirements.txt").read_bytes())
        (directory / "package.json").write_text(json.dumps({"version": "1.1.0", "devDependencies": {"electron": "same"}}))
        updater.seal_inventory(directory)
        return directory

    def runtime_config(self):
        return {"runtimeOptions": {"frameworks": [{"name": name, "version": "8.0.0"}
                for name in ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")]}}

    def snapshot(self, directory):
        return {path.relative_to(directory).as_posix(): (path.read_bytes(), path.stat().st_mtime_ns)
                for path in directory.rglob("*") if path.is_file()}

    def archive(self, extras=(), *, version="1.1.0"):
        data = BytesIO()
        rewrites = []
        with warnings.catch_warnings(), zipfile.ZipFile(data, "w", zipfile.ZIP_DEFLATED) as archive:
            warnings.simplefilter("ignore", UserWarning)
            archive.writestr("package.json", json.dumps({"version": version, "devDependencies": {"electron": "same"}}))
            archive.writestr("studio-build.json", (self.root / "studio-build.json").read_bytes())
            archive.writestr("requirements.txt", (self.root / "requirements.txt").read_bytes())
            archive.writestr("desktop/NBA2KCourtCreator.exe", b"new application")
            for name in updater.NATIVE_FILES[1:]:
                content = json.dumps(self.runtime_config()) if name.endswith("runtimeconfig.json") else b"native update sentinel"
                archive.writestr("desktop/" + name, content)
            for name, content in extras:
                if isinstance(name, str) and ("\\" in name or "\x00" in name):
                    safe = name.replace("\\", "/").replace("\x00", "_")
                    rewrites.append((safe.encode(), name.encode()))
                    archive.writestr(safe, content)
                else:
                    archive.writestr(name, content)
        encoded = data.getvalue()
        # The ZIP writer normalizes separators/NULs; restore raw hostile headers.
        for safe, original in rewrites:
            self.assertEqual(len(safe), len(original))
            encoded = encoded.replace(safe, original)
        return encoded

    def stage(self, data, *, tag="v1.1.0", transfer_error=None):
        release = {"tag_name": tag, "assets": [{"name": "court-creator-update.zip", "size": len(data),
                   "browser_download_url": "https://github.com" + updater.DOWNLOAD_PREFIX + tag + "/court-creator-update.zip"}]}
        def download(_url, output, limit):
            self.assertEqual(limit, len(data))
            output.write(data[:12] if transfer_error else data)
            if transfer_error:
                raise transfer_error
            return len(data)
        with patch.object(updater, "fetch", return_value=json.dumps(release).encode()), patch.object(updater, "transfer", side_effect=download):
            updater.stage()

    def test_downloads_are_chunked_bounded_and_validate_lengths_and_redirects(self):
        response = Response(b"x" * 150000, length=150000)
        target = BytesIO()
        with patch.object(updater, "urlopen", return_value=response):
            self.assertEqual(updater.transfer(updater.RELEASE_URL, target, 150000), 150000)
        self.assertEqual(len(target.getvalue()), 150000)
        self.assertTrue(all(0 < size <= updater.CHUNK_BYTES for size in response.read_sizes))
        cases = [(b"12345", None, 4, updater.RELEASE_URL, "size limit"),
                 (b"123", 10, 20, updater.RELEASE_URL, "Incomplete"),
                 (b"", 21, 20, updater.RELEASE_URL, "size limit"),
                 (b"", "-1", 20, updater.RELEASE_URL, "size limit"),
                 (b"", None, 20, "http://github.com/file.zip", "redirect"),
                 (b"", None, 20, "https://example.invalid/file.zip", "redirect")]
        for data, length, limit, url, message in cases:
            with self.subTest(url=url, length=length), patch.object(updater, "urlopen", return_value=Response(data, length=length, url=url)):
                with self.assertRaisesRegex(ValueError, message):
                    updater.transfer(updater.RELEASE_URL, BytesIO(), limit)
        with patch.object(updater, "urlopen") as opened:
            with self.assertRaises(ValueError):
                updater.transfer("file:///private", BytesIO(), 100)
            opened.assert_not_called()

    def test_download_has_a_total_deadline_and_releases_response_on_failure(self):
        response = Response(b"payload")
        with patch.object(updater, "urlopen", return_value=response), patch.object(updater.time, "monotonic", side_effect=[0, 0, updater.TRANSFER_SECONDS + 1]):
            with self.assertRaisesRegex(TimeoutError, "total time"):
                updater.transfer(updater.RELEASE_URL, BytesIO(), 100)
        self.assertTrue(response.closed)

    def test_archive_rejects_windows_aliases_duplicates_links_and_collisions_before_extracting(self):
        names = ["../escape", "desktop/../escape", "C:/escape", "desktop\\escape", "desktop/file:stream",
                 "desktop/CON.txt", "desktop/file.", "desktop/file ", "desktop//file", "desktop///", "desktop/\x00hidden"]
        symlink = zipfile.ZipInfo("desktop/link")
        symlink.create_system = 3
        symlink.external_attr = (stat.S_IFLNK | 0o777) << 16
        cases = [[(name, b"bad")] for name in names]
        cases.extend([[('desktop/NBA2KCourtCreator.exe', b"duplicate")], [('desktop/nba2kcourtcreator.EXE', b"case duplicate")],
                      [(symlink, b"../outside")], [("tools/item", b"file"), ("tools/item/child", b"collision")]])
        for number, extra in enumerate(cases):
            archive = self.root / f"bad-{number}.zip"
            archive.write_bytes(self.archive(extra))
            destination = self.root / f"extract-{number}"
            destination.mkdir()
            with self.subTest(extra=str(extra)), self.assertRaises(ValueError):
                updater.extract_archive(archive, destination)
            self.assertEqual(list(destination.iterdir()), [])
        self.assertFalse((self.root.parent / "escape").exists())

    def test_archive_budgets_cover_directory_all_entries_and_expanded_data(self):
        archive = self.root / "budget.zip"
        archive.write_bytes(self.archive([("excluded.txt", b"x" * 500)]))
        for setting, limit in (("MAX_ARCHIVE_BYTES", 10), ("MAX_DIRECTORY_BYTES", 100),
                               ("MAX_FILE_BYTES", 499), ("MAX_EXPANDED_BYTES", 500), ("MAX_ENTRIES", 4)):
            destination = self.root / setting
            destination.mkdir()
            with self.subTest(setting=setting), patch.object(updater, setting, limit):
                with self.assertRaises(ValueError):
                    updater.extract_archive(archive, destination)
            self.assertEqual(list(destination.iterdir()), [])
        data = bytearray(archive.read_bytes())
        ending = data.rfind(zipfile.stringEndArchive)
        struct.pack_into("<HH", data, ending + 8, 1, 1)
        archive.write_bytes(data)
        with patch.object(updater, "MAX_ENTRIES", 4), patch.object(zipfile, "ZipInfo", wraps=zipfile.ZipInfo) as objects:
            with self.assertRaisesRegex(ValueError, "too many entries"):
                updater.extract_archive(archive, self.root / "forged")
            self.assertEqual(objects.call_count, 0)

    def test_stage_success_preserves_installed_app_artwork_and_replaces_pending_only_after_validation(self):
        previous = self.pending()
        (previous / "desktop/NBA2KCourtCreator.exe").write_bytes(b"previous valid pending")
        updater.seal_inventory(previous)
        installed = self.snapshot(self.root / "desktop")
        personal = self.snapshot(self.root / "logos")
        self.stage(self.archive([("runtime/python/python.exe", b"must not install"), ("logos/personal.png", b"must not replace")]))
        self.assertEqual((self.updates / "pending/desktop/NBA2KCourtCreator.exe").read_bytes(), b"new application")
        self.assertFalse((self.updates / "pending/runtime").exists())
        self.assertEqual(self.snapshot(self.root / "desktop"), installed)
        self.assertEqual(self.snapshot(self.root / "logos"), personal)
        self.assertFalse((self.updates / "pending.previous").exists())
        self.assertFalse(list(self.updates.glob("stage-*")))

    def test_stage_failures_preserve_previous_pending_and_remove_owned_scratch(self):
        previous = self.pending()
        before = self.snapshot(previous)
        cases = [(self.archive(), OSError("download interrupted")), (self.archive(version="1.2.0"), None),
                 (self.archive([("tools/file:stream", b"invalid")]), None), (b"not a zip", None)]
        for data, failure in cases:
            with self.subTest(failure=failure), self.assertRaises((OSError, ValueError, zipfile.BadZipFile)):
                self.stage(data, transfer_error=failure)
            self.assertEqual(self.snapshot(previous), before)
            self.assertFalse(list(self.updates.glob("stage-*")))

    def test_pending_publish_failure_restores_old_folder_and_crash_gap_is_recovered(self):
        pending = self.pending()
        (pending / "desktop/NBA2KCourtCreator.exe").write_bytes(b"old pending")
        updater.seal_inventory(pending)
        before = self.snapshot(pending)
        destination = self.pending("incoming")
        replace = updater.os.replace
        def fail_incoming(source, target):
            if Path(source) == destination:
                raise OSError("publish blocked")
            return replace(source, target)
        with patch.object(updater.os, "replace", side_effect=fail_incoming):
            with self.assertRaisesRegex(OSError, "publish blocked"):
                updater.publish_pending(destination)
        self.assertEqual(self.snapshot(pending), before)
        pending.rename(self.updates / "pending.previous")
        updater.apply()
        self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"old pending")
        self.assertFalse((self.updates / "pending.previous").exists())

    def test_cross_process_exclusive_lock_releases_and_stale_file_does_not_block(self):
        code = """import sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import updater
updater.ROOT = Path(sys.argv[2]); updater.UPDATES = updater.ROOT / 'updates'
try:
    with updater.update_lock():
        print('acquired')
except updater.UpdateBusy:
    print('busy')
"""
        command = [sys.executable, "-I", "-B", "-c", code, str(Path(updater.__file__).parent), str(self.root)]
        def child():
            return subprocess.run(command, capture_output=True, text=True, timeout=15, check=True,
                                  creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout.strip()
        with updater.update_lock():
            self.assertEqual(child(), "busy")
        self.assertEqual(child(), "acquired")
        self.assertTrue((self.updates / "operation.lock").exists())

    def test_linked_targets_and_update_roots_are_rejected_without_touching_external_files(self):
        if sys.platform != "win32":
            self.skipTest("Windows junction coverage")
        import _winapi
        pending = self.pending()
        with tempfile.TemporaryDirectory(prefix="court-update-external-") as external:
            external = Path(external)
            (external / "main.js").write_bytes(b"external personal file")
            (pending / "electron").mkdir()
            (pending / "electron/main.js").write_bytes(b"update")
            updater.seal_inventory(pending)
            junction = self.root / "electron"
            _winapi.CreateJunction(str(external), str(junction))
            try:
                with self.assertRaisesRegex(ValueError, "outside|Linked"):
                    updater.apply()
                self.assertEqual((external / "main.js").read_bytes(), b"external personal file")
                self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"old application")
            finally:
                junction.rmdir()
            updater.remove_tree(self.updates / "pending")
            (self.updates / "operation.lock").unlink()
            self.updates.rmdir()
            _winapi.CreateJunction(str(external), str(self.updates))
            try:
                with self.assertRaises(ValueError):
                    updater.stage()
                self.assertEqual(list(external.iterdir()), [external / "main.js"])
            finally:
                self.updates.rmdir()

    def test_hard_linked_target_and_unrecognized_rollback_are_preserved(self):
        self.pending()
        target = self.root / "desktop/NBA2KCourtCreator.exe"
        alias = self.root / "logos/alias"
        os.link(target, alias)
        with self.assertRaisesRegex(ValueError, "Hard-linked"):
            updater.apply()
        self.assertEqual(alias.read_bytes(), b"old application")
        alias.unlink()
        rollback = self.updates / "rollback"
        rollback.mkdir()
        (rollback / "personal.txt").write_bytes(b"unrecognized")
        with self.assertRaisesRegex(ValueError, "Unrecognized rollback"):
            updater.apply()
        self.assertEqual((rollback / "personal.txt").read_bytes(), b"unrecognized")
        self.assertEqual(target.read_bytes(), b"old application")

    def test_partial_apply_rolls_back_atomically_and_retains_foreign_temporary_file(self):
        pending = self.pending()
        (pending / "desktop/aaa-new.dll").write_bytes(b"new library")
        (pending / "tools").mkdir()
        (pending / "tools/fail.py").write_bytes(b"fail later")
        updater.seal_inventory(pending)
        (self.root / "tools").mkdir()
        foreign = self.root / "desktop/NBA2KCourtCreator.exe.update-tmp"
        foreign.write_bytes(b"foreign temporary")
        before = self.snapshot(self.root / "desktop")
        replace = updater.os.replace
        def fail_last(source, target):
            if Path(target) == self.root / "tools/fail.py":
                raise OSError("last file busy")
            return replace(source, target)
        with patch.object(updater.os, "replace", side_effect=fail_last):
            with self.assertRaisesRegex(OSError, "last file busy"):
                updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        self.assertTrue(pending.exists())
        self.assertFalse(list(self.root.rglob(".court-update-*.tmp")))
        updater.apply()
        self.assertEqual(foreign.read_bytes(), b"foreign temporary")
        self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"new application")
        self.assertEqual((self.root / "logos/personal.png").read_bytes(), b"personal artwork")

    def test_failed_rollback_is_reported_as_launch_blocking_and_preserves_backup(self):
        pending = self.pending()
        (pending / "tools").mkdir()
        (pending / "tools/fail.py").write_bytes(b"late failure")
        updater.seal_inventory(pending)
        replace = updater.os.replace
        installed = False
        def fail_recovery(source, target):
            nonlocal installed
            target = Path(target)
            if target == self.root / "tools/fail.py":
                raise OSError("update blocked")
            if target == self.root / "desktop/NBA2KCourtCreator.exe":
                if installed:
                    raise OSError("rollback blocked")
                installed = True
            return replace(source, target)
        with patch.object(updater.os, "replace", side_effect=fail_recovery), redirect_stderr(StringIO()):
            self.assertEqual(updater.main(["--apply"]), 3)
        self.assertIn("rollback needs attention", (self.updates / "last-error.txt").read_text())
        self.assertEqual((self.updates / "rollback/desktop/NBA2KCourtCreator.exe").read_bytes(), b"old application")
        self.assertTrue(pending.exists())
        self.assertTrue((self.updates / "apply-journal.json").exists())
        updater.apply()
        self.assertEqual((self.updates / "rollback/desktop/NBA2KCourtCreator.exe").read_bytes(), b"old application")
        self.assertFalse((self.updates / "apply-journal.json").exists())

    def crash_during_apply(self):
        self.pending()
        code = """import os, sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import updater
updater.ROOT = Path(sys.argv[2]); updater.UPDATES = updater.ROOT / 'updates'
replace = updater.os.replace
def crash(source, target):
    replace(source, target)
    if Path(target) == updater.ROOT / 'desktop/NBA2KCourtCreator.exe':
        os._exit(73)
updater.os.replace = crash
updater.apply()
"""
        process = subprocess.run([sys.executable, "-I", "-B", "-c", code, str(Path(updater.__file__).parent), str(self.root)],
                                 capture_output=True, text=True, timeout=30,
                                 creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.assertEqual(process.returncode, 73, process.stderr)
        self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"new application")
        self.assertEqual(json.loads((self.root / "package.json").read_text())["version"], "1.0.0")
        self.assertTrue((self.updates / "apply-journal.json").exists())

    def test_real_process_exit_is_recovered_before_reapplying_and_staging_does_not_mutate_it(self):
        self.crash_during_apply()
        before = self.snapshot(self.root / "desktop")
        with patch.object(updater, "fetch") as network:
            with self.assertRaises(updater.UpdateRecoveryError):
                updater.stage()
            network.assert_not_called()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        with updater.update_lock():
            updater.recover_application()
        self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"old application")
        self.assertFalse((self.updates / "apply-journal.json").exists())
        updater.apply()
        self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"new application")
        self.assertEqual((self.updates / "rollback/desktop/NBA2KCourtCreator.exe").read_bytes(), b"old application")
        self.assertEqual((self.root / "logos/personal.png").read_bytes(), b"personal artwork")

    def test_recovery_rejects_corrupt_backups_and_unrelated_target_edits_without_overwriting(self):
        self.crash_during_apply()
        backup = self.updates / "rollback/desktop/NBA2KCourtCreator.exe"
        backup.write_bytes(b"corrupted backup")
        before = self.snapshot(self.root / "desktop")
        with redirect_stderr(StringIO()):
            self.assertEqual(updater.main(["--apply"]), 3)
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        backup.write_bytes(b"old application")
        target = self.root / "desktop/NBA2KCourtCreator.exe"
        target.write_bytes(b"unrelated manual edit")
        with self.assertRaisesRegex(updater.UpdateRecoveryError, "unrelated edits"):
            updater.apply()
        self.assertEqual(target.read_bytes(), b"unrelated manual edit")
        self.assertEqual(backup.read_bytes(), b"old application")
        self.assertTrue((self.updates / "apply-journal.json").exists())

    def test_committed_interruption_finishes_cleanup_without_rolling_back_new_app(self):
        self.pending()
        write = updater.write_journal
        def interrupt(document):
            write(document)
            if document["state"] == "committed":
                raise KeyboardInterrupt("test interruption after commit")
        with patch.object(updater, "write_journal", side_effect=interrupt):
            with self.assertRaises(KeyboardInterrupt):
                updater.apply()
        before = self.snapshot(self.root / "desktop")
        self.assertTrue((self.updates / "pending").exists())
        updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"new application")
        self.assertFalse((self.updates / "pending").exists())
        self.assertFalse((self.updates / "apply-journal.json").exists())

    def test_version_metadata_cli_and_checkout_safety(self):
        self.assertEqual(updater.version("v1.2"), updater.version("1.2.0.0"))
        for value in (None, True, "vv1.2", "1.2-beta", "1", "1.2.3.4.5", "9999999.0"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                updater.version(value)
        with patch.object(updater, "stage", side_effect=updater.UpdateBusy("busy")):
            self.assertEqual(updater.main([]), 0)
        with patch.object(updater, "apply", side_effect=updater.UpdateBusy("busy")), redirect_stderr(StringIO()):
            self.assertEqual(updater.main(["--apply"]), 2)
        (self.root / ".git").write_bytes(b"managed checkout")
        with patch.object(updater, "fetch") as network, patch.object(updater, "update_lock") as lock:
            updater.stage(); updater.apply()
        network.assert_not_called(); lock.assert_not_called()
        self.assertFalse(self.updates.exists())

    def test_incomplete_native_update_and_runtime_schema_changes_are_rejected_before_writes(self):
        pending = self.pending()
        before = self.snapshot(self.root / "desktop")
        library = pending / "desktop/TwoK.Studio.dll"
        library.unlink()
        with self.assertRaisesRegex(ValueError, "incomplete"):
            updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        library.write_bytes(b"library")
        (pending / "desktop/NBA2KCourtCreator.runtimeconfig.json").write_text('{"runtimeOptions":{"frameworks":[]}}')
        with self.assertRaisesRegex(ValueError, "runtime configuration"):
            updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        (pending / "desktop/NBA2KCourtCreator.runtimeconfig.json").write_text(json.dumps(self.runtime_config()))
        (self.root / "studio-build.json").write_text(json.dumps({"runtime": "wpf-net8", "projectSchema": 2}))
        with self.assertRaisesRegex(ValueError, "different desktop runtime"):
            updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)

    def test_stale_pending_update_cannot_downgrade_installed_version(self):
        self.pending()
        (self.root / "package.json").write_text(json.dumps({"version": "1.2.0"}))
        before = self.snapshot(self.root / "desktop")
        updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        self.assertEqual(json.loads((self.root / "package.json").read_text())["version"], "1.2.0")
        self.assertFalse((self.updates / "pending").exists())

    def test_bad_pending_cannot_delete_last_valid_previous_update(self):
        pending = self.pending()
        previous = self.pending("pending.previous")
        incoming = self.pending("incoming")
        (pending / "desktop/TwoK.Studio.dll").unlink()
        before = self.snapshot(previous)
        with self.assertRaisesRegex(ValueError, "incomplete"):
            updater.publish_pending(incoming)
        self.assertEqual(self.snapshot(previous), before)
        self.assertTrue(incoming.exists())

    def test_corrupt_zip_crc_preserves_pending_and_cleans_extraction_scratch(self):
        pending = self.pending()
        before = self.snapshot(pending)
        data = bytearray(self.archive())
        start = data.index(zipfile.stringCentralDir)
        name = data.index(b"desktop/TwoK.Studio.dll", start)
        header = name - zipfile.sizeCentralDir
        crc = struct.unpack_from("<I", data, header + 16)[0]
        struct.pack_into("<I", data, header + 16, crc ^ 1)
        with self.assertRaises(zipfile.BadZipFile):
            self.stage(bytes(data))
        self.assertEqual(self.snapshot(pending), before)
        self.assertFalse(list(self.updates.glob("stage-*")))

    def test_changed_source_restores_old_installation(self):
        pending = self.pending()
        before = self.snapshot(self.root / "desktop")
        copy = updater.replace_copy
        def change_source(source, target, expected_revision=None):
            if target == self.root / "desktop/NBA2KCourtCreator.exe" and source.is_relative_to(pending):
                source.write_bytes(b"changed after snapshot")
            return copy(source, target, expected_revision)
        with patch.object(updater, "replace_copy", side_effect=change_source):
            with self.assertRaisesRegex(ValueError, "changed while"):
                updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        self.assertFalse((self.updates / "apply-journal.json").exists())

    def test_running_batch_can_update_itself_and_execute_preparsed_tail_without_launching_app(self):
        if os.name != "nt":
            self.skipTest("Windows batch replacement coverage")
        pending = self.pending()
        launcher = self.root / "Launch NBA 2K Court Creator.bat"
        (self.root / "updater.py").write_bytes(Path(updater.__file__).read_bytes())
        launcher.write_text('@echo off\n(\n"' + sys.executable + '" -I -B "' + str(self.root / "updater.py") +
                            '" --apply\nif errorlevel 1 exit /b 1\necho PREPARSED-TAIL-CONTINUED\nexit /b 0\n)\n', encoding="ascii")
        (pending / launcher.name).write_text('@echo off\nrem replacement launcher; no app launch in this fixture\n', encoding="ascii")
        updater.seal_inventory(pending)
        command = '"' + os.environ.get("ComSpec", "cmd.exe") + '" /d /s /c ""' + str(launcher) + '""'
        result = subprocess.run(command, cwd=self.root, capture_output=True, text=True, timeout=30,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.assertIn("PREPARSED-TAIL-CONTINUED", result.stdout)
        self.assertIn("replacement launcher", launcher.read_text())
        self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"new application")

    def test_failed_commit_journal_restores_old_installation(self):
        self.pending()
        before = self.snapshot(self.root / "desktop")
        write = updater.write_journal
        def fail_commit(document):
            if document["state"] == "committed":
                raise OSError("commit record unavailable")
            write(document)
        with patch.object(updater, "write_journal", side_effect=fail_commit):
            with self.assertRaisesRegex(OSError, "commit record unavailable"):
                updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        self.assertFalse((self.updates / "apply-journal.json").exists())

    def sanitized_launcher(self):
        source = (Path(updater.__file__).parent / "Launch NBA 2K Court Creator.bat").read_text()
        lines, counts = [], {"python": 0, "process": 0, "app": 0, "background": 0}
        for line in source.splitlines():
            indent = line[:len(line) - len(line.lstrip())]
            if 'set "COURT_PYTHON=%~dp0runtime\\python\\python.exe"' in line:
                line = 'set "COURT_PYTHON=' + sys.executable + '"'
                counts["python"] += 1
            elif "Get-Process NBA2KCourtCreator" in line:
                line = indent + "cmd.exe /d /c exit 1"
                counts["process"] += 1
            elif line.strip().startswith('start "" '):
                line = indent + "echo APP-LAUNCH-REQUESTED"
                counts["app"] += 1
            elif "Start-Process -FilePath" in line:
                line = indent + "echo BACKGROUND-STAGE-REQUESTED"
                counts["background"] += 1
            elif line.strip() == "pause":
                line = indent + "echo PAUSE-SUPPRESSED"
            lines.append(line)
        self.assertEqual(counts, {"python": 1, "process": 1, "app": 1, "background": 1})
        launcher = self.root / "Launch NBA 2K Court Creator.bat"
        launcher.write_text("\n".join(lines) + "\n", encoding="ascii")
        return launcher

    def run_batch(self, launcher, *, via_call=True):
        host = '"' + os.environ.get("ComSpec", "cmd.exe") + '"'
        command = host + ' /d /c call "' + str(launcher) + '"' if via_call else host + ' /d /s /c ""' + str(launcher) + '""'
        return subprocess.run(command, cwd=self.root, capture_output=True, text=True, timeout=30,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def test_actual_launcher_tail_survives_replacement_with_app_start_sanitized(self):
        if os.name != "nt":
            self.skipTest("Windows launcher branches")
        launcher = self.sanitized_launcher()
        (self.root / "updater.py").write_bytes(Path(updater.__file__).read_bytes())
        pending = self.pending()
        (pending / launcher.name).write_text('@echo off\nrem updated launcher fixture\n')
        updater.seal_inventory(pending)
        result = self.run_batch(launcher)
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        self.assertIn("APP-LAUNCH-REQUESTED", result.stdout)
        self.assertIn("BACKGROUND-STAGE-REQUESTED", result.stdout)
        self.assertIn("updated launcher fixture", launcher.read_text())

    def test_actual_launcher_blocks_busy_and_recovery_errors_but_allows_restored_old_app(self):
        if os.name != "nt":
            self.skipTest("Windows launcher branches")
        launcher = self.sanitized_launcher()
        for code in (2, 3, 1):
            (self.root / "updater.py").write_text("import sys\nsys.exit(" + str(code) + ")\n")
            result = self.run_batch(launcher)
            with self.subTest(code=code):
                self.assertEqual(result.returncode, 0 if code == 1 else 1, result.stderr + result.stdout)
                self.assertEqual("APP-LAUNCH-REQUESTED" in result.stdout, code == 1)
                self.assertEqual("BACKGROUND-STAGE-REQUESTED" in result.stdout, code == 1)
                # Direct cmd /c reports a process status, while CALL exposes the
                # batch ERRORLEVEL. Both entry forms must block the start commands.
                direct = self.run_batch(launcher, via_call=False)
                self.assertEqual("APP-LAUNCH-REQUESTED" in direct.stdout, code == 1)
                self.assertEqual("BACKGROUND-STAGE-REQUESTED" in direct.stdout, code == 1)

    def test_download_does_not_hold_install_lock_and_publication_rechecks_installed_version(self):
        self.pending()
        data = self.archive()
        url = "https://github.com" + updater.DOWNLOAD_PREFIX + "v1.1.0/court-creator-update.zip"
        release = {"tag_name": "v1.1.0", "assets": [{"name": "court-creator-update.zip", "size": len(data), "browser_download_url": url}]}
        entered, finish = threading.Event(), threading.Event()
        def download(_url, output, _limit):
            entered.set()
            if not finish.wait(5):
                raise TimeoutError("test download was not released")
            output.write(data)
            return len(data)
        with patch.object(updater, "fetch", return_value=json.dumps(release).encode()), patch.object(updater, "transfer", side_effect=download):
            with ThreadPoolExecutor(max_workers=1) as pool:
                task = pool.submit(updater.stage)
                try:
                    self.assertTrue(entered.wait(3))
                    with updater.update_lock():
                        self.assertFalse((self.updates / "apply-journal.json").exists())
                    updater.apply()
                    self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"new application")
                finally:
                    finish.set()
                task.result(timeout=5)
        self.assertEqual(json.loads((self.root / "package.json").read_text())["version"], "1.1.0")
        self.assertFalse((self.updates / "pending").exists())
        self.assertFalse(list(self.updates.glob("stage-*")))

    def old_rollback(self):
        directory = self.updates / "rollback/desktop"
        directory.mkdir(parents=True)
        (directory / "NBA2KCourtCreator.exe").write_bytes(b"last good rollback")
        return directory.parent

    def test_staged_same_size_timestamp_changes_are_rejected_before_backup_or_app_writes(self):
        pending = self.pending()
        backup = self.old_rollback()
        target = pending / "desktop/NBA2KCourtCreator.dll"
        metadata = target.stat()
        target.write_bytes(b"X" * metadata.st_size)
        os.utime(target, ns=(metadata.st_atime_ns, metadata.st_mtime_ns))
        before_app, before_backup = self.snapshot(self.root / "desktop"), self.snapshot(backup)
        with self.assertRaisesRegex(ValueError, "pending update file changed"):
            updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before_app)
        self.assertEqual(self.snapshot(backup), before_backup)
        self.assertTrue(pending.exists())
        self.assertFalse((self.updates / "apply-journal.json").exists())

    def test_missing_inventory_native_queue_is_not_adopted_and_old_app_can_still_launch(self):
        pending = self.pending()
        (pending / updater.INVENTORY_NAME).unlink()
        before = self.snapshot(self.root / "desktop")
        with self.assertRaisesRegex(ValueError, "no integrity inventory"):
            updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        if os.name == "nt":
            launcher = self.sanitized_launcher()
            (self.root / "updater.py").write_bytes(Path(updater.__file__).read_bytes())
            result = self.run_batch(launcher)
            self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
            self.assertIn("APP-LAUNCH-REQUESTED", result.stdout)
            self.assertIn("no integrity inventory", result.stderr)
        self.assertTrue(pending.exists())

    def test_inventory_schema_paths_and_fingerprints_are_strict(self):
        pending = self.pending()
        path = pending / updater.INVENTORY_NAME
        original = json.loads(path.read_text())
        cases = []
        for key, value in (("version", True), ("version", 2), ("files", []), ("files", "wrong"), ("releaseVersion", "2.0.0")):
            cases.append({**original, key: value})
        for key, value in (("path", "../updater.py"), ("path", "data/court_presets.json"), ("path", "desktop/CON"),
                           ("bytes", True), ("bytes", -1), ("bytes", updater.MAX_FILE_BYTES + 1), ("revision", "g" * 64)):
            cases.append({**original, "files": [{**original["files"][0], key: value}, *original["files"][1:]]})
        cases.append({**original, "files": [*original["files"], {**original["files"][0], "path": original["files"][0]["path"].upper()}]})
        before = self.snapshot(self.root / "desktop")
        for document in cases:
            path.write_text(json.dumps(document))
            with self.subTest(document=str(document)[:90]), self.assertRaises(ValueError):
                updater.apply()
            self.assertEqual(self.snapshot(self.root / "desktop"), before)
        path.write_text(json.dumps(original))
        with patch.object(updater, "MAX_JOURNAL_BYTES", 10):
            with self.assertRaisesRegex(ValueError, "size limit"):
                updater.apply()

    def test_redownload_replaces_unsealed_or_changed_pending_with_locally_captured_inventory(self):
        pending = self.pending()
        archive = self.archive([(updater.INVENTORY_NAME, b'{"version":999,"files":[]}')])
        before = self.snapshot(self.root / "desktop")
        for failure in ("missing", "changed"):
            with self.subTest(failure=failure):
                if failure == "missing":
                    (pending / updater.INVENTORY_NAME).unlink()
                else:
                    (pending / "desktop/TwoK.Studio.dll").write_bytes(b"changed staged DLL")
                with self.assertRaises(ValueError):
                    updater.apply()
                self.stage(archive)
                records = updater.verify_inventory(pending)
                self.assertTrue(records)
                self.assertEqual(json.loads((pending / updater.INVENTORY_NAME).read_text())["version"], 1)
                self.assertEqual(self.snapshot(self.root / "desktop"), before)
                self.assertFalse((self.updates / "pending.previous").exists())
                self.assertFalse(list(self.updates.glob("stage-*")))

    def test_added_or_missing_staged_application_files_are_rejected(self):
        pending = self.pending()
        (pending / "court_creator").mkdir()
        source = pending / "court_creator/service.py"
        source.write_bytes(b"intentionally staged service")
        updater.seal_inventory(pending)
        before = self.snapshot(self.root / "desktop")
        source.unlink()
        with self.assertRaisesRegex(ValueError, "file list changed"):
            updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        source.write_bytes(b"intentionally staged service")
        (pending / "court_creator/unexpected.py").write_bytes(b"unexpected addition")
        with self.assertRaisesRegex(ValueError, "file list changed"):
            updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)

    def test_extraction_fingerprints_are_not_recomputed_from_changed_staging_files(self):
        pending = self.pending()
        before = self.snapshot(pending)
        extract = updater.extract_archive
        def changed_after_extraction(archive, destination):
            records = extract(archive, destination)
            source = destination / "desktop/NBA2KCourtCreator.dll"
            source.write_bytes(b"X" * source.stat().st_size)
            return records
        with patch.object(updater, "extract_archive", side_effect=changed_after_extraction):
            with self.assertRaisesRegex(ValueError, "pending update file changed"):
                self.stage(self.archive())
        self.assertEqual(self.snapshot(pending), before)
        self.assertFalse(list(self.updates.glob("stage-*")))

    def test_verified_revision_is_retained_through_backup_preparation_and_copy(self):
        pending = self.pending()
        verify = updater.verify_inventory
        def change_after_verification(directory, files=None):
            records = verify(directory, files)
            (directory / "desktop/NBA2KCourtCreator.dll").write_bytes(b"changed after verification")
            return records
        before = self.snapshot(self.root / "desktop")
        with patch.object(updater, "verify_inventory", side_effect=change_after_verification):
            with self.assertRaisesRegex(ValueError, "changed while"):
                updater.apply()
        self.assertEqual(self.snapshot(self.root / "desktop"), before)
        self.assertTrue(pending.exists())
        self.assertFalse((self.updates / "apply-journal.json").exists())

    def test_failed_inventory_publication_preserves_existing_pending_and_foreign_temp(self):
        pending = self.pending()
        before = self.snapshot(pending)
        foreign = self.updates / "inventory-personal-note.tmp"
        foreign.write_bytes(b"foreign temporary file")
        replace = updater.os.replace
        def fail_inventory(source, target):
            if Path(target).name == updater.INVENTORY_NAME:
                raise OSError("inventory publish blocked")
            return replace(source, target)
        with patch.object(updater.os, "replace", side_effect=fail_inventory):
            with self.assertRaisesRegex(OSError, "inventory publish blocked"):
                self.stage(self.archive())
        self.assertEqual(self.snapshot(pending), before)
        self.assertEqual(foreign.read_bytes(), b"foreign temporary file")
        self.assertFalse(list(self.updates.glob("stage-*")))

    def test_palette_catalog_update_preserves_personal_presets_and_game_settings(self):
        data = self.root / "data"
        data.mkdir()
        (data / "team_palettes.json").write_bytes(b"old managed palette")
        (data / "court_presets.json").write_bytes(b"personal preset")
        (data / "game_installation.json").write_bytes(b"personal game location")
        (data / "update_config.json").write_bytes(b"personal update preference")
        self.stage(self.archive([("data/team_palettes.json", b"new managed palette"),
                                 ("data/palette_sources/source.json", b"managed provenance"),
                                 ("data/court_presets.json", b"must not replace"),
                                 ("data/game_installation.json", b"must not replace"),
                                 ("data/update_config.json", b"must not replace")]))
        updater.apply()
        self.assertEqual((data / "team_palettes.json").read_bytes(), b"new managed palette")
        self.assertEqual((data / "palette_sources/source.json").read_bytes(), b"managed provenance")
        self.assertEqual((data / "court_presets.json").read_bytes(), b"personal preset")
        self.assertEqual((data / "game_installation.json").read_bytes(), b"personal game location")
        self.assertEqual((data / "update_config.json").read_bytes(), b"personal update preference")
        self.assertFalse((self.root / updater.INVENTORY_NAME).exists())

    def test_implicit_directory_budget_is_checked_before_extraction(self):
        archive = self.root / "implicit-directories.zip"
        archive.write_bytes(self.archive([("tools/a/b/c/d/e/file.py", b"small deeply nested entry")]))
        destination = self.root / "implicit-extraction"
        destination.mkdir()
        with zipfile.ZipFile(archive) as contents:
            entry_limit = len(contents.infolist()) + 1
        with patch.object(updater, "MAX_ENTRIES", entry_limit):
            with self.assertRaisesRegex(ValueError, "filesystem entries"):
                updater.extract_archive(archive, destination)
        self.assertEqual(list(destination.iterdir()), [])

    def test_changed_previous_inventory_does_not_promote_an_invalid_queue(self):
        previous = self.pending("pending.previous")
        (previous / "desktop/TwoK.Studio.dll").write_bytes(b"changed previous payload")
        before = self.snapshot(previous)
        with self.assertRaisesRegex(ValueError, "pending update file changed"):
            updater.apply()
        self.assertEqual(self.snapshot(previous), before)
        self.assertFalse((self.updates / "pending").exists())
        self.assertEqual((self.root / "desktop/NBA2KCourtCreator.exe").read_bytes(), b"old application")


if __name__ == "__main__":
    unittest.main()
