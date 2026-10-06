"""Native snapshot capture fixture; outputs JSON markers, never PNG or IFF art."""
import json
import sys
from pathlib import Path

for line in sys.stdin:
    envelope = json.loads(line)
    args = envelope["args"]
    if args[0] not in ("render", "export-current-iff"):
        raise ValueError("Unexpected snapshot fixture command")
    project = json.loads(Path(args[-1]).read_text(encoding="utf-8"))
    Path(project["outputPath"]).write_text(json.dumps(project), encoding="utf-8")
    print(json.dumps({"id": envelope["id"], "result": {"captured": True}}), flush=True)
