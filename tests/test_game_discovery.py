from contextlib import ExitStack
import codecs
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from court_creator import court_import


class GameDiscoveryTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="court-game-discovery-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.settings = self.root / "game-settings.json"
        self.game = self.root / "known game"
        self.game.mkdir()
        (self.game / "mod.exe").write_bytes(b"game tool sentinel")
        (self.game / "manifest").write_bytes(b"manifest sentinel")
        self.pf = self.root / "program files"
        self.steam = self.pf / "Steam"
        self.vdf = self.steam / "steamapps" / "libraryfolders.vdf"
        self.vdf.parent.mkdir(parents=True)
        self.scopes = ExitStack(); self.addCleanup(self.scopes.close)
        self.scopes.enter_context(patch.object(court_import, "GAME_SETTINGS_PATH", self.settings))
        self.scopes.enter_context(patch.dict(os.environ, {"NBA2K27_ROOT": str(self.game), "ProgramFiles(x86)": str(self.pf), "ProgramFiles": str(self.root / "missing-program-files")}))
        if os.name == "nt":
            self.scopes.enter_context(patch("winreg.OpenKey", side_effect=OSError("isolated registry fixture")))

    def test_wrong_saved_settings_shapes_fall_back_without_rewriting(self):
        for value in ([], None, True, 12, "unexpected", {"nba2k27Root": []}, {"nba2k27Root": {}}):
            with self.subTest(value=value):
                self.settings.write_text(json.dumps(value), encoding="utf-8")
                before = self.settings.read_bytes(), self.settings.stat().st_mtime_ns
                self.assertEqual(court_import.find_nba2k27_root(), self.game.resolve())
                self.assertEqual((self.settings.read_bytes(), self.settings.stat().st_mtime_ns), before)

    def test_wrong_steam_root_shape_is_ignored(self):
        self.vdf.write_text('"libraryfolders" "unexpected scalar"', encoding="utf-8")
        before = self.vdf.read_bytes(), self.vdf.stat().st_mtime_ns
        with patch.dict(os.environ, {"NBA2K27_ROOT": ""}), self.assertRaises(FileNotFoundError):
            court_import.find_nba2k27_root()
        self.assertEqual((self.vdf.read_bytes(), self.vdf.stat().st_mtime_ns), before)

    def test_deep_steam_library_objects_fail_with_a_bounded_value_error(self):
        text = '"nested" {' * 1200 + '"path" "x"' + '}' * 1200
        with self.assertRaisesRegex(ValueError, "nesting"):
            court_import._parse_vdf(text)

    def test_steam_comments_do_not_remove_slashes_inside_quoted_paths(self):
        text = '// comment\n"libraryfolders" { "0" { "path" "//server/share/library" } } // tail'
        self.assertEqual(court_import._parse_vdf(text), {"libraryfolders": {"0": {"path": "//server/share/library"}}})

    def test_known_settings_priority_and_boms_skip_all_steam_work(self):
        other = self.root / "environment game"; other.mkdir()
        (other / "mod.exe").touch(); (other / "manifest").touch()
        text = json.dumps({"nba2k27Root": str(self.game), "note": "keep"})
        for payload in (text.encode("utf-8"), text.encode("utf-8-sig"), codecs.BOM_UTF16_LE + text.encode("utf-16-le"), codecs.BOM_UTF16_BE + text.encode("utf-16-be")):
            self.settings.write_bytes(payload)
            before = self.settings.stat().st_mtime_ns
            with patch.dict(os.environ, {"NBA2K27_ROOT": str(other)}), patch.object(court_import, "_read_steam_libraries", side_effect=AssertionError("Known game must not scan Steam")):
                self.assertEqual(court_import.find_nba2k27_root(), self.game.resolve())
            self.assertEqual(self.settings.read_bytes(), payload)
            self.assertEqual(self.settings.stat().st_mtime_ns, before)

    def test_bad_saved_documents_and_path_values_use_environment_without_rewriting(self):
        payloads = [b"not json", b'{"nba2k27Root":"\xff"}', b'{"unused":NaN}', b'{"unused":1e400}', b'[' * 80 + b'0' + b']' * 80,
                    b'{}' + b' ' * court_import.MAX_GAME_SETTINGS_BYTES]
        payloads.extend(json.dumps({"nba2k27Root": value}).encode("utf-8") for value in (None, True, 1, [], {}, "", " ", "\0bad", "\ud800", "x" * 32769))
        for payload in payloads:
            with self.subTest(payload=payload[:30]):
                self.settings.write_bytes(payload)
                before = self.settings.stat().st_mtime_ns
                with patch.object(court_import, "_read_steam_libraries", side_effect=AssertionError("Known environment must not scan Steam")):
                    self.assertEqual(court_import.find_nba2k27_root(), self.game.resolve())
                self.assertEqual(self.settings.read_bytes(), payload)
                self.assertEqual(self.settings.stat().st_mtime_ns, before)

    def test_explicit_selection_does_not_fall_back_or_read_metadata(self):
        with patch.object(court_import, "read_document", side_effect=AssertionError("Explicit selection must not read settings")), patch.object(court_import, "_read_steam_libraries", side_effect=AssertionError("Explicit selection must not scan Steam")):
            self.assertEqual(court_import.find_nba2k27_root(self.game), self.game.resolve())
            with self.assertRaisesRegex(FileNotFoundError, "selected folder"):
                court_import.find_nba2k27_root(self.root / "invalid selection")

    def test_modern_legacy_and_bom_steam_libraries_are_discovered(self):
        library = self.root / "library space\u00e9"; game = library / "steamapps" / "common" / "NBA 2K27"
        game.mkdir(parents=True); (game / "mod.exe").write_bytes(b"tool"); (game / "manifest").write_bytes(b"manifest")
        quoted = json.dumps(str(library), ensure_ascii=False)
        for text in ('"libraryfolders" { "0" ' + quoted + ' }', '"libraryfolders" { "0" { "path" ' + quoted + ' "apps" { "example" "1" } } }'):
            for encoding in ("utf-8", "utf-8-sig"):
                self.vdf.write_text(text, encoding=encoding)
                before = self.vdf.read_bytes(), self.vdf.stat().st_mtime_ns
                with patch.dict(os.environ, {"NBA2K27_ROOT": ""}):
                    self.assertEqual(court_import.find_nba2k27_root(), game.resolve())
                self.assertEqual((self.vdf.read_bytes(), self.vdf.stat().st_mtime_ns), before)
        self.assertEqual((game / "mod.exe").read_bytes(), b"tool")
        self.assertEqual((game / "manifest").read_bytes(), b"manifest")

    def test_bad_steam_files_preserve_default_library_fallback(self):
        game = self.steam / "steamapps" / "common" / "NBA 2K27"; game.mkdir(parents=True)
        (game / "mod.exe").touch(); (game / "manifest").touch()
        for payload in (b'"libraryfolders" "scalar"', b'"unclosed', b'\xff', b' ' * (court_import.MAX_STEAM_LIBRARIES_BYTES + 1),
                        ('"nested" {' * 1200 + '"key" "value"' + '}' * 1200).encode("utf-8")):
            self.vdf.write_bytes(payload)
            before = self.vdf.stat().st_mtime_ns
            with patch.dict(os.environ, {"NBA2K27_ROOT": ""}):
                self.assertEqual(court_import.find_nba2k27_root(), game.resolve())
            self.assertEqual(self.vdf.read_bytes(), payload)
            self.assertEqual(self.vdf.stat().st_mtime_ns, before)

    def test_steam_reader_bounds_stat_and_growth_before_tokenization(self):
        class Stream(io.BytesIO):
            def fileno(self):
                return 42
        stream = Stream(b"unused")
        with patch.object(court_import.Path, "open", return_value=stream), patch.object(court_import.os, "fstat", return_value=SimpleNamespace(st_size=court_import.MAX_STEAM_LIBRARIES_BYTES + 1)), patch.object(stream, "read") as read, patch.object(court_import, "_parse_vdf") as parse:
            with self.assertRaisesRegex(ValueError, "size limit"):
                court_import._read_steam_libraries("large.vdf")
            read.assert_not_called(); parse.assert_not_called()
        self.assertTrue(stream.closed)
        calls = []
        class GrowingStream(Stream):
            def read(self, size=-1):
                calls.append(size)
                return super().read(size)
        stream = GrowingStream(b" " * (court_import.MAX_STEAM_LIBRARIES_BYTES + 2))
        with patch.object(court_import.Path, "open", return_value=stream), patch.object(court_import.os, "fstat", return_value=SimpleNamespace(st_size=0)), patch.object(court_import, "_parse_vdf") as parse:
            with self.assertRaisesRegex(ValueError, "size limit"):
                court_import._read_steam_libraries("growing.vdf")
            parse.assert_not_called()
        self.assertEqual(calls, [court_import.MAX_STEAM_LIBRARIES_BYTES + 1]); self.assertTrue(stream.closed)

    def test_parser_depth_token_size_and_malformed_boundaries(self):
        allowed = '"nested" {' * (court_import.MAX_STEAM_LIBRARIES_DEPTH - 1) + '"key" "value"' + '}' * (court_import.MAX_STEAM_LIBRARIES_DEPTH - 1)
        self.assertIsInstance(court_import._parse_vdf(allowed), dict)
        for text, message in ((allowed.replace('"key" "value"', '"one-more" { "key" "value" }'), "nesting"),
                              ("k v " * (court_import.MAX_STEAM_LIBRARIES_TOKENS // 2 + 1), "token limit"),
                              (" " * (court_import.MAX_STEAM_LIBRARIES_BYTES + 1), "size limit"),
                              ("\u00e9" * (court_import.MAX_STEAM_LIBRARIES_BYTES // 2 + 1), "size limit")):
            with self.assertRaisesRegex(ValueError, message):
                court_import._parse_vdf(text)
        for text in ('"unfinished', '{ key value }', '"key" }', '"key"', '"key" { "value" "x"', '"key" "\\q"', '}'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                court_import._parse_vdf(text)

    def test_unreadable_saved_settings_and_unavailable_candidate_fall_back(self):
        actual_open = Path.open
        def open_settings(path, *args, **kwargs):
            if path == self.settings:
                raise PermissionError("settings lock fixture")
            return actual_open(path, *args, **kwargs)
        with patch.object(Path, "open", open_settings):
            self.assertEqual(court_import.find_nba2k27_root(), self.game.resolve())
        unavailable = self.root / "locked game"
        self.settings.write_text(json.dumps({"nba2k27Root": str(unavailable)}), encoding="utf-8")
        actual_is_file = Path.is_file
        def is_file(path):
            if path.parent == unavailable:
                raise PermissionError("unavailable candidate fixture")
            return actual_is_file(path)
        with patch.object(Path, "is_file", is_file):
            self.assertEqual(court_import.find_nba2k27_root(), self.game.resolve())

    @unittest.skipUnless(os.name == "nt", "Windows Steam registry discovery")
    def test_wrong_registry_value_types_are_ignored(self):
        from contextlib import nullcontext
        for value in (123, True, [], {}, "\0bad", "\ud800"):
            with patch("winreg.OpenKey", side_effect=lambda *_: nullcontext(object())), patch("winreg.QueryValueEx", return_value=(value, 1)), patch.dict(os.environ, {"NBA2K27_ROOT": ""}), self.assertRaises(FileNotFoundError):
                court_import.find_nba2k27_root()


if __name__ == "__main__":
    unittest.main()
