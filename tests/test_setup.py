from contextlib import redirect_stdout
from io import StringIO
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from tools import setup_court_creator as installer
import updater


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix="court-setup-test-")
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        (self.root / "requirements.txt").write_text("Pillow>=10.4,<13\nShapely>=2.1,<3\n", encoding="utf-8")
        self.dotnet = {"path": "test-dotnet.exe", "runtimes": ["Microsoft.NETCore.App 8.0.30 [test]", "Microsoft.WindowsDesktop.App 8.0.30 [test]"], "sdks": ["8.0.424 [test]"]}

    def python(self, *, issues=(), pip=True, valid=True, broken=()):
        return {"version": [3, 12, 14], "bits": 64, "prefix": str(self.root / "runtime/python"),
                "executable": str(self.root / "runtime/python/python.exe"), "pipAvailable": pip,
                "packages": {"Pillow": "12.3.0", "Shapely": "2.1.2"}, "issues": list(issues),
                "requirementsValid": valid, "brokenImports": list(broken)}

    def existing_python(self, *, venv=False):
        directory = self.root / "runtime/python"
        executable = directory / ("Scripts/python.exe" if venv else "python.exe")
        executable.parent.mkdir(parents=True)
        executable.write_bytes(b"existing runtime sentinel")
        (directory / "keep-personal-runtime-file.txt").write_bytes(b"must not be removed")
        return executable

    def native(self):
        directory = self.root / "desktop"
        directory.mkdir(exist_ok=True)
        for name in installer.NATIVE_FILES:
            (directory / name).write_bytes(b"published sentinel")
        (directory / "NBA2KCourtCreator.runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {"frameworks": [
            {"name": name, "version": "8.0.0"} for name in ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")
        ]}}), encoding="utf-8")

    def sources(self):
        path = self.root / "src/NBA2KCourtCreator/NBA2KCourtCreator.csproj"
        path.parent.mkdir(parents=True)
        path.write_text("source sentinel")
        (self.root / "tools").mkdir(exist_ok=True)
        (self.root / "tools/sync_canvas_toolkit.py").write_text("source build gate sentinel")

    def snapshot(self):
        return {str(path.relative_to(self.root)): (path.read_bytes(), path.stat().st_mtime_ns)
                for path in self.root.rglob("*") if path.is_file()}

    def test_healthy_bundle_and_venv_are_reused_without_install_build_or_app_start(self):
        for venv in (False, True):
            with self.subTest(venv=venv):
                if venv:
                    (self.root / "runtime/python/python.exe").unlink()
                    path = self.root / "runtime/python/Scripts/python.exe"
                    path.parent.mkdir()
                    path.write_bytes(b"venv sentinel")
                else:
                    self.existing_python()
                self.native()
                before = self.snapshot()
                with patch.object(installer, "inspect_dotnet", return_value={**self.dotnet, "sdks": []}), patch.object(
                        installer, "probe_python", return_value=self.python()), patch.object(installer, "run") as run, patch.object(installer, "app_running") as running:
                    result = installer.setup(self.root)
                self.assertTrue(result["ready"])
                self.assertIn("Scripts" if venv else "python.exe", result["pythonPath"])
                run.assert_not_called(); running.assert_not_called()
                self.assertEqual(self.snapshot(), before)

    def test_check_only_reports_missing_runtime_without_creating_or_building(self):
        before = self.snapshot()
        with patch.object(installer, "inspect_dotnet", return_value=self.dotnet), patch.object(installer, "run") as run:
            result = installer.setup(self.root, check_only=True)
        self.assertFalse(result["ready"])
        self.assertIsNone(result["pythonPath"])
        self.assertTrue(result["nativeMissing"])
        run.assert_not_called()
        self.assertEqual(self.snapshot(), before)

    def test_nonempty_incomplete_runtime_is_never_overwritten(self):
        directory = self.root / "runtime/python"
        directory.mkdir(parents=True)
        (directory / "unknown-user-file").write_bytes(b"personal")
        before = self.snapshot()
        with patch.object(installer, "run") as run:
            with self.assertRaisesRegex(installer.SetupError, "will not be overwritten"):
                installer.setup(self.root)
        run.assert_not_called()
        self.assertEqual(self.snapshot(), before)

    def test_python_diagnostics_reject_old_32_bit_external_and_malformed_data(self):
        executable = self.existing_python()
        cases = []
        for key, value in (("version", [3, 11, 9]), ("bits", 32), ("prefix", str(self.root / "outside")),
                           ("version", [True, 12, 0]), ("issues", "wrong"), ("pipAvailable", 1),
                           ("brokenImports", ["unknown"]), ("requirementsValid", "true")):
            cases.append({**self.python(), key: value})
        cases.extend([[], None, {"version": [3, 12, 0]}])
        for data in cases:
            with self.subTest(data=data), patch.object(installer, "run", return_value=json.dumps(data)):
                with self.assertRaises(installer.SetupError):
                    installer.probe_python(executable, self.root)
        with patch.object(installer, "run", return_value="not JSON"):
            with self.assertRaisesRegex(installer.SetupError, "invalid diagnostic"):
                installer.probe_python(executable, self.root)

    def test_missing_desktop_runtime_and_sdk_stop_before_python_changes(self):
        self.sources()
        for dotnet in ({"path": None, "runtimes": [], "sdks": []},
                       {**self.dotnet, "runtimes": ["Microsoft.NETCore.App 8.0.30 [test]"]},
                       {**self.dotnet, "sdks": []}):
            before = self.snapshot()
            with self.subTest(dotnet=dotnet), patch.object(installer, "inspect_dotnet", return_value=dotnet), patch.object(installer, "run") as run:
                with self.assertRaises(installer.SetupError):
                    installer.setup(self.root)
            run.assert_not_called()
            self.assertEqual(self.snapshot(), before)

    def test_running_app_prevents_dependency_or_build_changes(self):
        self.existing_python(); self.native()
        before = self.snapshot()
        with patch.object(installer, "inspect_dotnet", return_value=self.dotnet), patch.object(
                installer, "probe_python", return_value=self.python(issues=["Shapely missing"])), patch.object(
                installer, "app_running", return_value=True), patch.object(installer, "run") as run:
            with self.assertRaisesRegex(installer.SetupError, "Close Court Creator"):
                installer.setup(self.root)
        run.assert_not_called()
        self.assertEqual(self.snapshot(), before)

    def test_missing_dependencies_use_existing_python_and_reprobe_after_install(self):
        executable = self.existing_python(); self.native()
        before = self.snapshot()
        bad = self.python(issues=["Shapely missing"])
        with patch.object(installer, "inspect_dotnet", return_value=self.dotnet), patch.object(installer, "probe_python", side_effect=[bad, bad, self.python(), self.python()]), patch.object(
                installer, "app_running", return_value=False), patch.object(installer, "run") as run:
            result = installer.setup(self.root)
        self.assertTrue(result["ready"])
        self.assertEqual(run.call_count, 1)
        command = run.call_args.args[0]
        self.assertEqual(command[:6], [executable, "-I", "-B", "-m", "pip", "install"])
        self.assertNotIn("--force-reinstall", command)
        self.assertEqual(command[-1], self.root / "requirements.txt")
        self.assertEqual(self.snapshot(), before)

    def test_missing_pip_and_broken_imports_have_explicit_repair_paths(self):
        executable = self.existing_python(); self.native()
        cases = [(self.python(issues=["verifier unavailable"], pip=False, valid=None), self.python(issues=["Shapely missing"]), False),
                 (self.python(issues=["PIL binary damaged"], broken=["PIL.Image"]), None, True)]
        for initial, after_pip, force in cases:
            sequence = [initial, initial]
            if after_pip is not None:
                sequence.append(after_pip)
            sequence.extend([self.python(), self.python()])
            with self.subTest(force=force), patch.object(installer, "inspect_dotnet", return_value=self.dotnet), patch.object(
                    installer, "probe_python", side_effect=sequence), patch.object(installer, "app_running", return_value=False), patch.object(installer, "run") as run:
                self.assertTrue(installer.setup(self.root)["ready"])
            commands = [call.args[0] for call in run.call_args_list]
            self.assertEqual(len(commands), 1 if force else 2)
            if not force:
                self.assertEqual(commands[0], [executable, "-I", "-B", "-m", "ensurepip", "--upgrade"])
            self.assertEqual("--force-reinstall" in commands[-1], force)
            self.assertFalse(any("venv" in command for command in commands))

    def test_failed_installer_preserves_existing_environment_and_does_not_build(self):
        self.existing_python(); self.sources()
        before = self.snapshot()
        with patch.object(installer, "inspect_dotnet", return_value=self.dotnet), patch.object(
                installer, "probe_python", return_value=self.python(issues=["missing"])), patch.object(
                installer, "app_running", return_value=False), patch.object(installer, "run", side_effect=installer.SetupError("download failed")) as run:
            with self.assertRaisesRegex(installer.SetupError, "download failed"):
                installer.setup(self.root)
        self.assertEqual(run.call_count, 1)
        self.assertIn("pip", run.call_args.args[0])
        self.assertEqual(self.snapshot(), before)

    def test_fresh_checkout_creates_only_empty_runtime_and_builds_without_launching(self):
        self.sources()
        commands = []
        def execute(command, *_args, **_kwargs):
            commands.append(command)
            if "venv" in command:
                self.existing_python(venv=True)
            elif "--build" in command and self.root / "tools/sync_canvas_toolkit.py" in command:
                self.native()
            else:
                self.fail("Unexpected command: " + str(command))
            return ""
        with patch.object(installer, "inspect_dotnet", return_value=self.dotnet), patch.object(
                installer, "probe_python", return_value=self.python()), patch.object(installer, "app_running", return_value=False), patch.object(installer, "run", side_effect=execute):
            self.assertTrue(installer.setup(self.root)["ready"])
        self.assertEqual(len(commands), 2)
        self.assertEqual(commands[0][-2:], ["--copies", self.root / "runtime/python"])
        self.assertEqual(commands[1][1:4], ["-I", "-B", self.root / "tools/sync_canvas_toolkit.py"])
        self.assertEqual(commands[1][-3:], ["--build", "--dotnet", self.dotnet["path"]])
        self.assertFalse(any(command[0] == self.root / "desktop/NBA2KCourtCreator.exe" for command in commands))

    def test_dotnet_checks_architecture_and_desktop_runtime_only_installation(self):
        first, second = self.root / "dotnet-x86.exe", self.root / "dotnet-x64.exe"
        first.touch(); second.touch()
        def execute(command, *_args, **_kwargs):
            if command[1] == "--info":
                return "Host:\n  Architecture: " + ("x86" if command[0] == first else "x64") + "\n"
            if command[1] == "--list-runtimes":
                return "\n".join(self.dotnet["runtimes"])
            if command[1] == "--list-sdks":
                return ""
            self.fail("Unexpected .NET command")
        with patch.object(installer, "dotnet_candidates", return_value=[first, second]), patch.object(installer, "run", side_effect=execute):
            report = installer.inspect_dotnet(self.root)
        self.assertEqual(report["path"], str(second))
        self.assertTrue(installer.has_framework(report, "Microsoft.WindowsDesktop.App"))
        self.assertEqual(report["sdks"], [])

    def test_probe_parses_real_requirement_specifiers_and_rejects_direct_urls(self):
        for content, valid, mismatch in (("Pillow>=10.4,<13\nShapely>=2.1,<3\n", True, False),
                                         ("Pillow>=999\nShapely>=2.1,<3\n", True, True),
                                         ("Pillow @ https://example.invalid/package.whl\n", False, False),
                                         ("-r another-file.txt\n", False, False)):
            path = self.root / "probe-requirements.txt"
            path.write_text(content, encoding="utf-8")
            result = subprocess.run([sys.executable, "-I", "-B", "-c", installer.PROBE, str(path)], capture_output=True,
                                    text=True, timeout=30, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0), check=True)
            report = json.loads(result.stdout)
            with self.subTest(content=content):
                self.assertEqual(report["requirementsValid"], valid)
                if mismatch:
                    self.assertTrue(any("does not satisfy" in issue for issue in report["issues"]))

    def test_probe_rejects_missing_core_constraints_oversize_and_invalid_utf8(self):
        path = self.root / "requirements.txt"
        for content in (b"", b"Pillow>=10.4\n", b"Shapely>=2.1\nPillow; python_version < '3'\n",
                        b"#" * 65537, b"\xff\xfe"):
            with self.subTest(content=content[:60]):
                path.write_bytes(content)
                result = subprocess.run([sys.executable, "-I", "-B", "-c", installer.PROBE, str(path)],
                                        capture_output=True, text=True, timeout=30, check=True,
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                report = json.loads(result.stdout)
                self.assertFalse(report["requirementsValid"])
                self.assertTrue(any("Cannot verify requirements" in issue for issue in report["issues"]))

    def test_missing_internal_package_module_is_reinstallable_not_just_missing(self):
        shadow = self.root / "shadow/PIL"
        shadow.mkdir(parents=True)
        (shadow / "__init__.py").write_text("")
        (shadow / "Image.py").write_text("raise ModuleNotFoundError('damaged Pillow internal module')")
        code = "import sys; sys.path.insert(0, sys.argv[2])\n" + installer.PROBE
        result = subprocess.run([sys.executable, "-I", "-B", "-c", code, str(self.root / "requirements.txt"), str(shadow.parent)],
                                capture_output=True, text=True, timeout=30, check=True,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        report = json.loads(result.stdout)
        self.assertTrue(report["requirementsValid"])
        self.assertIn("PIL.Image", report["brokenImports"])

    def test_invalid_requirements_and_failed_repair_never_start_a_build(self):
        executable = self.existing_python(); self.sources()
        before = self.snapshot()
        invalid = self.python(issues=["malformed constraints"], valid=False)
        with patch.object(installer, "run", return_value=json.dumps(invalid)) as run:
            with self.assertRaisesRegex(installer.SetupError, "cannot be safely verified"):
                installer.setup(self.root)
        self.assertEqual(run.call_count, 1)
        self.assertEqual(run.call_args.args[0][0], executable)
        bad = self.python(issues=["Pillow import still broken"], broken=["PIL.Image"])
        with patch.object(installer, "inspect_dotnet", return_value=self.dotnet), patch.object(
                installer, "probe_python", return_value=bad), patch.object(installer, "app_running", return_value=False), patch.object(installer, "run") as run:
            with self.assertRaisesRegex(installer.SetupError, "still failed"):
                installer.setup(self.root)
        self.assertEqual(run.call_count, 1)
        self.assertIn("pip", run.call_args.args[0])
        self.assertEqual(self.snapshot(), before)

    def test_external_runtime_and_desktop_junctions_are_preserved_without_repairs(self):
        if sys.platform != "win32":
            self.skipTest("Windows junction coverage")
        import _winapi
        with tempfile.TemporaryDirectory(prefix="court-setup-external-") as external:
            target = Path(external)
            sentinel = target / "python.exe"
            sentinel.write_bytes(b"external sentinel")
            before = sentinel.stat().st_mtime_ns
            runtime = self.root / "runtime/python"
            runtime.parent.mkdir()
            _winapi.CreateJunction(str(target), str(runtime))
            try:
                with patch.object(installer, "run") as run:
                    with self.assertRaisesRegex(installer.SetupError, "outside this project"):
                        installer.setup(self.root)
                    run.assert_not_called()
                self.assertTrue(runtime.is_junction())
            finally:
                runtime.rmdir()
            self.existing_python(); self.sources()
            desktop = self.root / "desktop"
            _winapi.CreateJunction(str(target), str(desktop))
            try:
                with patch.object(installer, "inspect_dotnet", return_value=self.dotnet), patch.object(
                        installer, "probe_python", return_value=self.python(issues=["missing package"])), patch.object(installer, "run") as run:
                    with self.assertRaisesRegex(installer.SetupError, "build path points outside"):
                        installer.setup(self.root)
                    run.assert_not_called()
                self.assertTrue(desktop.is_junction())
                self.assertEqual(sentinel.read_bytes(), b"external sentinel")
                self.assertEqual(sentinel.stat().st_mtime_ns, before)
            finally:
                desktop.rmdir()

    def test_invalid_native_configuration_requires_sources_and_sdk_without_installs(self):
        self.existing_python(); self.native()
        configuration = self.root / "desktop/NBA2KCourtCreator.runtimeconfig.json"
        for content in ("not json", "null", '{"runtimeOptions":{"frameworks":[]}}', " " * 65537):
            configuration.write_text(content)
            before = self.snapshot()
            with self.subTest(content=content[:60]), patch.object(installer, "inspect_dotnet", return_value={**self.dotnet, "sdks": []}), patch.object(
                    installer, "probe_python", return_value=self.python()), patch.object(installer, "run") as run:
                with self.assertRaisesRegex(installer.SetupError, "Restore the complete"):
                    installer.setup(self.root)
                run.assert_not_called()
            self.assertEqual(self.snapshot(), before)

    def test_release_archive_carries_setup_tools_but_no_owned_runtime_or_artwork(self):
        self.native(); self.existing_python()
        tools = self.root / "tools"
        tools.mkdir()
        for name in ("package.json", "studio-build.json", "updater.py", "Launch NBA 2K Court Creator.bat",
                     "Setup Court Creator.bat", "Build Court Creator.bat"):
            (self.root / name).write_bytes(b"root package sentinel")
        (self.root / "package.json").write_text(json.dumps({"version": "1.0.0"}))
        (self.root / "studio-build.json").write_text(json.dumps({"runtime": "wpf-net8"}))
        (self.root / "updater.py").write_bytes((installer.ROOT / "updater.py").read_bytes())
        (self.root / "data/palette_sources").mkdir(parents=True)
        (self.root / "data/team_palettes.json").write_text("[]")
        (self.root / "data/palette_sources/source.json").write_text("{}")
        (self.root / "data/court_presets.json").write_text("personal presets")
        (self.root / "data/game_installation.json").write_text("personal game settings")
        for name in ("texconv.exe", "texconv-LICENSE.txt", "court_logo_web.py", "export_2k26_court_texture.py", "setup_court_creator.py", "sync_canvas_toolkit.py"):
            (tools / name).write_bytes(b"tool sentinel")
        (self.root / "court_creator/__pycache__").mkdir(parents=True)
        (self.root / "court_creator/backend.py").write_bytes(b"backend sentinel")
        (self.root / "court_creator/__pycache__/stale.pyc").write_bytes(b"not for release")
        script = tools / "build_release.py"
        script.write_bytes((installer.ROOT / "tools/build_release.py").read_bytes())
        subprocess.run([sys.executable, "-I", "-B", str(script)], capture_output=True, timeout=30, check=True,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        with zipfile.ZipFile(self.root / "outputs/court-creator-update.zip") as archive:
            names = set(archive.namelist())
            self.assertTrue({"Setup Court Creator.bat", "Build Court Creator.bat", "tools/setup_court_creator.py",
                             "court_creator/backend.py", "desktop/NBA2KCourtCreator.exe", "data/team_palettes.json",
                             "data/palette_sources/source.json"}.issubset(names))
            self.assertNotIn("data/court_presets.json", names)
            self.assertNotIn("data/game_installation.json", names)
            self.assertFalse(any(name.startswith(("runtime/", "assets/", "templates/")) or "__pycache__" in name for name in names))

    def test_json_failure_and_process_diagnostics_do_not_install_or_launch(self):
        with patch.object(installer, "ROOT", self.root), patch.object(installer, "setup", side_effect=installer.SetupError("invalid runtime")), redirect_stdout(StringIO()) as stdout:
            self.assertEqual(installer.main(["--check-only", "--json"]), 1)
        self.assertEqual(json.loads(stdout.getvalue()), {"ready": False, "issues": ["invalid runtime"]})
        with patch.object(installer.os, "name", "nt"), patch.object(installer, "run", return_value='"NBA2KCourtCreator.exe","1234","Console","1","100 K"\n'):
            self.assertTrue(installer.app_running(self.root))
        with patch.object(installer.os, "name", "nt"), patch.object(installer, "run", return_value='INFO: No tasks are running which match the specified criteria.\n'):
            self.assertFalse(installer.app_running(self.root))

    def test_update_allowlist_includes_setup_and_preserves_user_runtime(self):
        marker = json.dumps({"runtime": "wpf-net8"})
        (self.root / "studio-build.json").write_text(marker)
        self.native(); self.existing_python()
        pending = self.root / "updates/pending"
        (pending / "desktop").mkdir(parents=True)
        (pending / "tools").mkdir()
        (pending / "studio-build.json").write_text(marker)
        (pending / "requirements.txt").write_bytes((self.root / "requirements.txt").read_bytes())
        (pending / "desktop/NBA2KCourtCreator.exe").write_text("new build")
        for name in installer.NATIVE_FILES[1:]:
            (pending / "desktop" / name).write_bytes((self.root / "desktop" / name).read_bytes())
        for name in ("Setup Court Creator.bat", "Build Court Creator.bat"):
            (pending / name).write_text("new setup/build tool")
        (pending / "tools/setup_court_creator.py").write_text("new setup helper")
        (pending / "runtime/python").mkdir(parents=True)
        (pending / "runtime/python/python.exe").write_text("must not install this runtime")
        (self.root / "package.json").write_text(json.dumps({"version": "1.0.0"}))
        (pending / "package.json").write_text(json.dumps({"version": "1.1.0"}))
        with patch.object(updater, "ROOT", self.root), patch.object(updater, "UPDATES", self.root / "updates"):
            updater.seal_inventory(pending)
            updater.apply()
        self.assertEqual((self.root / "runtime/python/python.exe").read_bytes(), b"existing runtime sentinel")
        self.assertEqual((self.root / "tools/setup_court_creator.py").read_text(), "new setup helper")
        self.assertEqual((self.root / "Setup Court Creator.bat").read_text(), "new setup/build tool")


if __name__ == "__main__":
    unittest.main()
