"""Sequential JSON-lines service; decoded PSD layers stay cached between requests."""
import json
import sys
from pathlib import Path
from . import backend

def main():
    for line in sys.stdin:
        message = {}
        try:
            message = json.loads(line)
            args = message["args"]
            command = args[0]
            if command == "load":
                result = backend.load_state(Path(args[2]) if len(args) > 2 else None)
            elif command == "render":
                result = backend.render_preview(Path(args[2]))
            elif command == "sample-color":
                result = backend.sample_color(args[2])
            elif command == "add-floor":
                result = backend.add_custom_floor(Path(args[2]))
            else:
                raise ValueError("Unknown command")
            response = {"id": message["id"], "result": result}
        except Exception as error:
            response = {"id": message.get("id"), "error": str(error)}
        print(json.dumps(response), flush=True)

if __name__ == "__main__":
    main()
