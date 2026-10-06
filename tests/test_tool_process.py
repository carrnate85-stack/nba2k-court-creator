import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import tracemalloc
import unittest
from unittest.mock import patch

from court_creator import tool_process


class ToolProcessTests(unittest.TestCase):
    def command(self, code, *args):
        return [sys.executable, "-B", "-c", code, *args]

    def tracked(self, children):
        actual = subprocess.Popen
        def start(*args, **kwargs):
            process = actual(*args, **kwargs)
            children.append(process)
            return process
        return start

    def assert_closed(self, children):
        self.assertTrue(children)
        self.assertTrue(all(process.poll() is not None and process.stdout.closed for process in children))

    def test_normal_merged_output_no_shell_and_stdin_is_closed(self):
        with tempfile.TemporaryDirectory(prefix="tool cwd with spaces ") as folder:
            children = []
            code = "import os,sys;print(os.getcwd());print(repr(sys.argv[1:]));print(repr(sys.stdin.buffer.read()));sys.stdout.flush();sys.stderr.write('diagnostic\\n')"
            with patch.object(tool_process.subprocess, "Popen", side_effect=self.tracked(children)) as start:
                result = tool_process.run_tool(self.command(code, "a & b", "x; y", "two words"), cwd=folder, timeout=3)
            self.assertEqual(result.returncode, 0)
            self.assertIn(str(Path(folder)), result.stdout)
            self.assertIn("['a & b', 'x; y', 'two words']", result.stdout)
            self.assertIn("b''", result.stdout)
            self.assertIn("diagnostic", result.stdout)
            self.assertEqual(result.stderr, "")
            options = start.call_args.kwargs
            self.assertFalse(options["shell"])
            self.assertEqual(options["creationflags"], getattr(subprocess, "CREATE_NO_WINDOW", 0))
            self.assertEqual(options["stdin"], subprocess.DEVNULL)
            self.assert_closed(children)

    def test_large_newline_free_output_retains_only_tail_and_last_error(self):
        children = []
        code = "import sys;sys.stdout.buffer.write(b'x'*200000);sys.stdout.flush();sys.stderr.write('FINAL ERROR');sys.stderr.flush();sys.exit(7)"
        with patch.object(tool_process.subprocess, "Popen", side_effect=self.tracked(children)):
            result = tool_process.run_tool(self.command(code), timeout=3, tail_bytes=4096)
        self.assertEqual(result.returncode, 7)
        self.assertEqual(len(result.stdout), 4096)
        self.assertTrue(result.stdout.endswith("FINAL ERROR"))
        self.assert_closed(children)

    def test_exact_output_budget_succeeds_and_one_extra_byte_is_rejected(self):
        children = []
        with patch.object(tool_process.subprocess, "Popen", side_effect=self.tracked(children)):
            result = tool_process.run_tool(self.command("import sys;sys.stdout.buffer.write(b'x'*32768)"),
                                           timeout=3, max_output_bytes=32768, tail_bytes=1024)
            self.assertEqual(result.returncode, 0)
            with self.assertRaisesRegex(RuntimeError, "output exceeded"):
                tool_process.run_tool(self.command("import sys;sys.stdout.buffer.write(b'x'*32769)"),
                                      timeout=3, max_output_bytes=32768, tail_bytes=1024)
        self.assert_closed(children)

    def test_continuous_flood_is_stopped_without_waiting_for_timeout(self):
        children = []
        code = "import sys,time\nwhile True:\n sys.stdout.buffer.write(b'flood'*2000);sys.stdout.flush();time.sleep(.001)"
        started = time.monotonic()
        with patch.object(tool_process.subprocess, "Popen", side_effect=self.tracked(children)):
            with self.assertRaisesRegex(RuntimeError, "private encoder output exceeded.*flood"):
                tool_process.run_tool(self.command(code), timeout=5, max_output_bytes=65536,
                                      tail_bytes=1024, label="private encoder")
        self.assertLess(time.monotonic() - started, 3)
        self.assert_closed(children)

    def test_quiet_timeout_preserves_last_diagnostic_and_reaps_child(self):
        children = []
        code = "import sys,time;sys.stderr.write('before-timeout');sys.stderr.flush();time.sleep(10)"
        with patch.object(tool_process.subprocess, "Popen", side_effect=self.tracked(children)):
            with self.assertRaises(subprocess.TimeoutExpired) as error:
                tool_process.run_tool(self.command(code), timeout=.4)
        self.assertEqual(error.exception.output, "before-timeout")
        self.assertEqual(error.exception.stderr, "")
        self.assert_closed(children)

    def test_eof_does_not_treat_a_still_running_child_as_success(self):
        children = []
        code = "import os,time;os.close(1);os.close(2);time.sleep(10)"
        with patch.object(tool_process.subprocess, "Popen", side_effect=self.tracked(children)):
            with self.assertRaises(subprocess.TimeoutExpired):
                tool_process.run_tool(self.command(code), timeout=.3)
        self.assert_closed(children)

    def test_fragmented_utf8_and_invalid_bytes_cannot_mask_tool_result(self):
        code = "import sys,time;sys.stdout.buffer.write(bytes([226]));sys.stdout.flush();time.sleep(.02);sys.stdout.buffer.write(bytes([130,172,255]));sys.stdout.flush()"
        result = tool_process.run_tool(self.command(code), timeout=3)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stdout, chr(0x20AC) + chr(0xFFFD))

    def test_eight_megabyte_output_has_bounded_python_allocation(self):
        tracemalloc.start()
        try:
            code = "import sys\nfor _ in range(1024):\n sys.stdout.buffer.write(b'x'*8192)"
            result = tool_process.run_tool(self.command(code), timeout=5)
            _current, peak = tracemalloc.get_traced_memory()
        finally:
            tracemalloc.stop()
        self.assertEqual(result.returncode, 0)
        self.assertEqual(len(result.stdout), tool_process.TAIL_BYTES)
        self.assertLess(peak, 1024 * 1024)

    def test_invalid_budgets_and_timeouts_fail_before_process_creation(self):
        options = [{"timeout": value} for value in (0, -1, True, float("nan"), float("inf"), 3601, 10**1000, "1")]
        options += [{"timeout": 1, "tail_bytes": value} for value in (0, -1, True, 1.5, 65537)]
        options += [{"timeout": 1, "max_output_bytes": value} for value in (0, -1, True, 1.5, tool_process.MAX_OUTPUT_BYTES + 1)]
        options += [{"timeout": 1, "drain_timeout": value} for value in (-1, True, float("nan"), 3, 10**1000)]
        with patch.object(tool_process.subprocess, "Popen", side_effect=AssertionError("Invalid input started a tool")):
            for settings in options:
                with self.subTest(settings=settings), self.assertRaises(ValueError):
                    tool_process.run_tool(self.command("pass"), **settings)
            for args in ([], "command", b"command", None):
                with self.subTest(args=args), self.assertRaises(ValueError):
                    tool_process.run_tool(args, timeout=1)

    def test_nonblocking_setup_failure_stops_child_and_closes_pipe(self):
        children = []
        with patch.object(tool_process.subprocess, "Popen", side_effect=self.tracked(children)), patch.object(
                tool_process.os, "set_blocking", side_effect=OSError("injected pipe setup failure")):
            with self.assertRaisesRegex(OSError, "pipe setup"):
                tool_process.run_tool(self.command("import time;time.sleep(10)"), timeout=1)
        self.assert_closed(children)

    def test_keyboard_interrupt_stops_child_without_a_reader_thread(self):
        children = []
        with patch.object(tool_process.subprocess, "Popen", side_effect=self.tracked(children)), patch.object(
                tool_process.time, "sleep", side_effect=KeyboardInterrupt()):
            with self.assertRaises(KeyboardInterrupt):
                tool_process.run_tool(self.command("import time;time.sleep(10)"), timeout=1)
        self.assert_closed(children)

    def test_pipe_read_failure_stops_child_and_keeps_original_error(self):
        children = []
        actual_start = self.tracked(children)
        class FailedPipe:
            def __init__(self, pipe):
                self.pipe = pipe
            def __getattr__(self, name):
                return getattr(self.pipe, name)
            def read(self, _count):
                raise OSError("injected diagnostic read failure")
        def start(*args, **kwargs):
            process = actual_start(*args, **kwargs)
            process.stdout = FailedPipe(process.stdout)
            return process
        with patch.object(tool_process.subprocess, "Popen", side_effect=start):
            with self.assertRaisesRegex(OSError, "diagnostic read failure"):
                tool_process.run_tool(self.command("import time;time.sleep(10)"), timeout=1)
        self.assert_closed(children)

    def test_exited_child_with_open_inherited_pipe_has_a_bounded_drain_deadline(self):
        reader, writer = os.pipe()
        class ExitedProcess:
            def __init__(self):
                self.stdout = os.fdopen(reader, "rb", buffering=0)
            def poll(self):
                return 0
        process = ExitedProcess()
        started = time.monotonic()
        try:
            with patch.object(tool_process.subprocess, "Popen", return_value=process):
                with self.assertRaisesRegex(RuntimeError, "output pipe remained open"):
                    tool_process.run_tool(self.command("pass"), timeout=1, drain_timeout=.05)
            self.assertLess(time.monotonic() - started, .5)
            self.assertTrue(process.stdout.closed)
        finally:
            process.stdout.close()
            os.close(writer)

    def test_missing_executable_fails_without_scratch_logs(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(FileNotFoundError):
                tool_process.run_tool([str(Path(folder) / "missing.exe")], timeout=1)
            self.assertEqual(list(Path(folder).iterdir()), [])


if __name__ == "__main__":
    unittest.main()
