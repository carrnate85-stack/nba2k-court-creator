import json
from contextlib import redirect_stderr
from io import StringIO
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import updater
from tools import sync_canvas_toolkit as sync


class CanvasSyncTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="court-canvas-sync-")
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name) / "Court Creator"
        self.root.mkdir()
        self.central = Path(self.folder.name) / "2k Texture Studio"
        self.updates = self.root / "updates"
        for name in ("package.json", "studio-build.json", "requirements.txt", "updater.py",
                     "Build Court Creator.bat", "Launch NBA 2K Court Creator.bat",
                     "tools/setup_court_creator.py", "tools/sync_canvas_toolkit.py",
                     "src/NBA2KCourtCreator/NBA2KCourtCreator.csproj", "tools/CourtStudio.Smoke/Program.cs"):
            path = self.root / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_text("fixture")
        (self.root / ".git").mkdir()
        (self.root / "desktop").mkdir()
        (self.root / "desktop/old.dll").write_bytes(b"last verified desktop")
        (self.root / "logos").mkdir()
        (self.root / "logos/personal.png").write_bytes(b"personal artwork")
        self.addCleanup(patch.stopall)
        patch.object(updater, "ROOT", self.root).start()
        patch.object(updater, "UPDATES", self.updates).start()
        patch.object(sync, "native_running", return_value=False).start()
        patch.object(sync.shutil, "which", return_value="dotnet").start()
        patch.dict(os.environ, {}, clear=True).start()
        self.calls = []

    def release(self, version="0.4.1", *, core_dependency=None, only=None, folder=None, salt=""):
        directory = self.central / "artifacts" / (folder or "published-command-v" + version)
        directory.mkdir(parents=True, exist_ok=True)
        for name, assembly in zip(sync.PACKAGES, sync.ASSEMBLIES):
            if only and name != only:
                continue
            dependency = "" if name == sync.PACKAGES[0] else f'<dependencies><group><dependency id="TwoK.Canvas.Core" version="{core_dependency or version}" /></group></dependencies>'
            with zipfile.ZipFile(directory / f"{name}.{version}.nupkg", "w") as archive:
                archive.writestr(name + ".nuspec", f'<package><metadata><id>{name}</id><version>{version}</version>{dependency}</metadata></package>')
                archive.writestr("lib/net8.0/" + assembly, (assembly + version + salt).encode())
        return directory

    def runner(self, arguments, root):
        self.calls.append(arguments)
        version = next(value.split("=", 1)[1] for value in arguments if value.startswith("-p:CanvasToolkitVersion="))
        feed = Path(next(value.split("=", 1)[1] for value in arguments if value.startswith("-p:CanvasToolkitFeed=")))
        if arguments[1] == "publish":
            destination = Path(arguments[arguments.index("-o") + 1]); destination.mkdir()
            for name in updater.NATIVE_FILES:
                (destination / name).write_bytes(b"native build")
            (destination / "NBA2KCourtCreator.deps.json").write_text(json.dumps({"libraries": {name + "/" + version: {} for name in sync.PACKAGES}}))
        else:
            destination = root / "tools/CourtStudio.Smoke/bin/Release/net8.0-windows"; destination.mkdir(parents=True, exist_ok=True)
        for name, assembly in zip(sync.PACKAGES, sync.ASSEMBLIES):
            with zipfile.ZipFile(feed / f"{name}.{version}.nupkg") as archive:
                (destination / assembly).write_bytes(archive.read("lib/net8.0/" + assembly))

    def snapshot(self):
        return sync.inventory(self.root / "desktop")

    def test_latest_matching_published_pair_is_installed_and_next_check_is_noop(self):
        self.release("0.4.1"); self.release("0.4.2")
        self.assertIn("0.4.2", sync.sync(self.root, automatic=True, runner=self.runner))
        self.assertEqual(len(self.calls), 2)
        self.assertIn("0.4.2", (self.root / "desktop/Canvas.Core.dll").read_text())
        before = self.snapshot()
        self.assertIn("already", sync.sync(self.root, automatic=True, runner=self.runner))
        self.assertEqual(len(self.calls), 2); self.assertEqual(before, self.snapshot())
        self.assertFalse(list(self.updates.glob("canvas-build-*")))
        self.assertFalse(list(self.updates.glob("canvas-backup-*")))
        self.assertEqual((self.root / "logos/personal.png").read_bytes(), b"personal artwork")

    def test_unpublished_packages_and_standalone_install_do_not_build(self):
        self.release(folder="packages")
        self.assertIn("No local", sync.sync(self.root, automatic=True, runner=self.runner))
        self.release(); (self.root / ".git").rmdir()
        self.assertIn("Standalone", sync.sync(self.root, automatic=True, runner=self.runner))
        self.assertFalse(self.calls)

    def test_incomplete_or_mismatched_release_is_rejected_without_mutation(self):
        self.release(); self.release("0.4.2", only=sync.PACKAGES[0])
        before = self.snapshot()
        with self.assertRaisesRegex(ValueError, "missing"):
            sync.sync(self.root, automatic=True, runner=self.runner)
        self.assertEqual(before, self.snapshot()); self.assertFalse(self.calls)
        self.release("0.4.2", core_dependency="0.4.1")
        with self.assertRaisesRegex(ValueError, "matching Core"):
            sync.sync(self.root, automatic=True, runner=self.runner)

    def test_gate_failure_preserves_last_build_and_removes_staging(self):
        self.release(); before = self.snapshot()
        def fail(arguments, root):
            self.runner(arguments, root)
            if arguments[1] == "run":
                raise RuntimeError("integration gate failed")
        with self.assertRaisesRegex(RuntimeError, "gate failed"):
            sync.sync(self.root, automatic=True, runner=fail)
        self.assertEqual(before, self.snapshot()); self.assertFalse(list(self.updates.glob("canvas-build-*")))

    def test_wrong_cached_assembly_is_rejected_before_tests_or_install(self):
        self.release(); before = self.snapshot()
        def stale(arguments, root):
            self.runner(arguments, root)
            if arguments[1] == "publish":
                (Path(arguments[arguments.index("-o") + 1]) / "Canvas.Core.dll").write_bytes(b"stale cache")
        with self.assertRaisesRegex(ValueError, "stale cached"):
            sync.sync(self.root, runner=stale)
        self.assertEqual(before, self.snapshot()); self.assertEqual(len(self.calls), 1)

    def test_same_version_republish_and_automatic_downgrade_are_rejected(self):
        self.release("0.4.2"); sync.sync(self.root, runner=self.runner); before = self.snapshot()
        self.release("0.4.2", salt="changed")
        with self.assertRaisesRegex(ValueError, "version bump"):
            sync.sync(self.root, runner=self.runner)
        self.assertEqual(before, self.snapshot())
        self.release("0.4.1")
        os.environ["CanvasToolkitFeed"] = str(self.central / "artifacts/published-command-v0.4.1")
        with self.assertRaisesRegex(ValueError, "downgrade"):
            sync.sync(self.root, runner=self.runner)

    def test_host_changes_and_running_process_prevent_installation(self):
        self.release(); before = self.snapshot()
        def changed(arguments, root):
            self.runner(arguments, root)
            if arguments[1] == "run":
                (root / "src/NBA2KCourtCreator/new.cs").write_text("new source")
        with self.assertRaisesRegex(ValueError, "changed during"):
            sync.sync(self.root, runner=changed)
        self.assertEqual(before, self.snapshot())
        with patch.object(sync, "native_running", return_value=True):
            with self.assertRaises(updater.UpdateBusy):
                sync.sync(self.root, runner=self.runner)
        self.assertEqual(before, self.snapshot())

    def test_directory_publication_failure_restores_previous_desktop(self):
        self.release(); before = self.snapshot(); replace = os.replace
        def denied(source, target):
            if Path(source).name == "desktop" and Path(source).parent.name.startswith("canvas-build-"):
                raise PermissionError("candidate move denied")
            return replace(source, target)
        with patch.object(sync.os, "replace", side_effect=denied):
            with self.assertRaisesRegex(PermissionError, "move denied"):
                sync.sync(self.root, runner=self.runner)
        self.assertEqual(before, self.snapshot()); self.assertFalse((self.updates / sync.JOURNAL).exists())

    def test_interrupted_install_recovers_previous_desktop_and_preserves_unknown_changes(self):
        self.updates.mkdir(); old = self.snapshot()
        backup = self.updates / ("canvas-backup-" + "a" * 32)
        os.replace(self.root / "desktop", backup)
        updater.write_metadata(self.updates / sync.JOURNAL, {"old": old, "new": {"new.dll": "hash"}, "backup": backup.name}, prefix="test-")
        sync.recover_install(); self.assertEqual(old, self.snapshot())
        updater.write_metadata(self.updates / sync.JOURNAL, {"old": old, "new": {"new.dll": "hash"}, "backup": backup.name}, prefix="test-")
        (self.root / "desktop/foreign.txt").write_text("personal")
        with self.assertRaises(updater.UpdateRecoveryError):
            sync.recover_install()
        self.assertEqual((self.root / "desktop/foreign.txt").read_text(), "personal")

    def test_explicit_private_feed_and_version_can_pin_release(self):
        self.release(); self.release("0.4.2")
        os.environ["CanvasToolkitVersion"] = "0.4.1"
        self.assertIn("0.4.1", sync.sync(self.root, runner=self.runner))
        self.assertTrue(all("-p:CanvasToolkitVersion=0.4.1" in arguments for arguments in self.calls))

    def test_automatic_recovery_runs_before_missing_source_or_feed_checks(self):
        self.updates.mkdir(); old = self.snapshot()
        backup = self.updates / ("canvas-backup-" + "b" * 32)
        os.replace(self.root / "desktop", backup)
        updater.write_metadata(self.updates / sync.JOURNAL, {"old": old, "new": {"new.dll": "hash"}, "backup": backup.name}, prefix="test-")
        (self.root / ".git").rmdir()
        self.assertIn("Standalone", sync.sync(self.root, automatic=True, runner=self.runner))
        self.assertEqual(old, self.snapshot()); self.assertFalse(self.calls)

    def test_source_changes_trigger_reverification_without_package_changes(self):
        self.release(); sync.sync(self.root, runner=self.runner)
        (self.root / "src/NBA2KCourtCreator/new.cs").write_text("new source")
        sync.sync(self.root, runner=self.runner); self.assertEqual(len(self.calls), 4)

    def test_setup_sdk_path_is_used_even_when_dotnet_is_not_on_path(self):
        self.release()
        with patch.object(sync.shutil, "which", return_value=None):
            sync.sync(self.root, runner=self.runner, dotnet_path="verified-x64-dotnet.exe")
        self.assertTrue(all(arguments[0] == "verified-x64-dotnet.exe" for arguments in self.calls))

    def test_busy_and_recovery_exit_codes_prevent_launch_during_installation(self):
        for error, code in ((updater.UpdateBusy("busy"), 2), (updater.UpdateRecoveryError("recovery"), 3), (ValueError("build failed"), 1)):
            with patch.object(sync, "sync", side_effect=error), redirect_stderr(StringIO()):
                self.assertEqual(sync.main(["--auto"]), code)


if __name__ == "__main__":
    unittest.main()
