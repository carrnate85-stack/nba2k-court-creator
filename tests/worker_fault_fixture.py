"""Isolated JSON-lines worker for native transport fault injection, not production."""
import json
import os
import sys
import time
from pathlib import Path

for line in sys.stdin:
    request = json.loads(line)
    args = request["args"]
    mode = args[0]
    if mode == "exit":
        print("fixture stopped", file=sys.stderr, flush=True)
        sys.exit(7)
    if mode == "invalid-json":
        print("{broken", flush=True)
        continue
    if mode == "output-overflow":
        sys.stdout.write("x" * 32768)
        sys.stdout.flush()
        continue
    if mode == "invalid-utf8":
        sys.stdout.buffer.write(b"\xff\n")
        sys.stdout.buffer.flush()
        continue
    if mode == "closed-stdout":
        os.close(sys.stdout.fileno())
        time.sleep(2)
        sys.exit(0)
    if mode.startswith("stderr-flood"):
        for _ in range(128):
            sys.stderr.write("diagnostic" * 512)
        sys.stderr.write("tail marker")
        sys.stderr.flush()
        if mode == "stderr-flood-exit":
            sys.exit(7)
    if mode == "sleep":
        time.sleep(float(args[1]))
    response = {"id": request["id"], "result": {"args": args, "pid": os.getpid()}}
    if mode == "wrong-id":
        response["id"] += 1
    elif mode == "invalid-result":
        response["result"] = []
    elif mode == "invalid-error":
        response["error"] = {}
    elif mode == "business-error":
        response["error"] = "operation could not be completed"
    elif mode == "excessive-depth":
        nested = {}
        for _ in range(70):
            nested = {"nested": nested}
        response["result"] = nested
    elif mode == "unterminated":
        sys.stdout.write(json.dumps(response))
        sys.stdout.flush()
        sys.exit(0)
    elif mode == "wrong-encoding":
        sys.stdout.buffer.write((json.dumps(response) + "\n").encode("utf-16"))
        sys.stdout.buffer.flush()
        continue
    elif mode == "unicode":
        response["result"] = {"name": "Caf\u00e9 \U0001f3c0"}
        sys.stdout.buffer.write((json.dumps(response, ensure_ascii=False) + "\n").encode("utf-8"))
        sys.stdout.buffer.flush()
        continue
    elif mode == "render":
        path = args[-1]
        response["result"] = {"requestPath": path, "request": json.loads(Path(path).read_text())}
        if response["result"]["request"].get("fixture") == "share-request":
            alias = path + ".shared"
            os.link(path, alias)
            response["result"]["aliasPath"] = alias
    elif mode == "load-stock" and len(sys.argv) > 1:
        response["result"] = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
    print(json.dumps(response), flush=True)
