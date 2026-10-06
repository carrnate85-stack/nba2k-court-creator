import json
import io
from contextlib import ExitStack
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from court_creator import backend, service


METHODS = ("load_state", "load_stock_state", "render_preview", "sample_color", "experimental_state",
           "prepare_logo_editor", "add_custom_floor", "inspect_import_iff", "import_base_status",
           "prepare_import_base", "preview_import", "export_import_png", "export_import_iff", "export_current_iff")


def frame(value):
    return json.dumps(value).encode("utf-8") + b"\n"


class TrackedInput(io.BytesIO):
    def __init__(self, data):
        super().__init__(data)
        self.read_limits = []

    def readline(self, size=-1):
        if size <= 0:
            raise AssertionError("Worker input reads must be bounded.")
        self.read_limits.append(size)
        return super().readline(size)


class ServiceInputTests(unittest.TestCase):
    def responses(self, data):
        output = io.StringIO()
        with ExitStack() as context:
            methods = {name: context.enter_context(patch.object(backend, name, return_value={"ok": True})) for name in METHODS}
            service.main(io.BytesIO(data), output)
        return [json.loads(line) for line in output.getvalue().splitlines()], methods

    def assert_recovery(self, invalid, *, response_id=None):
        replies, methods = self.responses(invalid + frame({"id": 2, "args": ["load-stock"]}))
        self.assertEqual(len(replies), 2)
        self.assertEqual(replies[0]["id"], response_id)
        self.assertTrue(replies[0]["error"])
        self.assertEqual(replies[1], {"id": 2, "result": {"ok": True}})
        methods["load_stock_state"].assert_called_once_with()
        for name, method in methods.items():
            if name != "load_stock_state":
                method.assert_not_called()
        return replies[0]["error"]

    def test_real_worker_survives_nonobject_message_and_loads_stock(self):
        request = json.dumps({"id": 2, "args": ["load-stock"]}).encode() + b"\n"
        result = subprocess.run([sys.executable, "-B", "-m", "court_creator.service"],
                                cwd=backend.PROJECT_ROOT, input=b"[]\n" + request,
                                capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
        responses = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(len(responses), 2)
        self.assertIsNone(responses[0]["id"])
        self.assertIn("object", responses[0]["error"])
        self.assertEqual(responses[1]["id"], 2)
        self.assertTrue(responses[1]["result"]["customFloorImages"])

    def test_nonobject_and_invalid_identifiers_recover_without_backend_calls(self):
        values = [None, [], "text", 1, True, {}, {"args": ["load-stock"]}]
        values += [{"id": value, "args": ["load-stock"]} for value in (True, 1.5, "1", [], {}, 9007199254740992, -9007199254740992)]
        for value in values:
            with self.subTest(value=value):
                self.assert_recovery(frame(value))

    def test_invalid_commands_shapes_flags_and_options_do_not_reach_backend(self):
        args = [None, {}, "load-stock", [], [True], ["unknown"], ["load-stock", "extra"],
                ["load", "--template"], ["load", "--wrong", "court.psd"],
                ["render", "--wrong", "request.json"], ["render", "--request", {}],
                ["sample-color", "--layer-id", ""], ["add-floor", "--source", None],
                ["add-stock-floor", "--source", "a\0b"], ["prepare-logo-editor", "\ud800"],
                ["preview-import", "x" * (service.MAX_ARGUMENT_CHARACTERS + 1)],
                ["experimental-lines", "false"], ["experimental-lines", 1],
                ["inspect-import", "source.iff", "false"], ["inspect-import", "source.iff", False, {}],
                ["prepare-import-base", False], ["export-current-iff", ""]]
        for value in args:
            with self.subTest(args=value if not isinstance(value, list) or len(str(value)) < 200 else "long argument"):
                self.assert_recovery(frame({"id": 1, "args": value}), response_id=1)

    def test_all_supported_command_forms_still_dispatch(self):
        commands = [["load"], ["load", "--template", "court.psd"], ["load-stock"],
                    ["render", "--request", "request.json"], ["sample-color", "--layer-id", "stock-paint"],
                    ["experimental-lines", False], ["experimental-lines", True],
                    ["prepare-logo-editor", "request.json"], ["add-floor", "--source", "floor.png"],
                    ["add-stock-floor", "--source", "floor.png"], ["inspect-import", "source.iff", False],
                    ["inspect-import", "source.iff", True, None], ["inspect-import", "source.iff", False, ""],
                    ["inspect-import", "source.iff", False, "big76ers"], ["import-base-status"],
                    ["prepare-import-base"], ["prepare-import-base", ""], ["prepare-import-base", "target.iff"],
                    ["preview-import", "request.json"], ["export-import-png", "request.json"],
                    ["export-import-iff", "request.json"], ["export-current-iff", "request.json"]]
        replies, methods = self.responses(b"".join(frame({"id": index, "args": args}) for index, args in enumerate(commands)))
        self.assertEqual(replies, [{"id": index, "result": {"ok": True}} for index in range(len(commands))])
        self.assertEqual(sum(method.call_count for method in methods.values()), len(commands))
        self.assertEqual([call.args for call in methods["experimental_state"].call_args_list], [(False,), (True,)])
        self.assertEqual([call.kwargs["target"] for call in methods["inspect_import_iff"].call_args_list], [False, True, False, False])

    def test_malformed_encoding_numbers_and_depth_recover(self):
        invalid = [b"not json\n", b"\xff\n", b"\n", b'{"id":1,"args":["load-stock"],"x":NaN}\n',
                   b'{"id":1,"args":["load-stock"],"x":Infinity}\n',
                   b'{"id":1,"args":["load-stock"],"x":1e400}\n',
                   b'{"id":1,"args":["load-stock"],"x":' + b"[" * 33 + b"0" + b"]" * 33 + b"}\n",
                   '{"id":1,"args":["load-stock"]}'.encode("utf-16") + b"\n"]
        for payload in invalid:
            with self.subTest(payload=payload):
                self.assert_recovery(payload)

    def test_size_boundary_crlf_bom_and_unterminated_eof(self):
        payload = json.dumps({"id": 1, "args": ["load-stock"]}).encode()
        with patch.object(service, "MAX_MESSAGE_BYTES", len(payload)):
            for ending in (b"\n", b"\r\n"):
                replies, methods = self.responses(payload + ending)
                self.assertEqual(replies, [{"id": 1, "result": {"ok": True}}])
                methods["load_stock_state"].assert_called_once()
            self.assertIn("size limit", self.assert_recovery(payload + b" \n"))
            replies, methods = self.responses(payload)
            self.assertIn("terminator", replies[0]["error"])
            methods["load_stock_state"].assert_not_called()
        replies, _methods = self.responses(b"\xef\xbb\xbf" + payload + b"\n")
        self.assertEqual(replies, [{"id": 1, "result": {"ok": True}}])
        self.assertEqual(self.responses(b"")[0], [])

    def test_oversized_frame_is_reported_before_bounded_discard_then_recovers(self):
        request = frame({"id": 2, "args": ["load-stock"]})
        source = TrackedInput(b"x" * 4096 + b"\n" + request)
        with patch.object(service, "MAX_MESSAGE_BYTES", 128):
            messages = service._messages(source)
            data, error = next(messages)
            self.assertIsNone(data)
            self.assertIn("size limit", error)
            self.assertEqual(source.tell(), 130)
            self.assertEqual(next(messages), (request[:-1], None))
            self.assertEqual(list(messages), [])
        self.assertTrue(source.read_limits)
        self.assertLessEqual(max(source.read_limits), 130)
        with patch.object(service, "MAX_MESSAGE_BYTES", 128):
            self.assert_recovery(b"x" * 4096 + b"\n")
            replies, methods = self.responses(b"x" * 4096)
            self.assertEqual(len(replies), 1)
            self.assertIn("size limit", replies[0]["error"])
            for method in methods.values():
                method.assert_not_called()

    def test_backend_errors_are_bounded_and_bad_results_do_not_kill_worker(self):
        request = frame({"id": 1, "args": ["load-stock"]}) + frame({"id": 2, "args": ["load-stock"]})
        for first in (ValueError("x" * 100000), {"value": float("nan")}, {"value": float("inf")}):
            output = io.StringIO()
            with patch.object(backend, "load_stock_state", side_effect=[first, {"ok": True}]):
                service.main(io.BytesIO(request), output)
            replies = [json.loads(line) for line in output.getvalue().splitlines()]
            self.assertEqual(replies[0]["id"], 1)
            self.assertLessEqual(len(replies[0]["error"]), service.MAX_ERROR_CHARACTERS)
            self.assertEqual(replies[1], {"id": 2, "result": {"ok": True}})

    def test_cleanup_notes_reach_worker_error_reply_with_existing_size_bound(self):
        primary = ValueError("original export failure")
        primary.add_note("Temporary-file cleanup failed; retained C:/owned-stage.tmp: sharing lock")
        primary.add_note("x" * 100000)
        request = frame({"id": 1, "args": ["load-stock"]}) + frame({"id": 2, "args": ["load-stock"]})
        output = io.StringIO()
        with patch.object(backend, "load_stock_state", side_effect=[primary, {"ok": True}]):
            service.main(io.BytesIO(request), output)
        replies = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(replies[0]["id"], 1)
        self.assertTrue(replies[0]["error"].startswith("original export failure\n"))
        self.assertIn("retained C:/owned-stage.tmp", replies[0]["error"])
        self.assertEqual(len(replies[0]["error"]), service.MAX_ERROR_CHARACTERS)
        self.assertEqual(replies[1], {"id": 2, "result": {"ok": True}})

    def test_real_worker_recovers_after_oversized_and_invalid_utf8_frames(self):
        request = frame({"id": 2, "args": ["load-stock"]})
        payload = b"x" * (service.MAX_MESSAGE_BYTES * 2 + 17) + b"\n\xff\n" + request
        result = subprocess.run([sys.executable, "-B", "-m", "court_creator.service"],
                                cwd=backend.PROJECT_ROOT, input=payload, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
        replies = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(len(replies), 3)
        self.assertIn("size limit", replies[0]["error"])
        self.assertIn("JSON", replies[1]["error"])
        self.assertEqual(replies[2]["id"], 2)
        self.assertTrue(replies[2]["result"]["customFloorImages"])

    def test_real_worker_preserves_unicode_request_path_and_source_file(self):
        with tempfile.TemporaryDirectory(prefix="court-worker-input-") as temporary:
            path = Path(temporary) / "\u7403\u573a.json"
            path.write_bytes(b"[]")
            before = path.read_bytes(), path.stat().st_mtime_ns
            invalid_file = {"id": 1, "args": ["render", "--request", str(path)]}
            payload = json.dumps(invalid_file, ensure_ascii=False).encode("utf-8") + b"\n"
            result = subprocess.run([sys.executable, "-B", "-m", "court_creator.service"],
                                    cwd=backend.PROJECT_ROOT, input=payload + frame({"id": 2, "args": ["load-stock"]}),
                                    capture_output=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
            replies = [json.loads(line) for line in result.stdout.splitlines()]
            self.assertEqual([reply["id"] for reply in replies], [1, 2])
            self.assertIn("JSON object", replies[0]["error"])
            self.assertTrue(replies[1]["result"]["customFloorImages"])
            self.assertEqual((path.read_bytes(), path.stat().st_mtime_ns), before)


if __name__ == "__main__":
    unittest.main()
