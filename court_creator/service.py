"""Sequential JSON-lines service; decoded PSD layers stay cached between requests."""
import json
import sys
from pathlib import Path
from . import backend
from .json_io import _parse_document


MAX_MESSAGE_BYTES = 1024 * 1024
MAX_ARGUMENT_CHARACTERS = 32768
MAX_ERROR_CHARACTERS = 4096
_ARGUMENT_COUNTS = {
    "load": (1, 3), "load-stock": (1,), "render": (3,),
    "sample-color": (3,), "experimental-lines": (2,),
    "prepare-logo-editor": (2,), "add-floor": (3,), "add-stock-floor": (3,),
    "inspect-import": (3, 4), "import-base-status": (1,),
    "prepare-import-base": (1, 2), "preview-import": (2,),
    "export-import-png": (2,), "export-import-iff": (2,), "export-current-iff": (2,),
}
_FLAGS = {"load": "--template", "render": "--request", "sample-color": "--layer-id",
          "add-floor": "--source", "add-stock-floor": "--source"}


def _messages(stream):
    while line := stream.readline(MAX_MESSAGE_BYTES + 2):
        content = line[:-1] if line.endswith(b"\n") else line
        if content.endswith(b"\r"):
            content = content[:-1]
        if len(content) > MAX_MESSAGE_BYTES:
            yield None, "Worker request exceeds the 1 MiB size limit."
            # Discard only this frame in bounded chunks before accepting the next request.
            while not line.endswith(b"\n"):
                line = stream.readline(min(8192, MAX_MESSAGE_BYTES + 2))
                if not line:
                    return
            continue
        if not line.endswith(b"\n"):
            yield None, "Worker request ended before its newline terminator."
            return
        yield content, None


def _response_id(message):
    value = message.get("id") if isinstance(message, dict) else None
    return value if type(value) is int and abs(value) <= 9007199254740991 else None


def _arguments(message):
    if not isinstance(message, dict):
        raise ValueError("Worker request must be a JSON object.")
    if _response_id(message) is None:
        raise ValueError("Worker request id must be a safe integer.")
    args = message.get("args")
    if not isinstance(args, list) or not args or not isinstance(args[0], str):
        raise ValueError("Worker request args must be a nonempty command array.")
    command = args[0]
    if command not in _ARGUMENT_COUNTS:
        raise ValueError("Unknown command")
    if len(args) not in _ARGUMENT_COUNTS[command]:
        raise ValueError("Incorrect argument count for " + command + ".")
    if command in _FLAGS and len(args) > 1 and args[1] != _FLAGS[command]:
        raise ValueError("Incorrect argument flag for " + command + ".")
    for index, value in enumerate(args[1:], 1):
        if (command == "experimental-lines" and index == 1) or (command == "inspect-import" and index == 2):
            if type(value) is not bool:
                raise ValueError("The " + command + " option must be a boolean.")
            continue
        if command == "inspect-import" and index == 3 and value is None:
            continue
        allow_empty = (command == "prepare-import-base" and index == 1) or (command == "inspect-import" and index == 3)
        if not isinstance(value, str) or (not value and not allow_empty):
            raise ValueError("The " + command + " argument must be text.")
        if len(value) > MAX_ARGUMENT_CHARACTERS or "\0" in value:
            raise ValueError("The " + command + " argument is too long or contains a null character.")
        try:
            value.encode("utf-8")
        except UnicodeError as error:
            raise ValueError("The " + command + " argument contains invalid Unicode.") from error
    return args


def main(input_stream=None, output_stream=None):
    input_stream = sys.stdin.buffer if input_stream is None else input_stream
    output_stream = sys.stdout if output_stream is None else output_stream
    for line, framing_error in _messages(input_stream):
        request_id = None
        try:
            if framing_error:
                raise ValueError(framing_error)
            message = _parse_document(line, encoding="utf-8-sig")
            request_id = _response_id(message)
            args = _arguments(message)
            command = args[0]
            if command == "load":
                result = backend.load_state(Path(args[2]) if len(args) > 2 else None)
            elif command == "load-stock":
                result = backend.load_stock_state()
            elif command == "render":
                result = backend.render_preview(Path(args[2]))
            elif command == "sample-color":
                result = backend.sample_color(args[2])
            elif command == "experimental-lines":
                result = backend.experimental_state(bool(args[1]))
            elif command == "prepare-logo-editor":
                result = backend.prepare_logo_editor(Path(args[1]))
            elif command == "add-floor":
                result = backend.add_custom_floor(Path(args[2]))
            elif command == "add-stock-floor":
                result = backend.add_custom_floor(Path(args[2]), native=True)
            elif command == "inspect-import":
                result = backend.inspect_import_iff(Path(args[1]), target=bool(args[2]),
                                                    selected=args[3] if len(args) > 3 else None)
            elif command == "import-base-status":
                result = backend.import_base_status()
            elif command == "prepare-import-base":
                result = backend.prepare_import_base(Path(args[1]) if len(args) > 1 and args[1] else None)
            elif command == "preview-import":
                result = backend.preview_import(Path(args[1]))
            elif command == "export-import-png":
                result = backend.export_import_png(Path(args[1]))
            elif command == "export-import-iff":
                result = backend.export_import_iff(Path(args[1]))
            elif command == "export-current-iff":
                result = backend.export_current_iff(Path(args[1]))
            else:
                raise ValueError("Unknown command")
            serialized = json.dumps({"id": request_id, "result": result}, allow_nan=False)
        except Exception as error:
            text = str(error)[:MAX_ERROR_CHARACTERS]
            for note in getattr(error, "__notes__", ()):
                remaining = MAX_ERROR_CHARACTERS - len(text) - 1
                if remaining <= 0:
                    break
                text += "\n" + str(note)[:remaining]
            serialized = json.dumps({"id": request_id, "error": text})
        print(serialized, file=output_stream, flush=True)

if __name__ == "__main__":
    main()
