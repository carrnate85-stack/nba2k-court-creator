import csv
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch
from zipfile import ZIP_DEFLATED, ZipFile

from court_creator import court_import
from tools.export_2k26_court_texture import make_dds


def game_fixture(root):
    game = root / "game folder"
    game.mkdir()
    (game / "mod.exe").touch()
    (game / "0P").write_bytes(b"stock archive, never changed")
    (game / "shared pack").write_bytes(b"shared archive, never changed")
    with (game / "manifest").open("w", encoding="utf-8", newline="") as stream:
        csv.writer(stream).writerows([
            [court_import.NBA2K27_BASE_ENTRY, "0P", "0", "16"],
            ["shared/texture.tld", "shared pack", "0", "12"],
        ])
    live = game / "mods" / court_import.NBA2K27_BASE_ENTRY
    live.parent.mkdir(parents=True)
    live.write_bytes(b"user floor mod, never changed")
    return game, live


def write_floor(path, *, supported_scene=True):
    path.parent.mkdir(parents=True, exist_ok=True)
    scene = {"level_floor": {"Model": {"floor": {"Prim": [
        {"Mesh": "NBA_full_court_floor_lowShape" if supported_scene else "unknown_surface", "Material": "area"},
        {"Mesh": "NBA_line_three_point_lowShape", "Material": "lines"},
    ]}}}}
    block = struct.pack("<HHI", 0xF800, 0xF800, 0)
    dds = make_dds(8192, 4096, "DXT1", block * (8192 * 4096 // 16))
    with ZipFile(path, "w", compression=ZIP_DEFLATED) as archive:
        archive.writestr("bigcourt.dds", dds)
        archive.writestr("level_floor.SCNE", json.dumps(scene))


class StockExtractionTests(unittest.TestCase):
    def test_preparation_is_isolated_atomic_and_cached(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            game, live = game_fixture(root)
            original_manifest = (game / "manifest").read_bytes()
            base = root / "cache" / "base.iff"
            scratch = []

            def expand(args, **options):
                work = Path(options["cwd"])
                scratch.append(work)
                self.assertNotEqual(work.resolve(), game.resolve())
                self.assertEqual(args, [str(game / "mod.exe"), court_import.NBA2K27_BASE_ENTRY])
                self.assertEqual(options["timeout"], 180)
                self.assertEqual(options["label"], "NBA 2K27 extractor")
                with (work / "manifest").open(encoding="utf-8", newline="") as stream:
                    entries = list(csv.reader(stream))
                self.assertEqual(entries, [[court_import.NBA2K27_BASE_ENTRY, str(game / "0P"), "0", "16"],
                                          ["shared/texture.tld", str(game / "shared pack"), "0", "12"]])
                self.assertFalse(base.exists())
                self.assertFalse(base.with_name(base.name + ".held-loose").exists())
                self.assertFalse(base.with_name(base.name + ".extraction.json").exists())
                write_floor(work / "mods" / court_import.NBA2K27_BASE_ENTRY)
                return subprocess.CompletedProcess(args, 0, "expanded")

            with patch.object(court_import, "GAME_SETTINGS_PATH", root / "settings.json"), patch.object(court_import, "run_tool", side_effect=expand) as run:
                result = court_import.prepare_2k27_base(base, game)
                second = court_import.prepare_2k27_base(base)
                self.assertEqual(run.call_count, 1)
            self.assertEqual(result["selected"], "bigcourt.dds")
            self.assertEqual(result, second)
            self.assertEqual(Path(result["path"]), base)
            self.assertEqual(live.read_bytes(), b"user floor mod, never changed")
            self.assertEqual((game / "manifest").read_bytes(), original_manifest)
            self.assertEqual((game / "0P").read_bytes(), b"stock archive, never changed")
            self.assertFalse(any(path.exists() for path in scratch))
            self.assertFalse(list(base.parent.glob("*.tmp")))
            with ZipFile(base) as archive:
                self.assertEqual(archive.comment, court_import.BASE_VERSION)
                scene, _ = court_import._scene_document(archive.read("level_floor.SCNE"))
                self.assertEqual(len(scene["level_floor"]["Model"]["floor"]["Prim"]), 1)

    def test_bad_scene_preserves_previous_cache_and_game_mod(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            game, live = game_fixture(root)
            base = root / "cache" / "base.iff"
            base.parent.mkdir()
            base.write_bytes(b"previous incomplete cache")
            scratch = []

            def expand(args, **options):
                work = Path(options["cwd"])
                scratch.append(work)
                write_floor(work / "mods" / court_import.NBA2K27_BASE_ENTRY, supported_scene=False)
                return subprocess.CompletedProcess(args, 0, "expanded")

            with patch.object(court_import, "GAME_SETTINGS_PATH", root / "settings.json"), patch.object(court_import, "run_tool", side_effect=expand):
                with self.assertRaisesRegex(ValueError, "full-court floor surface"):
                    court_import.prepare_2k27_base(base, game)
            self.assertEqual(base.read_bytes(), b"previous incomplete cache")
            self.assertEqual(live.read_bytes(), b"user floor mod, never changed")
            self.assertFalse(any(path.exists() for path in scratch))
            self.assertFalse(list(base.parent.glob("*.tmp")))

    def test_retry_discards_only_its_scratch_output(self):
        with tempfile.TemporaryDirectory() as folder:
            game, live = game_fixture(Path(folder))
            scratch = []

            def expand(args, **options):
                work = Path(options["cwd"])
                scratch.append(work)
                output = work / "mods" / court_import.NBA2K27_BASE_ENTRY
                self.assertFalse(output.exists())
                output.parent.mkdir(parents=True, exist_ok=True)
                if len(scratch) == 1:
                    output.write_bytes(b"incomplete extraction")
                    return subprocess.CompletedProcess(args, 1, "first attempt failed")
                with ZipFile(output, "w") as archive:
                    archive.writestr("fixture", b"stock")
                return subprocess.CompletedProcess(args, 0, "expanded")

            with patch.object(court_import, "run_tool", side_effect=expand):
                with court_import.extracted_stock_floor(game) as archive:
                    self.assertTrue(archive.is_file())
                    with ZipFile(archive) as opened:
                        self.assertEqual(opened.read("fixture"), b"stock")
            self.assertEqual(scratch[0], scratch[1])
            self.assertFalse(scratch[0].exists())
            self.assertEqual(live.read_bytes(), b"user floor mod, never changed")

    def test_timeout_and_keyboard_interrupt_cleanup_scratch(self):
        for failure in (subprocess.TimeoutExpired("mod.exe", 180), KeyboardInterrupt(),
                        RuntimeError("NBA 2K27 extractor output exceeded its safety limit"),
                        OSError("private diagnostic pipe failure")):
            with self.subTest(failure=type(failure).__name__), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                game, live = game_fixture(root)
                base = root / "cache" / "base.iff"
                scratch = []

                def expand(args, **options):
                    work = Path(options["cwd"])
                    scratch.append(work)
                    output = work / "mods" / court_import.NBA2K27_BASE_ENTRY
                    output.parent.mkdir(parents=True)
                    output.write_bytes(b"partial output")
                    raise failure

                with patch.object(court_import, "GAME_SETTINGS_PATH", root / "settings.json"), patch.object(court_import, "run_tool", side_effect=expand):
                    with self.assertRaises(type(failure)):
                        court_import.prepare_2k27_base(base, game)
                self.assertFalse(base.exists())
                self.assertFalse(scratch[0].exists())
                self.assertFalse(list(base.parent.glob("*.tmp")))
                self.assertEqual(live.read_bytes(), b"user floor mod, never changed")

    def test_manifest_rejection_never_starts_extraction(self):
        for entries in ([], [[court_import.NBA2K27_BASE_ENTRY, "0P", "-1", "12"]],
                        [[court_import.NBA2K27_BASE_ENTRY, "0P", "0", "0"]],
                        [[court_import.NBA2K27_BASE_ENTRY, "0P", "0", "12"]] * 2):
            with tempfile.TemporaryDirectory() as folder:
                game, live = game_fixture(Path(folder))
                with (game / "manifest").open("w", encoding="utf-8", newline="") as stream:
                    csv.writer(stream).writerows(entries)
                with patch.object(court_import, "run_tool") as run:
                    with self.assertRaises(ValueError):
                        with court_import.extracted_stock_floor(game):
                            self.fail("Invalid manifest was accepted")
                    run.assert_not_called()
                self.assertEqual(live.read_bytes(), b"user floor mod, never changed")

    def test_runner_failure_retains_previous_cache_and_installed_game_inputs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            game, live = game_fixture(root)
            base = root / "cache" / "base.iff"
            base.parent.mkdir()
            base.write_bytes(b"previous incomplete cached floor")
            watched = (base, live, game / "mod.exe", game / "manifest", game / "0P")
            before = {path: (path.read_bytes(), path.stat().st_mtime_ns) for path in watched}
            scratch = []
            def fail(args, **options):
                temporary = Path(options["cwd"])
                scratch.append(temporary)
                partial = temporary / "mods" / court_import.NBA2K27_BASE_ENTRY
                partial.parent.mkdir(parents=True)
                partial.write_bytes(b"partial private extraction")
                raise RuntimeError("NBA 2K27 extractor output exceeded its safety limit")
            with patch.object(court_import, "GAME_SETTINGS_PATH", root / "settings.json"), patch.object(
                    court_import, "run_tool", side_effect=fail):
                with self.assertRaisesRegex(RuntimeError, "output exceeded"):
                    court_import.prepare_2k27_base(base, game)
            self.assertEqual({path: (path.read_bytes(), path.stat().st_mtime_ns) for path in watched}, before)
            self.assertTrue(scratch)
            self.assertTrue(all(not directory.exists() for directory in scratch))
            self.assertFalse(list(base.parent.glob("*.tmp")))

    def test_repeated_manifest_archives_are_resolved_only_once(self):
        with tempfile.TemporaryDirectory() as folder:
            game, _ = game_fixture(Path(folder))
            with (game / "manifest").open("a", encoding="utf-8", newline="") as stream:
                csv.writer(stream).writerows([[f"shared/repeated-{index}.tld", "shared pack", "0", "12"] for index in range(2000)])
            calls = []
            resolve = Path.resolve

            def tracked(path, *args, **options):
                if path in (game / "0P", game / "shared pack"):
                    calls.append(path)
                return resolve(path, *args, **options)

            def expand(args, **options):
                output = Path(options["cwd"]) / "mods" / court_import.NBA2K27_BASE_ENTRY
                output.parent.mkdir(parents=True)
                with ZipFile(output, "w") as archive:
                    archive.writestr("fixture", b"stock")
                return subprocess.CompletedProcess(args, 0, "expanded")

            with patch.object(Path, "resolve", tracked), patch.object(court_import, "run_tool", side_effect=expand):
                with court_import.extracted_stock_floor(game):
                    pass
            self.assertEqual(calls.count(game / "0P"), 1)
            self.assertEqual(calls.count(game / "shared pack"), 1)

    def test_simultaneous_preparation_uses_one_extraction(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            game, live = game_fixture(root)
            base = root / "cache" / "base.iff"

            def expand(args, **options):
                time.sleep(.03)
                write_floor(Path(options["cwd"]) / "mods" / court_import.NBA2K27_BASE_ENTRY)
                return subprocess.CompletedProcess(args, 0, "expanded")

            with patch.object(court_import, "GAME_SETTINGS_PATH", root / "settings.json"), patch.object(court_import, "run_tool", side_effect=expand) as run:
                with ThreadPoolExecutor(max_workers=2) as executor:
                    results = list(executor.map(lambda _: court_import.prepare_2k27_base(base, game), range(2)))
            self.assertEqual(results[0], results[1])
            self.assertEqual(run.call_count, 1)
            self.assertEqual(live.read_bytes(), b"user floor mod, never changed")

    def test_malformed_legacy_recovery_preserves_all_files(self):
        for state in ([], {"gameRoot": ".", "hadLoose": False}, {"gameRoot": "/game", "hadLoose": "false"}):
            with tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                base = root / "base.iff"
                backup = root / "base.iff.held-loose"
                backup.write_bytes(b"original user mod")
                journal = root / "base.iff.extraction.json"
                journal.write_text(json.dumps(state), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "backup has been preserved"):
                    court_import._recover_base_extraction(base)
                self.assertEqual(backup.read_bytes(), b"original user mod")
                self.assertEqual(json.loads(journal.read_text()), state)

    def test_unavailable_legacy_game_does_not_create_a_phantom_mod_folder(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            base = root / "base.iff"
            backup = root / "base.iff.held-loose"
            backup.write_bytes(b"original user mod")
            missing_game = root / "uninstalled game"
            journal = root / "base.iff.extraction.json"
            journal.write_text(json.dumps({"gameRoot": str(missing_game), "hadLoose": True}), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "game folder is unavailable"):
                court_import._recover_base_extraction(base)
            self.assertFalse(missing_game.exists())
            self.assertEqual(backup.read_bytes(), b"original user mod")
            self.assertTrue(journal.is_file())


if __name__ == "__main__":
    unittest.main()
