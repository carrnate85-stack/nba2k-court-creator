"""Bound output and deadlines for local extraction and texture-encoding tools."""

import math
import os
import subprocess
import time


MAX_OUTPUT_BYTES = 16 * 1024 * 1024
TAIL_BYTES = 64 * 1024
READ_BYTES = 8192


def _diagnostic(tail):
    return bytes(tail).decode("utf-8", errors="replace")


def run_tool(args, *, timeout, cwd=None, label="External tool",
             max_output_bytes=MAX_OUTPUT_BYTES, tail_bytes=TAIL_BYTES, drain_timeout=1):
    if (isinstance(timeout, bool) or not isinstance(timeout, (int, float))
            or not 0 < timeout <= 3600 or not math.isfinite(timeout)):
        raise ValueError("External tool timeout must be finite and between 0 and 3600 seconds.")
    if (type(max_output_bytes) is not int or not 0 < max_output_bytes <= MAX_OUTPUT_BYTES
            or type(tail_bytes) is not int or not 0 < tail_bytes <= min(TAIL_BYTES, max_output_bytes)):
        raise ValueError("External tool output and diagnostic budgets are invalid.")
    if (isinstance(drain_timeout, bool) or not isinstance(drain_timeout, (int, float))
            or not 0 <= drain_timeout <= 2 or not math.isfinite(drain_timeout)):
        raise ValueError("External tool output-drain deadline must be between 0 and 2 seconds.")
    if not isinstance(args, (list, tuple)) or not args:
        raise ValueError("External tool arguments must be a nonempty argument list.")
    process = subprocess.Popen(args, cwd=cwd, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               bufsize=0, shell=False,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    tail = bytearray()
    total = 0
    deadline = time.monotonic() + timeout
    exited_at = None
    eof = False
    try:
        os.set_blocking(process.stdout.fileno(), False)
        while True:
            now = time.monotonic()
            if now >= deadline:
                raise subprocess.TimeoutExpired(args, timeout, output=_diagnostic(tail), stderr="")
            chunk = process.stdout.read(READ_BYTES) if not eof else None
            if chunk:
                total += len(chunk)
                tail.extend(chunk)
                if len(tail) > tail_bytes:
                    del tail[:-tail_bytes]
                if total > max_output_bytes:
                    raise RuntimeError(f"{label} output exceeded its {max_output_bytes}-byte safety limit. Last output: {_diagnostic(tail)[-1200:]}")
            elif chunk == b"":
                eof = True
            code = process.poll()
            if code is not None:
                if eof:
                    return subprocess.CompletedProcess(args, code, _diagnostic(tail), "")
                if exited_at is None:
                    exited_at = now
                if now - exited_at >= drain_timeout:
                    raise RuntimeError(f"{label} exited but its output pipe remained open. Last output: {_diagnostic(tail)[-1200:]}")
            if not chunk:
                time.sleep(min(.01, max(0, deadline - time.monotonic())))
    finally:
        try:
            if process.poll() is None:
                try:
                    process.kill()
                except ProcessLookupError:
                    pass
                process.wait(timeout=2)
        finally:
            process.stdout.close()
